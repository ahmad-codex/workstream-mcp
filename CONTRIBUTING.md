# Contributing

Thanks for taking the time to contribute. This file covers how to build, test and send a change.

## Before you start

- Read [`workstream-mcp-spec.md`](./workstream-mcp-spec.md). It is the design reference, and
  most review comments come back to it.
- For anything larger than a bug fix, open an issue first so the approach can be agreed
  before you write the code.

## Where to start

[docs/ROADMAP.md](./docs/ROADMAP.md) lists good first issues with pointers to the code, and the
larger planned work.

## Prerequisites

- .NET SDK 10 (the exact feature band is pinned in [`global.json`](./global.json))
- Docker, for the integration and concurrency tests (Testcontainers starts Postgres 16)

Or open the repository in the dev container (`.devcontainer/`, also used by GitHub
Codespaces). It has the .NET 10 SDK, a Postgres 16 service with `WORKSTREAM_DB_CONNECTION`
already set, and Docker-in-Docker for the Testcontainers tests. `make run` starts the API there.

## Build and test

```bash
dotnet restore
dotnet build

# Unit tests, no Docker needed
dotnet test tests/Workstream.UnitTests

# Need Docker running
dotnet test tests/Workstream.IntegrationTests
dotnet test tests/Workstream.ConcurrencyTests

# One test by name
dotnet test tests/Workstream.UnitTests --filter "FullyQualifiedName~StateMachineServiceTests"
```

The `Makefile` wraps the same commands: `make build`, `make test-unit`, `make test`,
`make format`, `make format-check`, `make run`, `make up` / `make down` (quick-start stack).

The build treats warnings as errors (`Directory.Build.props`) and enforces the code-style rules
in [`.editorconfig`](./.editorconfig) at build time (`EnforceCodeStyleInBuild`). A change that
adds a warning will fail CI, and so will code that `dotnet format style` would change
(`make format` fixes it). Configure your editor to honor `.editorconfig`.

The concurrency tests in `tests/Workstream.ConcurrencyTests` are the correctness gate for the
claim primitive. If they fail or flake, the change does not merge.

## Rules the code depends on

These come from the spec and are checked in review.

- **State changes go through `StateMachineService`.** Illegal transitions return the
  `illegal_transition` error with `details.allowed_next` filled in.
- **External calls go through an outbox.** No GitHub or Slack HTTP call inside an MCP request
  transaction. Write a row to `board_sync_log` or `slack_notify_log` and let the worker send it.
- **Every state-changing transaction writes an `events` row.** The table is append-only.
- **The URL token is a credential.** Never log it, put it in an error, or send it anywhere.
- **New plan types are data.** Add a profile under `deploy/profiles/` and a seed migration; do
  not add plan-type-specific branches in C#.
- **Migrations are forward-only.** Add a new numbered file under `deploy/migrations/`; never
  edit one that has already shipped.

## Adding an MCP tool

1. Add a class under `src/Workstream.Mcp/Tools/<Group>/` that extends `McpTool<TInput, TOutput>`.
2. Write the `Description` for the calling LLM: when to use the tool, what state the entity
   must be in, and what the result means.
3. Register it in `McpToolRegistration.AddWorkstreamMcpTools`.
4. Add integration tests under `tests/Workstream.IntegrationTests/Tools/` for the happy path
   and the structured error cases.
5. Add a row to [`docs/mcp-tools.md`](./docs/mcp-tools.md) and the tool table in the README.

## Commits and pull requests

- Branch from `master`. Keep pull requests focused on one change.
- Commit messages: a short sentence in the imperative mood, capitalized, no trailing period,
  for example `Add cancel_audit tool` or `Fix claim TTL parsing for hour units`.
- Pull request titles follow the same style; they become the release-note lines. Branch names
  starting with `fix/`, `feat/` or `docs/` get the matching label automatically.
- Fill in the pull request template. Say how you tested the change.
- CI must pass: build, code style, unit, integration and concurrency tests.
- Labels drive the release notes: `bug`, `enhancement`, `documentation`, `breaking-change`,
  `security`, `skip-changelog`. Area labels (`area: slack`, ...) are added from the changed paths.
- Signing off commits (`git commit -s`, Developer Certificate of Origin) is welcome but not
  required. There is no CLA.
- Update docs in `docs/` when behavior or configuration changes, and add a line under
  `Unreleased` in [`CHANGELOG.md`](./CHANGELOG.md).

## Reporting bugs and security issues

Use the issue templates for bugs and feature requests. Report security problems privately as
described in [SECURITY.md](./SECURITY.md).

By contributing you agree that your contributions are licensed under the [MIT License](./LICENSE),
and you agree to follow the [Code of Conduct](./CODE_OF_CONDUCT.md).
