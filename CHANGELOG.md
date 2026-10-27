# Changelog

All notable changes to MyAgents Community are documented here. This project
follows the Keep a Changelog format and Semantic Versioning after its first
stable release.

## [Unreleased]

### Added

- Initial Community distribution candidate with provider-neutral chat and tool
  execution, memory, MCP integration, and deterministic testing.
- An Ollama and OpenAI-compatible chat and clock-tool sample with an explicit
  tool allowlist.
- Tests for denied and unknown tools, cancellation, tool-call limits, and tool
  failure observations.

### Security

- Tool calls are allowed when no approval policy is registered; applications
  should configure an explicit policy before exposing sensitive tools.
- Keep SQLitePCLRaw at 2.1.12 for Community builds so NuGet cannot resolve the
  vulnerable 2.1.11 native SQLite dependency (GHSA-2m69-gcr7-jv3q).
