# NativeAOT Tools Sample

This sample demonstrates NativeAOT-safe tool authoring with `[AgentTool(JsonContext = ...)]` using `MyAgents.Generators`.

JSON schemas, argument binding, and result serialization are generated at compile time and backed by a `JsonSerializerContext`, allowing the entire application to compile directly to a self-contained native executable with **zero reflection and zero JIT overhead**.

## Requirements

- .NET 10 SDK
- Native C++ build tools (Visual Studio C++ build tools on Windows, `clang` and `zlib` on Linux/macOS)

## Run with .NET CLI

```sh
dotnet run --project community/samples/NativeAotTools/NativeAotTools.csproj
```

## Publish as a Native Executable

Publish as a NativeAOT self-contained binary:

```sh
dotnet publish community/samples/NativeAotTools/NativeAotTools.csproj -c Release
```

The resulting executable in `bin/Release/net10.0/<rid>/publish/` has no external .NET runtime dependency and starts instantaneously.
