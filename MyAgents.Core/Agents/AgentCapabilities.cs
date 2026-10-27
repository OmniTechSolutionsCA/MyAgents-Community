namespace MyAgents.Core;

/// <summary>
/// Built-in well-known capability definitions.
/// Use these constants when declaring or querying agent capabilities so that
/// identifiers are consistent across the codebase.
/// </summary>
public static class AgentCapabilities
{
    /// <summary>The agent supports streaming responses via <c>InvokeStreamingAsync</c>.</summary>
    public static readonly IAgentCapability Streaming = new BuiltInCapability(
        "streaming",
        "Agent supports token-by-token streaming responses.");

    /// <summary>The agent can invoke registered tools (function calling).</summary>
    public static readonly IAgentCapability Tools = new BuiltInCapability(
        "tools",
        "Agent supports tool / function-calling.");

    /// <summary>The agent has access to a persistent memory store.</summary>
    public static readonly IAgentCapability Memory = new BuiltInCapability(
        "memory",
        "Agent has access to a persistent key-value memory store.");

    /// <summary>The agent can process multi-modal input (images, audio, binary data).</summary>
    public static readonly IAgentCapability MultiModal = new BuiltInCapability(
        "multi-modal",
        "Agent accepts multi-modal content parts (images, audio, binary data).");

    /// <summary>The agent participates as a named step inside a <c>WorkflowHost</c>.</summary>
    public static readonly IAgentCapability Workflow = new BuiltInCapability(
        "workflow",
        "Agent is designed to run as a step inside a workflow orchestration.");

    /// <summary>The agent emits OpenTelemetry traces and metrics.</summary>
    public static readonly IAgentCapability Telemetry = new BuiltInCapability(
        "telemetry",
        "Agent emits OpenTelemetry traces and metrics via TelemetryFeature.");

    /// <summary>The agent is connected to an <see cref=\"MyAgents.Core.Messaging.IAgentChannel\"/> message bus.</summary>
    public static readonly IAgentCapability Channel = new BuiltInCapability(
        "channel",
        "Agent is connected to an IAgentChannel pub/sub message bus.");

    /// <summary>The agent supports structured output with schema-constrained responses.</summary>
    public static readonly IAgentCapability StructuredOutput = new BuiltInCapability(
        "structured-output",
        "Agent supports structured output with schema-constrained responses.");
    // ──────────────────────────────────────────────────────────────────────────

    private sealed record BuiltInCapability(string Id, string Description) : IAgentCapability;
}
