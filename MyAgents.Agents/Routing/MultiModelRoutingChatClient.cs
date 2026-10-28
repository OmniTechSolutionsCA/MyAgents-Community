using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace MyAgents.Agents;

/// <summary>
/// Adaptive multi-model routing and token/cost optimizing chat client.
/// Routes turns between Economy, Standard, and Advanced models based on prompt complexity,
/// cascades to fallback models on failure, and tracks tokens and dollar spend against budgets.
/// </summary>
public sealed class MultiModelRoutingChatClient : IChatClient, IDisposable
{
    private readonly ModelCandidate[] _candidates;
    private readonly MultiModelRoutingOptions _options;
    private readonly ITokenCostTelemetrySink? _telemetrySink;
    private readonly ConcurrentDictionary<string, SessionSpendTracker> _sessionSpends = new(StringComparer.OrdinalIgnoreCase);

    public MultiModelRoutingChatClient(
        IEnumerable<ModelCandidate> candidates,
        MultiModelRoutingOptions? options = null,
        ITokenCostTelemetrySink? telemetrySink = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _candidates = candidates.ToArray();
        if (_candidates.Length == 0)
        {
            throw new ArgumentException("At least one model candidate must be registered.", nameof(candidates));
        }

        _options = options ?? new MultiModelRoutingOptions();
        _telemetrySink = telemetrySink;
    }

    /// <inheritdoc />
    private readonly ChatClientMetadata _metadata = new("MyAgents.MultiModelRouter");
    public ChatClientMetadata Metadata => _metadata;

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(MultiModelRoutingChatClient)) return this;
        if (serviceType == typeof(IReadOnlyList<ModelCandidate>)) return _candidates;
        return null;
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var sessionId = ExtractSessionId(options);

        // 1. Pre-flight session quota check
        CheckSessionQuotaBeforeTurn(sessionId);

        // 2. Select model candidate
        var candidate = SelectCandidate(messageList, options);
        var stopwatch = Stopwatch.StartNew();
        var operationId = Guid.NewGuid().ToString("N");
        var fallbackTriggered = false;
        string? fallbackReason = null;

        ChatResponse response;
        try
        {
            response = await candidate.Client.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_options.EnableFallbackCascade)
        {
            var fallback = FindFallbackCandidate(candidate);
            if (fallback is null) throw;

            fallbackTriggered = true;
            fallbackReason = ex.Message;
            candidate = fallback;
            response = await candidate.Client.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();

        // 3. Compute token and cost metrics
        var inputTokens = response.Usage?.InputTokenCount ?? EstimateTokens(string.Join(" ", messageList.Select(m => m.Text ?? string.Empty)));
        var outputTokens = response.Usage?.OutputTokenCount ?? EstimateTokens(string.Join(" ", response.Messages.Select(m => m.Text ?? string.Empty)));
        var totalTokens = response.Usage?.TotalTokenCount ?? (inputTokens + outputTokens);
        var costUsd = candidate.Pricing?.CalculateCost(inputTokens, outputTokens) ?? 0m;

        // 4. Validate turn quotas
        ValidateTurnQuotas(sessionId, totalTokens, costUsd);

        // 5. Update session quotas
        UpdateSessionSpend(sessionId, totalTokens, costUsd);

        // 6. Record telemetry
        if (_telemetrySink is not null)
        {
            var metrics = new ModelRoutingCostMetrics(
                OperationId: operationId,
                SessionId: sessionId,
                SelectedModelId: candidate.ModelId,
                SelectedTier: candidate.Tier,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                TotalTokens: totalTokens,
                EstimatedCostUsd: costUsd,
                Duration: stopwatch.Elapsed,
                FallbackTriggered: fallbackTriggered,
                FallbackReason: fallbackReason,
                Timestamp: DateTimeOffset.UtcNow);

            await _telemetrySink.RecordMetricsAsync(metrics, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var sessionId = ExtractSessionId(options);

        CheckSessionQuotaBeforeTurn(sessionId);

        var candidate = SelectCandidate(messageList, options);
        var stopwatch = Stopwatch.StartNew();
        var operationId = Guid.NewGuid().ToString("N");
        var outputTextAccumulator = new System.Text.StringBuilder();

        await using var enumerator = candidate.Client.GetStreamingResponseAsync(messageList, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            var update = enumerator.Current;
            if (!string.IsNullOrEmpty(update.Text))
            {
                outputTextAccumulator.Append(update.Text);
            }
            yield return update;
        }

        stopwatch.Stop();

        var inputTokens = EstimateTokens(string.Join(" ", messageList.Select(m => m.Text ?? string.Empty)));
        var outputTokens = EstimateTokens(outputTextAccumulator.ToString());
        var totalTokens = inputTokens + outputTokens;
        var costUsd = candidate.Pricing?.CalculateCost(inputTokens, outputTokens) ?? 0m;

        UpdateSessionSpend(sessionId, totalTokens, costUsd);

        if (_telemetrySink is not null)
        {
            var metrics = new ModelRoutingCostMetrics(
                OperationId: operationId,
                SessionId: sessionId,
                SelectedModelId: candidate.ModelId,
                SelectedTier: candidate.Tier,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                TotalTokens: totalTokens,
                EstimatedCostUsd: costUsd,
                Duration: stopwatch.Elapsed,
                FallbackTriggered: false,
                Timestamp: DateTimeOffset.UtcNow);

            await _telemetrySink.RecordMetricsAsync(metrics, cancellationToken).ConfigureAwait(false);
        }
    }

    public ModelCandidate SelectCandidate(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        if (_options.CustomSelector is not null)
        {
            var custom = _options.CustomSelector(messages, options, _candidates);
            if (custom is not null) return custom;
        }

        if (options?.AdditionalProperties is not null)
        {
            if (options.AdditionalProperties.TryGetValue("PreferredModel", out var pm) && pm is string modelIdStr)
            {
                var match = _candidates.FirstOrDefault(c => string.Equals(c.ModelId, modelIdStr, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }

            if (options.AdditionalProperties.TryGetValue("PreferredTier", out var pt) && pt is ModelTier tier)
            {
                var match = _candidates.FirstOrDefault(c => c.Tier == tier && !c.IsFallback);
                if (match is not null) return match;
            }
        }

        var text = string.Join(" ", messages.Select(m => m.Text ?? string.Empty));
        var lower = text.ToLowerInvariant();

        // High complexity heuristics
        if (text.Length > 2500 ||
            lower.Contains("architecture") ||
            lower.Contains("formal proof") ||
            lower.Contains("distributed consensus") ||
            lower.Contains("concurrency") ||
            lower.Contains("cryptographic"))
        {
            var advanced = _candidates.FirstOrDefault(c => c.Tier == ModelTier.Advanced && !c.IsFallback);
            if (advanced is not null) return advanced;
        }

        // Low complexity heuristics
        if (text.Length < 300 && (
            lower.Contains("format") ||
            lower.Contains("convert") ||
            lower.Contains("extract") ||
            lower.Contains("json") ||
            lower.Contains("summarize in one word") ||
            lower.Contains("translate")))
        {
            var economy = _candidates.FirstOrDefault(c => c.Tier == ModelTier.Economy && !c.IsFallback);
            if (economy is not null) return economy;
        }

        // Standard or default fallback
        return _candidates.FirstOrDefault(c => c.Tier == ModelTier.Standard && !c.IsFallback)
            ?? _candidates.FirstOrDefault(c => c.Tier == ModelTier.Economy && !c.IsFallback)
            ?? _candidates.FirstOrDefault(c => !c.IsFallback)
            ?? _candidates[0];
    }

    private ModelCandidate? FindFallbackCandidate(ModelCandidate current)
    {
        var explicitFallback = _candidates.FirstOrDefault(c => c.IsFallback && !string.Equals(c.ModelId, current.ModelId, StringComparison.OrdinalIgnoreCase));
        if (explicitFallback is not null) return explicitFallback;

        var nextTier = _candidates.FirstOrDefault(c => c.Tier > current.Tier && !string.Equals(c.ModelId, current.ModelId, StringComparison.OrdinalIgnoreCase));
        if (nextTier is not null) return nextTier;

        return _candidates.FirstOrDefault(c => !string.Equals(c.ModelId, current.ModelId, StringComparison.OrdinalIgnoreCase));
    }

    private void CheckSessionQuotaBeforeTurn(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        if (_sessionSpends.TryGetValue(sessionId, out var spend))
        {
            if (_options.MaxTokensPerSession.HasValue && spend.TotalTokens >= _options.MaxTokensPerSession.Value)
            {
                throw new TokenBudgetQuotaException(
                    $"Session token quota of {_options.MaxTokensPerSession.Value} tokens exceeded (current: {spend.TotalTokens}).",
                    sessionId,
                    _options.MaxTokensPerSession.Value,
                    spend.TotalTokens);
            }

            if (_options.MaxCostPerSessionUsd.HasValue && spend.TotalCostUsd >= _options.MaxCostPerSessionUsd.Value)
            {
                throw new TokenBudgetQuotaException(
                    $"Session cost quota of ${_options.MaxCostPerSessionUsd.Value:F4} exceeded (current: ${spend.TotalCostUsd:F4}).",
                    sessionId,
                    _options.MaxCostPerSessionUsd.Value,
                    spend.TotalCostUsd);
            }
        }
    }

    private void ValidateTurnQuotas(string? sessionId, long turnTokens, decimal turnCostUsd)
    {
        if (_options.MaxTokensPerTurn.HasValue && turnTokens > _options.MaxTokensPerTurn.Value)
        {
            throw new TokenBudgetQuotaException(
                $"Turn token quota of {_options.MaxTokensPerTurn.Value} tokens exceeded (consumed: {turnTokens}).",
                sessionId,
                _options.MaxTokensPerTurn.Value,
                turnTokens);
        }

        if (_options.MaxCostPerTurnUsd.HasValue && turnCostUsd > _options.MaxCostPerTurnUsd.Value)
        {
            throw new TokenBudgetQuotaException(
                $"Turn cost quota of ${_options.MaxCostPerTurnUsd.Value:F4} exceeded (consumed: ${turnCostUsd:F4}).",
                sessionId,
                _options.MaxCostPerTurnUsd.Value,
                turnCostUsd);
        }
    }

    private void UpdateSessionSpend(string? sessionId, long tokens, decimal cost)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        var tracker = _sessionSpends.GetOrAdd(sessionId, _ => new SessionSpendTracker());
        tracker.Add(tokens, cost);
    }

    private static string? ExtractSessionId(ChatOptions? options)
    {
        if (options?.AdditionalProperties is null) return null;

        if (options.AdditionalProperties.TryGetValue("SessionId", out var sid) && sid is string s1)
            return s1;
        if (options.AdditionalProperties.TryGetValue("session_id", out var sid2) && sid2 is string s2)
            return s2;

        return null;
    }

    private static long EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Math.Max(1, (long)Math.Ceiling(text.Length / 4.0));
    }

    public void Dispose()
    {
        foreach (var candidate in _candidates)
        {
            candidate.Client.Dispose();
        }
    }

    private sealed class SessionSpendTracker
    {
        private long _tokens;
        private decimal _cost;
        private readonly Lock _gate = new();

        public long TotalTokens
        {
            get { lock (_gate) return _tokens; }
        }

        public decimal TotalCostUsd
        {
            get { lock (_gate) return _cost; }
        }

        public void Add(long tokens, decimal cost)
        {
            lock (_gate)
            {
                _tokens += tokens;
                _cost += cost;
            }
        }
    }
}
