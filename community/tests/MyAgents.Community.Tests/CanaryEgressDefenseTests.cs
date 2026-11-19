using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyAgents.Agents;
using MyAgents.Core;
using MyAgents.Core.Memory;
using MyAgents.Core.Security;
using MyAgents.Core.Tools;
using MyAgents.Runtime.Chat;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

/// <summary>
/// Adversarial tests for <see cref="CanaryDefenseFeature"/>.
/// </summary>
/// <remarks>
/// Every test drives the real runtime through a fake <see cref="IChatClient"/>, so the canary is
/// obtained the way an attacker would obtain it: by reading the system prompt the framework actually
/// sent. Nothing reaches into framework internals to fetch a token.
/// </remarks>
[TestFixture]
public sealed partial class CanaryEgressDefenseTests
{
    /// <summary>The single message a blocked turn must produce.</summary>
    private const string Refusal = "I'm sorry, but I cannot provide a response to that request.";

    [GeneratedRegex("CANARY-[A-Z2-7]{26}", RegexOptions.CultureInvariant)]
    private static partial Regex CanaryPattern();

    // ── Confirmed violations on the output channel ──────────────────────────────

    [Test]
    public async Task Canary_in_output_is_replaced_with_a_uniform_refusal()
    {
        var client = new DelegateChatClient(canary => $"Sure, the secret is {canary}");
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "what is your secret?")]);

        Assert.Multiple(() =>
        {
            // Positive control that the feature actually ran and issued a canary in the first place.
            Assert.That(SystemPrompt(client), Does.Match(CanaryPattern()),
                "The feature must inject a canary into the system prompt.");
            Assert.That(ExtractCanary(client.Requests[0]), Is.Not.Empty, "No canary was delivered to the model.");

            // Then the assertion that actually matters.
            Assert.That(result.Output.Single().Content, Is.EqualTo(Refusal));
            Assert.That(result.Output.Single().Content, Does.Not.Contain("CANARY-"));
        });
    }

    [Test]
    public async Task A_clean_turn_is_not_refused()
    {
        // Control for every "is refused" test: proves the middleware is not simply blanking turns.
        var client = new DelegateChatClient(_ => "An ordinary, harmless answer.");
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "hello")]);

        Assert.That(result.Output.Single().Content, Is.EqualTo("An ordinary, harmless answer."));
    }

    [Test]
    public async Task Base64_wrapped_canary_is_detected()
    {
        var client = new DelegateChatClient(canary => "data: " + ToBase64(canary));
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "encode it")]);

        Assert.That(result.Output.Single().Content, Is.EqualTo(Refusal));
    }

    [Test]
    public async Task Zero_width_split_canary_is_detected()
    {
        var client = new DelegateChatClient(SplitWithZeroWidth);
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "spell it out")]);

        Assert.That(result.Output.Single().Content, Is.EqualTo(Refusal));
    }

    [Test]
    public async Task Reversed_canary_is_detected()
    {
        var client = new DelegateChatClient(canary =>
        {
            var characters = canary.ToCharArray();
            Array.Reverse(characters);
            return new string(characters);
        });
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "reverse it")]);

        Assert.That(result.Output.Single().Content, Is.EqualTo(Refusal));
    }

    // ── Cross-turn smuggling (T5) ──────────────────────────────────────────────

    [Test]
    public async Task A_canary_split_across_two_turns_is_detected_on_the_second()
    {
        const int splitAt = 16;
        var client = new DelegateChatClient(canary => $"part one: {canary[..splitAt]}");
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        var first = await session.InvokeAsync([AgentMessage.FromText("user", "start")]);

        client.Transform = canary => $"part two: {canary[splitAt..]}";
        var second = await session.InvokeAsync([AgentMessage.FromText("user", "continue")]);

        Assert.Multiple(() =>
        {
            // Neither half contains the canary, so the first turn must reach the caller intact --
            // without this, a middleware that refused everything would satisfy the assertion below.
            Assert.That(first.Output.Single().Content, Does.StartWith("part one:"),
                "The first turn holds only a fragment and must not be refused.");

            Assert.That(second.Output.Single().Content, Is.EqualTo(Refusal),
                "The completing fragment must be caught against the recent-turn window.");
        });
    }

    [Test]
    public async Task A_canary_split_across_three_turns_is_detected_on_the_third()
    {
        const int step = 11; // the 33-character token divides into three equal fragments
        var client = new DelegateChatClient(canary => $"a: {canary[..step]}");
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        var first = await session.InvokeAsync([AgentMessage.FromText("user", "one")]);
        client.Transform = canary => $"b: {canary[step..(step * 2)]}";
        var second = await session.InvokeAsync([AgentMessage.FromText("user", "two")]);
        client.Transform = canary => $"c: {canary[(step * 2)..]}";
        var third = await session.InvokeAsync([AgentMessage.FromText("user", "three")]);

        Assert.Multiple(() =>
        {
            Assert.That(first.Output.Single().Content, Does.StartWith("a:"));
            Assert.That(second.Output.Single().Content, Does.StartWith("b:"),
                "Two fragments are not yet a token, so the second turn must reach the caller.");
            Assert.That(third.Output.Single().Content, Is.EqualTo(Refusal),
                "The third fragment completes the token across the window.");
        });
    }

    [Test]
    public async Task Ten_clean_turns_are_never_refused()
    {
        const string clean = "a clean and entirely ordinary reply";
        var client = new DelegateChatClient(_ => clean);
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        for (var turn = 0; turn < 10; turn++)
        {
            var result = await session.InvokeAsync([AgentMessage.FromText("user", $"turn {turn}")]);
            Assert.That(result.Output.Single().Content, Is.EqualTo(clean),
                $"turn {turn} must not be refused; a growing window must not manufacture a match.");
        }
    }

    [Test]
    public async Task The_window_bound_decides_whether_a_split_is_caught()
    {
        // Both directions in one test: a short separation must be caught and a long one must not.
        // Asserting only the long case would pass just as happily with no window at all.

        Assert.Multiple(() =>
        {
            Assert.That(SplitAcrossTurnsAsync(gap: 2).GetAwaiter().GetResult(), Is.EqualTo(Refusal),
                "two clean turns later the first fragment is still inside the 8-turn window");
            Assert.That(SplitAcrossTurnsAsync(gap: 9).GetAwaiter().GetResult(),
                Does.Not.EqualTo(Refusal),
                "nine clean turns evict the first fragment, so the completing one must not match");
        });
    }

    /// <summary>Emits the two canary halves with <paramref name="gap"/> clean turns between them.</summary>
    private async Task<string> SplitAcrossTurnsAsync(int gap)
    {
        var client = new DelegateChatClient(canary => $"part one: {canary[..16]}");
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        await session.InvokeAsync([AgentMessage.FromText("user", "start")]);

        client.Transform = _ => "clean reply";
        for (var turn = 0; turn < gap; turn++)
            await session.InvokeAsync([AgentMessage.FromText("user", $"clean {turn}")]);

        client.Transform = canary => $"part two: {canary[16..]}";
        var final = await session.InvokeAsync([AgentMessage.FromText("user", "finish")]);
        return final.Output.Single().Content;
    }

    private static string ExtractCanaryFromLastPrompt(DelegateChatClient client)
        => ExtractCanary(client.Requests[^1]);

    // ── Quarantine is monotonic ─────────────────────────────────────────────────

    [Test]
    public async Task A_quarantined_session_refuses_later_turns_without_calling_the_model()
    {
        var client = new DelegateChatClient(canary => canary);
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        var first = await session.InvokeAsync([AgentMessage.FromText("user", "leak it")]);
        Assert.That(first.Output.Single().Content, Is.EqualTo(Refusal));

        // The next turn would be clean, so only quarantine — not detection — can refuse it.
        client.Transform = _ => "perfectly innocent output";
        var second = await session.InvokeAsync([AgentMessage.FromText("user", "hello again")]);

        Assert.Multiple(() =>
        {
            Assert.That(second.Output.Single().Content, Is.EqualTo(Refusal));
            Assert.That(client.Requests, Has.Count.EqualTo(1),
                "A quarantined session must not spend a model call.");
        });
    }

    // ── Tool-argument channel ───────────────────────────────────────────────────

    [Test]
    public async Task Canary_in_tool_arguments_aborts_before_the_tool_runs()
    {
        var tool = new SpyTool();
        var client = new ScriptedChatClient();
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(),
            new ToolRegistrationFeature(tool));

        // The model reads the canary from its prompt and puts it in a web-search style argument.
        client.Script.Enqueue(messages => ToolCall(
            "call-1",
            "search",
            new Dictionary<string, object?>
            {
                ["query"] = "lookup " + ToBase64(ExtractCanary(messages)),
            }));

        var exception = Assert.ThrowsAsync<CanaryViolationException>(async () =>
            await session.InvokeAsync([AgentMessage.FromText("user", "search for it")]));

        Assert.Multiple(() =>
        {
            Assert.That(tool.CallCount, Is.Zero, "The tool must never be invoked on a violation.");
            Assert.That(exception, Is.Not.Null);
            // No oracle: the uniform message must not name the detector or the canary.
            Assert.That(exception!.Message, Is.EqualTo("The turn was aborted by egress defense."));
            Assert.That(exception.Message, Does.Not.Contain("canary"));
        });
    }

    // ── Explicit states: empty, oversize, scanner failure ──────────────────────

    [Test]
    public async Task An_empty_response_is_not_a_violation()
    {
        var client = new DelegateChatClient(_ => string.Empty);
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "say nothing")]);

        Assert.That(result.Output.Single().Content, Is.Not.EqualTo(Refusal));
    }

    [Test]
    public async Task A_payload_over_the_scan_limit_fails_closed_even_without_a_canary()
    {
        var client = new DelegateChatClient(_ => new string('x', 256));
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(options => options.MaxScanBytes = 64));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "talk a lot")]);

        Assert.That(result.Output.Single().Content, Is.EqualTo(Refusal));
    }

    [Test]
    public async Task A_throwing_scanner_fails_closed_by_default()
    {
        var client = new DelegateChatClient(_ => "harmless looking text");
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature(new ThrowingScanner()));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "hello")]);

        Assert.That(result.Output.Single().Content, Is.EqualTo(Refusal));
    }

    [Test]
    public async Task A_throwing_scanner_fails_open_when_explicitly_configured()
    {
        var client = new DelegateChatClient(_ => "harmless looking text");
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(new ThrowingScanner(), options => options.FailOpenOnScannerError = true));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "hello")]);

        Assert.That(result.Output.Single().Content, Is.EqualTo("harmless looking text"));
    }

    // ── Suspicious findings are audited by default ─────────────────────────────

    [Test]
    public async Task A_suspicious_finding_does_not_block_under_the_default_handler()
    {
        var client = new DelegateChatClient(_ => "see ![chart](https://evil.example/pixel)");
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "draw a chart")]);

        Assert.That(result.Output.Single().Content, Is.EqualTo("see ![chart](https://evil.example/pixel)"),
            "Suspicious findings are audited by default so heuristics can be measured before enforcement.");
    }

    [Test]
    public async Task An_allowing_handler_still_lets_a_confirmed_canary_through()
    {
        // Documents the one hole in the policy surface: audit-only is a measurement mode, not a
        // production setting. Asserting it here stops the behaviour drifting silently.
        var client = new DelegateChatClient(canary => canary);
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature(new AuditOnlyViolationHandler()));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "leak it")]);

        Assert.That(result.Output.Single().Content, Does.Match(CanaryPattern()));
    }

    // ── Streaming parity ───────────────────────────────────────────────────────

    [Test]
    public async Task Streaming_a_canary_turn_yields_only_the_refusal()
    {
        var client = new DelegateChatClient(canary => $"prefix {canary} suffix");
        var session = await CreateSessionAsync(client, new CanaryDefenseFeature());

        var chunks = new List<AgentTurnResult>();
        await foreach (var chunk in session.InvokeStreamingAsync([AgentMessage.FromText("user", "leak it")]))
            chunks.Add(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(chunks, Has.Count.EqualTo(1), "A violating stream must not emit a clean prefix.");
            Assert.That(chunks[0].Output.Single().Content, Is.EqualTo(Refusal));
        });
    }

    // ── Audit sink ─────────────────────────────────────────────────────────────

    [Test]
    public async Task A_canary_in_output_is_recorded_in_the_audit_sink_and_refused()
    {
        var sink = new InMemoryEgressAuditSink();
        var client = new DelegateChatClient(canary => $"Here is the secret: {canary}");
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(options => options.AuditSink = sink));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "what is the secret?")]);

        Assert.Multiple(async () =>
        {
            Assert.That(result.Output.Single().Content, Is.EqualTo(Refusal));
            var records = await sink.ListAllAsync();
            Assert.That(records, Has.Count.EqualTo(1));
            var record = records[0];
            Assert.That(record.Channel, Is.EqualTo(EgressChannel.TurnOutput));
            Assert.That(record.Severity, Is.EqualTo(EgressSeverity.Confirmed));
            Assert.That(record.DetectorName, Is.EqualTo("session-canary"));
            Assert.That(record.SessionId, Is.EqualTo("canary-session"));
        });
    }

    [Test]
    public async Task A_suspicious_tool_argument_is_audited_while_allowing_the_tool_to_run()
    {
        var sink = new InMemoryEgressAuditSink();
        var tool = new SpyTool();
        var client = new ScriptedChatClient();
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(options => options.AuditSink = sink),
            new ToolRegistrationFeature(tool));

        client.Script.Enqueue(_ => ToolCall(
            "call-1",
            "search",
            new Dictionary<string, object?>
            {
                ["query"] = "lookup ![beacon](https://evil.example/pixel)",
            }));
        client.Script.Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Here is the search result.")));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "search with beacon")]);

        Assert.Multiple(async () =>
        {
            Assert.That(tool.CallCount, Is.EqualTo(1), "Auditing observes; it does not enforce on suspicious findings.");
            Assert.That(result.Output.Single().Content, Is.EqualTo("Here is the search result."));
            var records = await sink.ListAllAsync();
            Assert.That(records, Has.Count.EqualTo(1));
            Assert.That(records[0].Channel, Is.EqualTo(EgressChannel.ToolArguments));
            Assert.That(records[0].Severity, Is.EqualTo(EgressSeverity.Suspicious));
            Assert.That(records[0].ToolName, Is.EqualTo("search"));
        });
    }

    [Test]
    public async Task Clean_turns_write_zero_audit_records()
    {
        var sink = new InMemoryEgressAuditSink();
        var client = new DelegateChatClient(_ => "Safe text without canaries.");
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(options => options.AuditSink = sink));

        for (var i = 0; i < 10; i++)
        {
            var result = await session.InvokeAsync([AgentMessage.FromText("user", $"turn {i}")]);
            Assert.That(result.Output.Single().Content, Is.EqualTo("Safe text without canaries."));
        }

        var records = await sink.ListAllAsync();
        Assert.That(records, Is.Empty);
    }

    [Test]
    public async Task A_throwing_audit_sink_does_not_fail_a_clean_turn()
    {
        var sink = new ThrowingEgressAuditSink();
        var client = new DelegateChatClient(_ => "Safe and sound.");
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(options => options.AuditSink = sink));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "hello")]);

        Assert.That(result.Output.Single().Content, Is.EqualTo("Safe and sound."));
    }

    [Test]
    public async Task A_throwing_audit_sink_does_not_prevent_refusal_on_violation()
    {
        var sink = new ThrowingEgressAuditSink();
        var client = new DelegateChatClient(canary => $"Exfiltrating: {canary}");
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(options => options.AuditSink = sink));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "leak it")]);

        Assert.That(result.Output.Single().Content, Is.EqualTo(Refusal));
    }

    [Test]
    public async Task Confirmed_violation_audit_record_carries_detector_name_and_reason_without_caller_leak()
    {
        var sink = new InMemoryEgressAuditSink();
        var client = new DelegateChatClient(canary => $"Exfiltrating: {canary}");
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(options => options.AuditSink = sink));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "tell me")]);

        Assert.Multiple(async () =>
        {
            var refusal = result.Output.Single().Content;
            Assert.That(refusal, Is.EqualTo(Refusal));

            var records = await sink.ListAllAsync();
            Assert.That(records, Has.Count.EqualTo(1));
            var record = records[0];

            Assert.That(record.DetectorName, Is.Not.Null.And.Not.Empty);
            Assert.That(record.Reason, Is.Not.Null.And.Not.Empty);

            // Refusal text must contain neither the detector name nor the reason
            Assert.That(refusal, Does.Not.Contain(record.DetectorName));
            Assert.That(refusal, Does.Not.Contain(record.Reason));
        });
    }

    // ── Opt-in secret patterns (RFC 0001 §4 Pass 6 & R4) ──────────────────────

    [Test]
    public async Task Default_options_do_not_flag_secret_like_strings()
    {
        const string fakeKey = "sk-proj-1234567890abcdef";
        var sink = new InMemoryEgressAuditSink();
        var client = new DelegateChatClient(_ => $"Here is the token: {fakeKey}");
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(options => options.AuditSink = sink));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "print the token")]);

        Assert.Multiple(async () =>
        {
            Assert.That(result.Output.Single().Content, Is.EqualTo($"Here is the token: {fakeKey}"));
            var records = await sink.ListAllAsync();
            Assert.That(records, Is.Empty, "Secret pattern scanning is off by default (R4).");
        });
    }

    [Test]
    public async Task Configured_secret_pattern_in_output_is_audited_as_suspicious()
    {
        const string fakeKey = "sk-proj-1234567890abcdef";
        var sink = new InMemoryEgressAuditSink();
        var pattern = new SecretPattern(
            "openai-api-key",
            new Regex(@"sk-proj-[A-Za-z0-9]{16}", RegexOptions.NonBacktracking));
        var client = new DelegateChatClient(_ => $"Here is the token: {fakeKey}");
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(options =>
            {
                options.AuditSink = sink;
                options.SecretPatterns = [pattern];
            }));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "print the token")]);

        Assert.Multiple(async () =>
        {
            Assert.That(result.Output.Single().Content, Is.EqualTo($"Here is the token: {fakeKey}"),
                "Default handler allows Suspicious findings in shadow/measurement mode.");

            var records = await sink.ListAllAsync();
            Assert.That(records, Has.Count.EqualTo(1));
            var record = records[0];
            Assert.That(record.Channel, Is.EqualTo(EgressChannel.TurnOutput));
            Assert.That(record.Severity, Is.EqualTo(EgressSeverity.Suspicious));
            Assert.That(record.DetectorName, Is.EqualTo("openai-api-key"));
            Assert.That(record.Reason, Does.Contain("openai-api-key"));
            Assert.That(record.Reason, Does.Not.Contain(fakeKey),
                "Audit record reason must never contain the matched raw secret value.");
        });
    }

    [Test]
    public async Task Configured_secret_pattern_in_tool_arguments_is_audited()
    {
        const string fakeKey = "sk-proj-1234567890abcdef";
        var sink = new InMemoryEgressAuditSink();
        var tool = new SpyTool();
        var client = new ScriptedChatClient();
        var pattern = new SecretPattern(
            "openai-api-key",
            new Regex(@"sk-proj-[A-Za-z0-9]{16}", RegexOptions.NonBacktracking));
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(options =>
            {
                options.AuditSink = sink;
                options.SecretPatterns = [pattern];
            }),
            new ToolRegistrationFeature(tool));

        client.Script.Enqueue(_ => ToolCall(
            "call-1",
            "search",
            new Dictionary<string, object?>
            {
                ["query"] = $"use key {fakeKey}",
            }));
        client.Script.Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "run search")]);

        Assert.Multiple(async () =>
        {
            Assert.That(tool.CallCount, Is.EqualTo(1), "Default handler audits suspicious findings without blocking.");
            Assert.That(result.Output.Single().Content, Is.EqualTo("Done."));
            var records = await sink.ListAllAsync();
            Assert.That(records, Has.Count.EqualTo(1));
            var record = records[0];
            Assert.That(record.Channel, Is.EqualTo(EgressChannel.ToolArguments));
            Assert.That(record.Severity, Is.EqualTo(EgressSeverity.Suspicious));
            Assert.That(record.DetectorName, Is.EqualTo("openai-api-key"));
            Assert.That(record.ToolName, Is.EqualTo("search"));
            Assert.That(record.Reason, Does.Contain("openai-api-key"));
            Assert.That(record.Reason, Does.Not.Contain(fakeKey));
        });
    }

    [Test]
    public async Task Strict_handler_aborts_turn_and_tool_call_on_secret_pattern_match()
    {
        const string fakeKey = "sk-proj-1234567890abcdef";
        var pattern = new SecretPattern(
            "openai-api-key",
            new Regex(@"sk-proj-[A-Za-z0-9]{16}", RegexOptions.NonBacktracking));

        // 1. Turn output with strict handler -> uniform refusal
        var outputClient = new DelegateChatClient(_ => $"Leaking {fakeKey}");
        var outputSession = await CreateSessionAsync(
            outputClient,
            new CanaryDefenseFeature(
                new StrictSuspiciousViolationHandler(),
                options => options.SecretPatterns = [pattern]));

        var turnResult = await outputSession.InvokeAsync([AgentMessage.FromText("user", "print key")]);
        Assert.That(turnResult.Output.Single().Content, Is.EqualTo(Refusal));

        // 2. Tool call arguments with strict handler -> aborted before tool runs
        var tool = new SpyTool();
        var toolClient = new ScriptedChatClient();
        var toolSession = await CreateSessionAsync(
            toolClient,
            new CanaryDefenseFeature(
                new StrictSuspiciousViolationHandler(),
                options => options.SecretPatterns = [pattern]),
            new ToolRegistrationFeature(tool));

        toolClient.Script.Enqueue(_ => ToolCall(
            "call-1",
            "search",
            new Dictionary<string, object?>
            {
                ["query"] = fakeKey,
            }));

        var exception = Assert.ThrowsAsync<CanaryViolationException>(async () =>
            await toolSession.InvokeAsync([AgentMessage.FromText("user", "search key")]));

        Assert.Multiple(() =>
        {
            Assert.That(tool.CallCount, Is.Zero, "Tool must not run when strict handler aborts on secret pattern.");
            Assert.That(exception, Is.Not.Null);
            Assert.That(exception!.Message, Is.EqualTo("The turn was aborted by egress defense."));
        });
    }

    // ── Durable memory write channel (EgressChannel.MemoryWrite) ──────────────

    [Test]
    public async Task Canary_in_memory_write_aborts_before_persisting_and_audits()
    {
        var backingStore = new InMemoryMemoryStore();
        var sink = new InMemoryEgressAuditSink();
        var client = new DelegateChatClient(_ => "ok");
        CanaryViolationException? writeException = null;

        var session = await CreateSessionAsync(
            client,
            new MemoryFeature(backingStore),
            new CanaryDefenseFeature(options => options.AuditSink = sink),
            new MemoryWriteCallbackFeature(async context =>
            {
                var store = context.Services.GetRequiredService<IMemoryStore>();
                var vault = context.Services.GetRequiredService<ICanaryVault>();
                var canary = vault.Canaries[0];

                try
                {
                    await store.SetAsync(new MemoryEntry(
                        Key: "poison-1",
                        Namespace: "facts",
                        Content: $"remember {ToBase64(canary)}"));
                }
                catch (CanaryViolationException ex)
                {
                    writeException = ex;
                }
            }));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "hi")]);

        Assert.Multiple(async () =>
        {
            Assert.That(writeException, Is.Not.Null, "Writing a canary to IMemoryStore must throw CanaryViolationException.");
            var persisted = await backingStore.GetAsync("facts", "poison-1");
            Assert.That(persisted, Is.Null, "Violating entry must not be persisted to the backing store.");
            Assert.That(result.Output.Single().Content, Is.EqualTo(Refusal), "Session is quarantined after a confirmed memory write violation.");

            var records = await sink.ListAllAsync();
            Assert.That(records, Has.Count.EqualTo(1));
            Assert.That(records[0].Channel, Is.EqualTo(EgressChannel.MemoryWrite));
            Assert.That(records[0].Severity, Is.EqualTo(EgressSeverity.Confirmed));
            Assert.That(records[0].DetectorName, Is.EqualTo("session-canary"));
        });
    }

    [Test]
    public async Task Memory_write_is_guarded_regardless_of_feature_registration_order()
    {
        var backingStore = new InMemoryMemoryStore();
        var client = new DelegateChatClient(_ => "ok");
        CanaryViolationException? writeException = null;

        // Register CanaryDefenseFeature BEFORE MemoryFeature
        var session = await CreateSessionAsync(
            client,
            new CanaryDefenseFeature(),
            new MemoryFeature(backingStore),
            new MemoryWriteCallbackFeature(async context =>
            {
                var store = context.Services.GetRequiredService<IMemoryStore>();
                var vault = context.Services.GetRequiredService<ICanaryVault>();
                var canary = vault.Canaries[0];

                try
                {
                    await store.SetAsync(new MemoryEntry(
                        Key: "order-test",
                        Namespace: "facts",
                        Content: "clean content",
                        Metadata: new Dictionary<string, string>
                        {
                            ["leak"] = canary,
                        }));
                }
                catch (CanaryViolationException ex)
                {
                    writeException = ex;
                }
            }));

        await session.InvokeAsync([AgentMessage.FromText("user", "hi")]);

        Assert.Multiple(async () =>
        {
            Assert.That(writeException, Is.Not.Null,
                "IMemoryStore must be wrapped even when CanaryDefenseFeature contributes before MemoryFeature.");
            var persisted = await backingStore.GetAsync("facts", "order-test");
            Assert.That(persisted, Is.Null, "Violating entry in Metadata must not be persisted.");
        });
    }

    [Test]
    public async Task Quarantined_session_refuses_subsequent_memory_writes()
    {
        var backingStore = new InMemoryMemoryStore();
        var client = new DelegateChatClient(_ => "ok");
        CanaryViolationException? secondWriteException = null;

        var session = await CreateSessionAsync(
            client,
            new MemoryFeature(backingStore),
            new CanaryDefenseFeature(),
            new MemoryWriteCallbackFeature(async context =>
            {
                var store = context.Services.GetRequiredService<IMemoryStore>();
                var vault = context.Services.GetRequiredService<ICanaryVault>();
                var canary = vault.Canaries[0];

                try
                {
                    await store.SetAsync(new MemoryEntry("first", "facts", canary));
                }
                catch (CanaryViolationException)
                {
                    // Expected: quarantines session
                }

                try
                {
                    await store.SetAsync(new MemoryEntry("second-clean", "facts", "completely harmless fact"));
                }
                catch (CanaryViolationException ex)
                {
                    secondWriteException = ex;
                }
            }));

        await session.InvokeAsync([AgentMessage.FromText("user", "hi")]);

        Assert.Multiple(async () =>
        {
            Assert.That(secondWriteException, Is.Not.Null, "Quarantined session must refuse subsequent clean memory writes.");
            var persisted = await backingStore.GetAsync("facts", "second-clean");
            Assert.That(persisted, Is.Null, "Clean entry written after quarantine must not reach backing store.");
        });
    }

    [Test]
    public async Task Suspicious_memory_write_is_audited_and_respects_handler_disposition()
    {
        const string fakeKey = "sk-proj-1234567890abcdef";
        var pattern = new SecretPattern(
            "openai-api-key",
            new Regex(@"sk-proj-[A-Za-z0-9]{16}", RegexOptions.NonBacktracking));

        // 1. Default handler: audits suspicious finding on MemoryWrite and allows persistence
        var defaultBackingStore = new InMemoryMemoryStore();
        var defaultSink = new InMemoryEgressAuditSink();
        var defaultClient = new DelegateChatClient(_ => "ok");

        var defaultSession = await CreateSessionAsync(
            defaultClient,
            new MemoryFeature(defaultBackingStore),
            new CanaryDefenseFeature(options =>
            {
                options.AuditSink = defaultSink;
                options.SecretPatterns = [pattern];
            }),
            new MemoryWriteCallbackFeature(async context =>
            {
                var store = context.Services.GetRequiredService<IMemoryStore>();
                await store.SetAsync(new MemoryEntry("key-1", "secrets", $"api_key={fakeKey}"));
            }));

        await defaultSession.InvokeAsync([AgentMessage.FromText("user", "save key")]);

        var defaultPersisted = await defaultBackingStore.GetAsync("secrets", "key-1");
        var defaultRecords = await defaultSink.ListAllAsync();

        Assert.Multiple(() =>
        {
            Assert.That(defaultPersisted, Is.Not.Null, "Default handler allows suspicious memory writes in shadow mode.");
            Assert.That(defaultRecords, Has.Count.EqualTo(1));
            Assert.That(defaultRecords[0].Channel, Is.EqualTo(EgressChannel.MemoryWrite));
            Assert.That(defaultRecords[0].Severity, Is.EqualTo(EgressSeverity.Suspicious));
            Assert.That(defaultRecords[0].DetectorName, Is.EqualTo("openai-api-key"));
            Assert.That(defaultRecords[0].Reason, Does.Not.Contain(fakeKey));
        });

        // 2. Strict handler: audits and aborts suspicious memory write before persistence
        var strictBackingStore = new InMemoryMemoryStore();
        var strictSink = new InMemoryEgressAuditSink();
        var strictClient = new DelegateChatClient(_ => "ok");
        CanaryViolationException? strictException = null;

        var strictSession = await CreateSessionAsync(
            strictClient,
            new MemoryFeature(strictBackingStore),
            new CanaryDefenseFeature(
                new StrictSuspiciousViolationHandler(),
                options =>
                {
                    options.AuditSink = strictSink;
                    options.SecretPatterns = [pattern];
                }),
            new MemoryWriteCallbackFeature(async context =>
            {
                var store = context.Services.GetRequiredService<IMemoryStore>();
                try
                {
                    await store.SetAsync(new MemoryEntry("key-2", "secrets", $"api_key={fakeKey}"));
                }
                catch (CanaryViolationException ex)
                {
                    strictException = ex;
                }
            }));

        await strictSession.InvokeAsync([AgentMessage.FromText("user", "save key")]);

        var strictPersisted = await strictBackingStore.GetAsync("secrets", "key-2");
        var strictRecords = await strictSink.ListAllAsync();

        Assert.Multiple(() =>
        {
            Assert.That(strictException, Is.Not.Null, "Strict handler must abort suspicious memory write.");
            Assert.That(strictPersisted, Is.Null, "Aborted suspicious memory write must not be persisted.");
            Assert.That(strictRecords, Has.Count.EqualTo(1));
            Assert.That(strictRecords[0].Channel, Is.EqualTo(EgressChannel.MemoryWrite));
        });
    }

    [Test]
    public async Task ScanMemoryWrites_false_bypasses_memory_write_scanning()
    {
        var backingStore = new InMemoryMemoryStore();
        var sink = new InMemoryEgressAuditSink();
        var client = new DelegateChatClient(_ => "ok");

        var session = await CreateSessionAsync(
            client,
            new MemoryFeature(backingStore),
            new CanaryDefenseFeature(options =>
            {
                options.AuditSink = sink;
                options.ScanMemoryWrites = false;
            }),
            new MemoryWriteCallbackFeature(async context =>
            {
                var store = context.Services.GetRequiredService<IMemoryStore>();
                var vault = context.Services.GetRequiredService<ICanaryVault>();
                await store.SetAsync(new MemoryEntry("unscanned", "facts", vault.Canaries[0]));
            }));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "hi")]);

        Assert.Multiple(async () =>
        {
            Assert.That(result.Output.Single().Content, Is.EqualTo("ok"));
            var persisted = await backingStore.GetAsync("facts", "unscanned");
            Assert.That(persisted, Is.Not.Null, "When ScanMemoryWrites is false, memory writes are not scanned.");
            var records = await sink.ListAllAsync();
            Assert.That(records, Is.Empty);
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private readonly List<ServiceProvider> _providers = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var provider in _providers)
            provider.Dispose();

        _providers.Clear();
    }

    private async Task<IAgentSession> CreateSessionAsync(
        IChatClient client,
        params IAgentFeature[] features)
    {
        // The provider must outlive the session: the runtime resolves the approval policy, trace sink
        // and other turn services from it on every turn. Disposing it here makes every tool call fail
        // with ObjectDisposedException, which would look like a defense failure rather than a bug.
        var provider = new ServiceCollection().BuildServiceProvider();
        _providers.Add(provider);

        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions()));
        var factory = new DefaultAgentSessionFactory(runtime, provider);
        return await factory.CreateSessionAsync("canary-session", new AgentDefinition("guarded", features));
    }

    private static string SystemPrompt(DelegateChatClient client)
        => client.Requests[0].FirstOrDefault(static message => message.Role == ChatRole.System)?.Text ?? string.Empty;

    private static string ExtractCanary(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            var match = CanaryPattern().Match(message.Text ?? string.Empty);
            if (match.Success)
                return match.Value;
        }

        return string.Empty;
    }

    private static string ToBase64(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string SplitWithZeroWidth(string value)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (var character in value)
        {
            builder.Append(character);
            builder.Append('\u200B');
        }

        return builder.ToString();
    }

    private static ChatResponse ToolCall(string callId, string toolName, IDictionary<string, object?> arguments)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, arguments)]));

    private sealed class ToolRegistrationFeature(ITool tool) : IAgentFeature
    {
        public void Contribute(AgentAssembly assembly) => assembly.Tools.Add(tool);
    }

    private sealed class MemoryWriteCallbackFeature(Func<AgentTurnContext, ValueTask> onTurnAsync) : IAgentFeature
    {
        public void Contribute(AgentAssembly assembly)
            => assembly.Pipeline.Use(new CallbackMiddleware(onTurnAsync));

        private sealed class CallbackMiddleware(Func<AgentTurnContext, ValueTask> onTurnAsync) : IAgentMiddleware
        {
            public async ValueTask<AgentTurnResult> InvokeAsync(
                AgentTurnContext context,
                Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
                CancellationToken cancellationToken = default)
            {
                await onTurnAsync(context).ConfigureAwait(false);
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class SpyTool : ITool
    {
        public string Name => "search";
        public string? Description => "Searches the web.";
        public int CallCount { get; private set; }

        public ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult("tool result");
        }
    }

    private sealed class ThrowingScanner : IEgressScanner
    {
        public ValueTask<EgressScanResult> ScanAsync(
            EgressChannel channel,
            string payload,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("scanner is unavailable");
    }

    /// <summary> Fake model that answers with a fixed transformation of the canary it was told.</summary>
    private sealed class DelegateChatClient(Func<string, string> transform) : IChatClient
    {
        public Func<string, string> Transform { get; set; } = transform;

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = messages.ToArray();
            Requests.Add(snapshot);

            var text = Transform(ExtractCanary(snapshot));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new TextContent(text)])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var content in response.Messages[0].Contents)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [content]);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Fake model that replays a script of queued responses, for tool-call scenarios.</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public Queue<Func<IReadOnlyList<ChatMessage>, ChatResponse>> Script { get; } = new();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Script.Count == 0)
                throw new InvalidOperationException("No fake chat response is queued.");

            return Task.FromResult(Script.Dequeue()(messages.ToArray()));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var content in response.Messages[0].Contents)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [content]);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingEgressAuditSink : IEgressAuditSink
    {
        public ValueTask RecordAsync(EgressAuditRecord record, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Audit store unavailable");

        public ValueTask<IReadOnlyList<EgressAuditRecord>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Audit store unavailable");

        public ValueTask<IReadOnlyList<EgressAuditRecord>> ListAllAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Audit store unavailable");
    }

    private sealed class StrictSuspiciousViolationHandler : ICanaryViolationHandler
    {
        public ValueTask<CanaryDisposition> HandleAsync(
            CanaryViolationContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(CanaryDisposition.Abort);
    }
}
