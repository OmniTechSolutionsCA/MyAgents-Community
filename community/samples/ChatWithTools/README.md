# Chat with a model and a tool

This sample connects MyAgents Community to an Ollama model or an OpenAI-compatible
provider. It asks the model to call a read-only UTC clock tool. The sample
explicitly allows that tool and rejects others.

## Run with Ollama

- .NET 10 SDK and a running Ollama server.
- A model that supports tool calls. The default is `llama3.2`.
- Enough disk space for the model. Model downloads and storage are managed by
  Ollama; configure its model storage location before pulling if needed.

Start Ollama and pull the model using its instructions, for example:

```sh
ollama pull llama3.2
```

From the repository root:

```sh
dotnet restore MyAgents.Community.slnx
dotnet run --project community/samples/ChatWithTools/ChatWithTools.csproj
```

You should see `[tool] Reading the local UTC clock.`, the model's answer, and
`Tool calls: 1` or more. Model output can vary. If the model answers without a
tool call, the sample exits with code 2 and suggests a tool-capable model.

The Ollama server defaults to `http://127.0.0.1:11434/` and the model to `llama3.2`.
To use another Ollama server or model, set `OLLAMA_BASE_URL` and `OLLAMA_MODEL`
in your environment before running the sample. The prompt and tool result are
sent to that server; use a server you trust.

## OpenAI-compatible provider

Alternatively set `MYAGENTS_PROVIDER=openai`, `MYAGENTS_BASE_URL` to the
provider's API base URL, `MYAGENTS_MODEL` to a tool-capable model, and
`MYAGENTS_API_KEY` to your API key in your environment. For example, Synthetic's
OpenAI-compatible base URL is `https://api.synthetic.new/openai/v1/`. Consult
your provider for current model names and costs. The prompt and the UTC clock
result are sent to the configured provider. Never commit API keys.

For example, in PowerShell, after setting `MYAGENTS_API_KEY` privately:

```powershell
$env:MYAGENTS_PROVIDER = 'openai'
$env:MYAGENTS_BASE_URL = 'https://api.synthetic.new/openai/v1/'
$env:MYAGENTS_MODEL = 'hf:zai-org/GLM-4.7-Flash'
dotnet run --project community/samples/ChatWithTools/ChatWithTools.csproj
```

This sample does not configure provider middleware that invokes tools
automatically: MyAgents owns dispatch and checks the policy before execution.
It uses OllamaSharp 5.4.23 to match the Community solution's current
Microsoft.Extensions.AI 10.3 dependency; later adapter versions may require
newer Microsoft.Extensions.AI packages.
