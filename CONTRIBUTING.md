# Contributing to MyAgents Community

Thanks for considering a contribution. This repository is the Community
distribution only; proprietary projects and generated private-source exports
must not be added.

## Before opening a pull request

1. Open an issue first for larger changes so the scope can be agreed.
2. Keep changes focused and include tests for behavior changes.
3. Update public API documentation and `CHANGELOG.md` when appropriate.
4. Do not include credentials, customer data, proprietary source, generated
   build output, or dependencies whose redistribution rights are unclear.

## Local validation

Install the .NET 10 SDK and PowerShell 7, then run these commands from the
repository root:

```powershell
pwsh -File community/eng/check-open-core-boundary.ps1
dotnet restore MyAgents.Community.slnx
dotnet build MyAgents.Community.slnx --no-restore -c Release
dotnet test MyAgents.Community.slnx --no-build -c Release
dotnet pack MyAgents.Community.slnx --no-build --no-restore -c Release -o artifacts/packages
```

Run these checks locally before submitting a pull request. The Community
validation workflow is also configured to run against the sanitized export on
Windows, Linux, and macOS when GitHub Actions is enabled.

## Pull requests

Use the pull request template. Describe the user-visible change, compatibility
impact, and validation performed. Keep commits reviewable, and do not merge or
publish a release from a pull request.

By submitting a contribution, you confirm that you have the right to submit it
under the repository's MIT license. No separate contributor agreement is
required at this time.
