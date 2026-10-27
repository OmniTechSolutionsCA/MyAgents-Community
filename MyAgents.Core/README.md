# MyAgents.Core

Provider-neutral contracts and primitives shared by the MyAgents Community
packages. This library defines agent messages and definitions, session and
runtime abstractions, structured output response formats (`AgentResponseFormat`),
tool and approval contracts (including `DelegateTool` for method/lambda tool binding),
turn results, and related core services.

Use `MyAgents.Runtime.Chat` when you need an in-process chat and tool runtime
over a `Microsoft.Extensions.AI.IChatClient`. See the root README for the
Community feature boundary and local build instructions.
