# Prompt Registry & Versioned Templates

Hardcoding system prompts across dozens of agents makes it difficult to track prompt revisions, run A/B experiments, or inject environment-specific variables. `MyAgents.Core.Prompts` and `MyAgents.Agents` provide `PromptRegistry`, `PromptTemplateDefinition`, and `PromptRegistryFeature` for versioned prompt management.

## 1. Registering Versioned Prompt Templates

`PromptRegistry` stores named, versioned `PromptTemplateDefinition` records with `{VariableName}` placeholder rendering:

```csharp
using MyAgents.Core.Prompts;

var registry = new PromptRegistry()
    .Register(new PromptTemplateDefinition(
        Name: "system.support",
        Version: "1.0",
        Template: "You are a support engineer for {ProductName}. Keep answers under {MaxWords} words.",
        Description: "Initial support prompt"))
    .Register(new PromptTemplateDefinition(
        Name: "system.support",
        Version: "2.0",
        Template: "You are a senior reliability engineer for {ProductName}. " +
                  "Always cite runbooks and keep answers under {MaxWords} words.",
        Description: "V2 support prompt with runbook citation requirement"));
```

### Resolving and Rendering Templates Directly

```csharp
// Get a specific version
IPromptTemplate v1 = registry.Get("system.support", version: "1.0")!;

// Or get the latest registered version
IPromptTemplate latest = registry.GetLatest("system.support")!;

string rendered = latest.Render(new Dictionary<string, string>
{
    ["ProductName"] = "MyAgents Cloud",
    ["MaxWords"] = "150"
});
```

---

## 2. Attaching `PromptRegistryFeature` to an Agent

`PromptRegistryFeature` wires a `PromptRegistry` into the session (`assembly.PromptRegistry`) and optionally renders a selected template (latest or pinned version) into the agent's system prompt at assembly time:

```csharp
using MyAgents.Agents;
using MyAgents.Core;

// Render the latest version of "system.support"
var latestAgent = new AgentDefinition("support-latest", [
    new PromptRegistryFeature(
        registry,
        systemTemplateName: "system.support",
        variables: new Dictionary<string, string>
        {
            ["ProductName"] = "MyAgents Community",
            ["MaxWords"] = "200"
        })
]);

// Pin a specific version ("1.0") for deterministic regression testing
var pinnedAgent = new AgentDefinition("support-v1", [
    new PromptRegistryFeature(
        registry,
        systemTemplateName: "system.support",
        systemTemplateVersion: "1.0",
        variables: new Dictionary<string, string>
        {
            ["ProductName"] = "MyAgents Community",
            ["MaxWords"] = "200"
        })
]);
```
