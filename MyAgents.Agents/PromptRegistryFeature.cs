using MyAgents.Core;
using MyAgents.Core.Prompts;

namespace MyAgents.Agents;

/// <summary>
/// Feature that wires a <see cref="PromptRegistry"/> into the agent session and
/// optionally renders selected templates into the agent's system prompt at assembly time.
/// </summary>
/// <remarks>
/// The registry is available at turn time via
/// <c>context.Services.GetService&lt;PromptRegistry&gt;()</c>, allowing middleware
/// and tools to dynamically look up and render templates during execution.
/// </remarks>
/// <example>
/// <code>
/// var registry = new PromptRegistry()
///     .Register(new PromptTemplateDefinition(
///         Name: "system.researcher",
///         Version: "1.0",
///         Template: "You are a research assistant. Focus area: {Domain}. Be concise.",
///         Description: "Researcher system prompt"))
///     .Register(new PromptTemplateDefinition(
///         Name: "system.researcher",
///         Version: "2.0",
///         Template: "You are an expert researcher specialising in {Domain}. "
///                 + "Cite sources. Summarise findings in bullet points.",
///         Description: "Researcher system prompt v2"));
///
/// var agent = new AgentDefinition(
///     Name: "ResearchAgent",
///     Features: new IAgentFeature[]
///     {
///         new PromptRegistryFeature(
///             registry,
///             systemTemplateName: "system.researcher",  // renders latest version into system prompt
///             variables: new Dictionary&lt;string, string&gt; { ["Domain"] = "quantum computing" }),
///         new LoggingFeature(),
///     });
/// </code>
/// </example>
public sealed class PromptRegistryFeature : IAgentFeature
{
    private readonly PromptRegistry _registry;
    private readonly string? _systemTemplateName;
    private readonly string? _systemTemplateVersion;
    private readonly IReadOnlyDictionary<string, string>? _variables;

    /// <summary>
    /// Creates a feature that exposes <paramref name="registry"/> via DI but does not
    /// automatically inject any system prompt. Templates are resolved at turn time.
    /// </summary>
    public PromptRegistryFeature(PromptRegistry registry)
        : this(registry, systemTemplateName: null, systemTemplateVersion: null, variables: null) { }

    /// <summary>
    /// Creates a feature that exposes <paramref name="registry"/> via DI and renders the
    /// latest version of <paramref name="systemTemplateName"/> (with optional
    /// <paramref name="variables"/>) as the agent's system prompt at assembly time.
    /// </summary>
    public PromptRegistryFeature(
        PromptRegistry registry,
        string systemTemplateName,
        IReadOnlyDictionary<string, string>? variables = null)
        : this(registry, systemTemplateName, systemTemplateVersion: null, variables) { }

    /// <summary>
    /// Creates a feature that exposes <paramref name="registry"/> via DI and renders a
    /// specific version of <paramref name="systemTemplateName"/> as the system prompt.
    /// </summary>
    public PromptRegistryFeature(
        PromptRegistry registry,
        string? systemTemplateName,
        string? systemTemplateVersion,
        IReadOnlyDictionary<string, string>? variables)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _systemTemplateName = systemTemplateName;
        _systemTemplateVersion = systemTemplateVersion;
        _variables = variables;
    }

    public void Contribute(AgentAssembly assembly)
    {
        // Wire registry into session DI overlay
        assembly.PromptRegistry = _registry;

        // Optionally render a template into the system prompt now (at composition time)
        if (_systemTemplateName is not null)
        {
            var template = _systemTemplateVersion is not null
                ? _registry.Get(_systemTemplateName, _systemTemplateVersion)
                : _registry.GetLatest(_systemTemplateName);

            if (template is not null)
                assembly.Prompts.AddSystem(template.Render(_variables));
        }
    }
}
