# Community development

The Community solution includes the libraries, tests, and sample used to build
and validate this edition. From the repository root:

```sh
pwsh -File community/eng/check-open-core-boundary.ps1
dotnet restore MyAgents.Community.slnx
dotnet build MyAgents.Community.slnx --no-restore -c Release
dotnet test MyAgents.Community.slnx --no-build -c Release
```

The Community validation workflow runs these checks on Windows, Linux, and macOS.
