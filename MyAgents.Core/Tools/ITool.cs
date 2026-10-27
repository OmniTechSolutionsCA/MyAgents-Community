namespace MyAgents.Core.Tools;

/// <summary>
/// A tool that can be invoked by an agent.
/// </summary>
public interface ITool
{
    /// <summary>
    /// The name of the tool.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Optional description of what the tool does.
    /// </summary>
    string? Description { get; }

    /// <summary>
    /// Optional JSON Schema (as a JSON string) describing the tool's parameters.
    /// When provided, adapters advertise it to the model so it emits correctly-named,
    /// correctly-typed arguments instead of guessing. Null means "an object with
    /// unspecified properties".
    /// </summary>
    string? ParametersSchemaJson => null;

    /// <summary>
    /// Invokes the tool with JSON arguments.
    /// </summary>
    /// <param name="jsonArguments">Tool arguments as a JSON string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tool result as a string (typically JSON).</returns>
    ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default);
}
