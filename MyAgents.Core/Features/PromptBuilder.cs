namespace MyAgents.Core;

/// <summary>
/// Builder for registering system prompts during agent assembly.
/// </summary>
public sealed class PromptBuilder
{
    private readonly List<string> _prompts = new();

    /// <summary>
    /// Adds a system prompt.
    /// </summary>
    /// <param name="prompt">The prompt text.</param>
    /// <returns>The builder for fluent chaining.</returns>
    public PromptBuilder AddSystem(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        _prompts.Add(prompt);
        return this;
    }

    /// <summary>
    /// Builds the combined system prompt.
    /// </summary>
    /// <returns>The concatenated system prompts, or null if none.</returns>
    public string? Build()
        => _prompts.Count == 0 ? null : string.Join("\n\n", _prompts);
}
