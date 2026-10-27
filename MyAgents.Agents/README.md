# MyAgents.Agents

Feature-composition building blocks for MyAgents agents. Add prompts, tools
(`ToolsFeature`), structured outputs (`StructuredOutputFeature<T>`), guardrails
(`GuardrailsFeature`), model capabilities, observers, and other behavior through
agent features while keeping the core abstractions provider-neutral.

Pair this package with a runtime implementation such as `MyAgents.Runtime.Chat`
to execute chat turns. See the root README for the Community feature boundary
and local build instructions.
