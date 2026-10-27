namespace MyAgents.Runtime.Chat;

/// <summary>Options for the provider-neutral chat client runtime.</summary>
public sealed class ChatClientRuntimeOptions
{
    /// <summary>Model ID used when an agent does not declare an <see cref="MyAgents.Core.AgentModelCapability"/>.</summary>
    public string? DefaultModelId { get; set; }

    /// <summary>Optional sampling temperature forwarded to the configured chat client.</summary>
    public float? Temperature { get; set; }

    /// <summary>Optional maximum number of output tokens requested from the model.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Maximum number of model round trips allowed to complete tool calls.</summary>
    public int MaxToolRoundTrips { get; set; } = 8;
}
