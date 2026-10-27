using System.Text.Json;
using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Feature that enables strongly-typed structured output for an agent, configuring
/// the agent's schema and providing self-correction retries if the response is invalid JSON.
/// </summary>
/// <typeparam name="T">The target structured type.</typeparam>
public sealed class StructuredOutputFeature<T> : IAgentFeature
{
    private readonly int _maxValidationRetries;
    private readonly JsonSerializerOptions? _jsonSerializerOptions;
    private readonly string? _schemaName;
    private readonly string? _schemaDescription;
    private readonly bool? _strict;

    /// <summary>
    /// Creates a <see cref="StructuredOutputFeature{T}"/>.
    /// </summary>
    /// <param name="maxValidationRetries">Maximum self-correction retries when JSON validation fails.</param>
    /// <param name="jsonSerializerOptions">Optional JSON serializer options.</param>
    /// <param name="schemaName">Optional schema name.</param>
    /// <param name="schemaDescription">Optional schema description.</param>
    /// <param name="strict">Whether strict schema matching is enforced.</param>
    public StructuredOutputFeature(
        int maxValidationRetries = 2,
        JsonSerializerOptions? jsonSerializerOptions = null,
        string? schemaName = null,
        string? schemaDescription = null,
        bool? strict = true)
    {
        if (maxValidationRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxValidationRetries), "Max validation retries cannot be negative.");

        _maxValidationRetries = maxValidationRetries;
        _jsonSerializerOptions = jsonSerializerOptions;
        _schemaName = schemaName;
        _schemaDescription = schemaDescription;
        _strict = strict;
    }

    /// <inheritdoc />
    public void Contribute(AgentAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var format = AgentResponseFormat.ForType<T>(
            _schemaName,
            _schemaDescription,
            _jsonSerializerOptions,
            _strict);

        assembly.Pipeline.Use(new StructuredOutputMiddleware<T>(
            format,
            _maxValidationRetries,
            _jsonSerializerOptions));

        assembly.Capabilities.Add(AgentCapabilities.StructuredOutput);
    }
}

/// <summary>
/// Middleware that sets the <see cref="AgentResponseFormat"/> on each turn and validates/retries
/// if the model response does not conform to <typeparamref name="T"/>.
/// </summary>
public sealed class StructuredOutputMiddleware<T> : IAgentMiddleware
{
    private readonly AgentResponseFormat _format;
    private readonly int _maxValidationRetries;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new <see cref="StructuredOutputMiddleware{T}"/>.
    /// </summary>
    public StructuredOutputMiddleware(
        AgentResponseFormat format,
        int maxValidationRetries = 2,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _maxValidationRetries = maxValidationRetries;
        _jsonOptions = jsonSerializerOptions ?? JsonSerializerOptions.Default;
    }

    /// <inheritdoc />
    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        context.Capabilities.Set(_format);

        var currentContext = context;
        for (var attempt = 0; attempt <= _maxValidationRetries; attempt++)
        {
            var result = await next(currentContext, cancellationToken).ConfigureAwait(false);
            var assistantMessage = result.Output.LastOrDefault(static m =>
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));

            if (assistantMessage is null || string.IsNullOrWhiteSpace(assistantMessage.Content))
            {
                if (attempt == _maxValidationRetries)
                    return result;

                currentContext = currentContext with
                {
                    Messages = currentContext.Messages
                        .Concat(result.Output)
                        .Append(new AgentMessage("user", "The response was empty. Please provide the required structured JSON response."))
                        .ToArray()
                };
                continue;
            }

            var content = assistantMessage.Content.Trim();
            if (content.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                content = content["```json".Length..];
                if (content.EndsWith("```", StringComparison.Ordinal))
                    content = content[..^3];
                content = content.Trim();
            }
            else if (content.StartsWith("```", StringComparison.Ordinal))
            {
                content = content[3..];
                if (content.EndsWith("```", StringComparison.Ordinal))
                    content = content[..^3];
                content = content.Trim();
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<T>(content, _jsonOptions);
                if (parsed is not null)
                {
                    return result;
                }
            }
            catch (JsonException ex)
            {
                if (attempt == _maxValidationRetries)
                    throw new InvalidOperationException(
                        $"The model failed to produce valid JSON for '{typeof(T).Name}' after {_maxValidationRetries + 1} attempt(s). Last output: {content}", ex);

                currentContext = currentContext with
                {
                    Messages = currentContext.Messages
                        .Concat(result.Output)
                        .Append(new AgentMessage("user", $"The JSON output was invalid: {ex.Message}. Please correct the JSON to match the expected schema."))
                        .ToArray()
                };
            }
        }

        return await next(currentContext, cancellationToken).ConfigureAwait(false);
    }
}
