namespace MyAgents.Core.Tools;

/// <summary>
/// Marks a method as an agent tool to be generated at compile time by the Roslyn source generator.
/// The generated tool embeds its JSON schema as a compile-time constant and binds primitive
/// arguments (<see cref="string"/>, <see cref="bool"/>, <see cref="int"/>, <see cref="long"/>,
/// <see cref="float"/>, <see cref="double"/>, <see cref="decimal"/>) without reflection or boxing.
/// Other parameter and return types use reflection-based System.Text.Json serialization unless
/// <see cref="JsonContext"/> supplies source-generated serialization metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class AgentToolAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the tool name, or null to use the method name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the tool description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> type that
    /// provides source-generated serialization metadata for the tool's complex parameter and result
    /// types. Every non-primitive argument and result type must be declared in the context via
    /// <see cref="System.Text.Json.Serialization.JsonSerializableAttribute"/>; the generator reports a
    /// compile-time error otherwise. When set, the generated tool binds arguments and serializes results
    /// through <see cref="System.Text.Json.JsonTypeInfo"/> without reflection, making the tool
    /// NativeAOT- and trimming-safe.
    /// </summary>
    public Type? JsonContext { get; set; }

    /// <summary>
    /// Initializes a new instance of <see cref="AgentToolAttribute"/>.
    /// </summary>
    /// <param name="name">Optional tool name. If omitted, the method name is used.</param>
    public AgentToolAttribute(string? name = null)
    {
        Name = name;
    }
}
