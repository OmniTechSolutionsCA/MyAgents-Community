namespace MyAgents.Core.Prompts;

/// <summary>
/// A versioned, parameterized prompt template.
/// </summary>
/// <param name="Name">Unique template name (e.g., "system.researcher").</param>
/// <param name="Version">Semantic version string (e.g., "1.0", "2.1").</param>
/// <param name="Template">
/// Template text. Use <c>{VariableName}</c> placeholders for substitution via
/// <see cref="IPromptTemplate.Render"/>.
/// </param>
/// <param name="Description">Optional human-readable description of the template's purpose.</param>
public sealed record PromptTemplateDefinition(
    string Name,
    string Version,
    string Template,
    string? Description = null);

/// <summary>
/// A resolved, renderable prompt template.
/// Obtained from <see cref="PromptRegistry"/> via <see cref="PromptRegistry.Get"/> or
/// <see cref="PromptRegistry.GetLatest"/>.
/// </summary>
public interface IPromptTemplate
{
    /// <summary>Template name.</summary>
    string Name { get; }

    /// <summary>Template version.</summary>
    string Version { get; }

    /// <summary>
    /// Renders the template by substituting <paramref name="variables"/> into
    /// <c>{VariableName}</c> placeholders (case-sensitive).
    /// Missing keys are left as-is in the output.
    /// </summary>
    string Render(IReadOnlyDictionary<string, string>? variables = null);
}

/// <summary>
/// Registry for versioned prompt templates.
/// Register templates at startup; look them up by name + optional version at call time.
/// </summary>
public sealed class PromptRegistry
{
    // Key: (name, version)
    private readonly Dictionary<(string Name, string Version), IPromptTemplate> _templates = new();

    /// <summary>
    /// Registers a prompt template. If a template with the same name + version already exists
    /// it is silently replaced (last-write-wins — useful for test overrides).
    /// </summary>
    public PromptRegistry Register(PromptTemplateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Template);

        _templates[(definition.Name, definition.Version)] = new DefaultPromptTemplate(definition);
        return this;
    }

    /// <summary>
    /// Retrieves a specific version of a template.
    /// </summary>
    /// <exception cref="KeyNotFoundException">When the name+version combination is not registered.</exception>
    public IPromptTemplate Get(string name, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (_templates.TryGetValue((name, version), out var t))
            return t;

        throw new KeyNotFoundException($"Prompt template '{name}' v{version} is not registered.");
    }

    /// <summary>
    /// Returns the most recently registered version for the given name, using
    /// <see cref="Version"/> string comparison. Returns null if no template with that
    /// name is registered.
    /// </summary>
    public IPromptTemplate? GetLatest(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _templates
            .Where(kv => kv.Key.Name == name)
            .OrderByDescending(kv => ParseVersion(kv.Key.Version))
            .Select(kv => kv.Value)
            .FirstOrDefault();
    }

    /// <summary>Returns true if the given name + version is registered.</summary>
    public bool Contains(string name, string version)
        => _templates.ContainsKey((name, version));

    /// <summary>Returns all registered template definitions, ordered by name then version.</summary>
    public IReadOnlyList<IPromptTemplate> All()
        => _templates.Values
            .OrderBy(t => t.Name)
            .ThenByDescending(t => ParseVersion(t.Version))
            .ToList();

    private static Version ParseVersion(string v)
    {
        if (System.Version.TryParse(v, out var parsed))
            return parsed;
        // Fallback: treat single-segment strings like "1" as "1.0"
        if (int.TryParse(v, out var major))
            return new Version(major, 0);
        return new Version(0, 0);
    }

    // ── Default implementation ─────────────────────────────────────────────

    private sealed class DefaultPromptTemplate : IPromptTemplate
    {
        private readonly PromptTemplateDefinition _def;

        public DefaultPromptTemplate(PromptTemplateDefinition def) => _def = def;

        public string Name => _def.Name;
        public string Version => _def.Version;

        public string Render(IReadOnlyDictionary<string, string>? variables = null)
        {
            if (variables is null or { Count: 0 })
                return _def.Template;

            var result = _def.Template;
            foreach (var (key, value) in variables)
                result = result.Replace($"{{{key}}}", value, StringComparison.Ordinal);

            return result;
        }
    }
}
