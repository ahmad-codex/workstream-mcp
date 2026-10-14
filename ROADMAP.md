# Roadmap

Workstream is the shared ledger under your agents. Tools like Vibe Kanban, Conductor, Claude
Squad and Task Master run agents; Workstream makes sure two of them never take the same task,
that every change goes through review, and that you can replay what happened afterwards.

This file is the plan, and each item links to an issue you can pick up. The
[roadmap board](https://github.com/users/ahmad-codex/projects/1) is the live view. There are no
dates: items move from Next to Now when someone is working on them.

## Now

Make it easy to see Workstream working, and safe to run.

- **Web dashboard (read-only).** (#46) A board per plan, live claims with their TTL, stuck work, and the
  event timeline for any task or finding.
- **Webhook outbox.** (#47) Signed HTTP callbacks for task, claim, review and finding events, with
  retries through the same outbox as GitHub and Slack. One generic sink covers most
  integrations before they have their own connector.
- **Hashed tokens and per-agent API keys.** (#32, #48) Tokens stored as hashes, scoped keys per agent, and
  the token kept out of URLs where the client allows it.
- **Docs site and one-click deploy.** (#59, #60) A docs site built from `docs/`, and a Railway or Fly
  template next to the Docker image.

Smaller items in Now: tests for the outbox workers (#61), token rotation behind a proxy (#25),
board commands in the CLI (#26), releasing finding claims (#28), docs for Codex CLI and Gemini
CLI (#29), and a two-agent example script (#30).

## Next

Grow the number of places Workstream can talk to, mostly through contributions.

- **Connector SDK.** (#49) One interface for outbound sinks, a project template and a guide, so a new
  integration is a small, self-contained pull request.
- **Connectors.** Linear (#50), Jira (#51), Discord (#52), Microsoft Teams (#53) and email (#54).
- **Two-way GitHub sync.** (#55) Apply inbound board moves, with idempotent delivery handling.
- **Human actions in the dashboard.** Approve a review, override a verdict, release a stuck
  claim.
- **Client SDKs and CLI.** (#56, #57) TypeScript and Python clients, and a `workstream` CLI for people.
- **Importers.** (#58) Task Master `tasks.json`, Backlog.md and GitHub issues into a plan.
- **Stuck-work report in Slack.** (#31) Post the hourly report through the Slack outbox.
- **Declarative bootstrap and more plan types.** (#33, #34) `bootstrap.yml` and testing, migration and
  release profiles.
- **Observability.** Claim latency, review cycle time and stuck rate as OpenTelemetry metrics,
  with the Grafana dashboard updated.

## Later

For teams running Workstream across many projects.

- Workspaces with tenant isolation.
- OAuth 2.1 for remote MCP clients, and roles (agent, reviewer, admin).
- Policy rules, for example "tasks of this type need an audit before done".
- Analytics: cycle time per plan, rework rate, cost per task.
- Running outbox workers on more than one replica.

## Not planned

- Running agents. Workstream records and coordinates work; runners stay separate.
- A general automation builder. Use n8n or Activepieces and connect them through the webhook
  outbox.
- Databases other than Postgres. The claim model relies on `SKIP LOCKED`.

## How to help

Pick an issue labelled `good first issue` or `help wanted`, comment that you are taking it, and
read [CONTRIBUTING.md](./CONTRIBUTING.md). To suggest or vote on features, use
[Discussions](https://github.com/ahmad-codex/workstream-mcp/discussions).
