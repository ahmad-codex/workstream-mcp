## What and why

<!-- What this changes and the problem it solves. Link the issue if there is one. -->

## How it was tested

<!-- Commands run, new or changed tests, manual MCP calls. -->

## Checklist

- [ ] `dotnet build` passes with no warnings
- [ ] Unit, integration and concurrency tests pass
- [ ] State changes go through `StateMachineService` and write an `events` row
- [ ] No GitHub or Slack call inside an MCP request transaction (outbox only)
- [ ] No URL token or secret in logs, errors or tests
- [ ] New migrations are new files; no shipped migration was edited
- [ ] Docs and `CHANGELOG.md` updated where behavior or config changed
