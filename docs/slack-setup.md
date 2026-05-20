# Slack setup

Per project. Steps are one-time per Slack workspace + channel pair. Once configured, every state change on that project's tasks/findings produces a Slack notification through the outbox worker (§8).

## 1. Create the Slack app (on api.slack.com)

1. **api.slack.com/apps** → **Create New App** → **From scratch**
2. Name (e.g. `Workstream`) + pick the workspace
3. Left sidebar → **OAuth & Permissions** → **Scopes** → **Bot Token Scopes** → add:
   - `chat:write`
   - `chat:write.public`
   - `channels:read`
4. Top of the same page → **Install to <workspace>** → **Allow**
5. After install, copy the **Bot User OAuth Token** (starts with `xoxb-`). This is the per-project secret.

## 2. Invite the bot to your channel (in Slack)

In the channel you want notifications:

```
/invite @<bot-name>
```

If the channel is public, `chat:write.public` lets the bot post even without an invite, but inviting is cleaner — Slack's UI is less surprising when the bot is a member.

Grab the channel ID: right-click the channel name → **View channel details** → bottom of the modal (`C01ABCDEFGH`).

## 3. Drop the token into the repo

Per-project, in `deploy/secrets/`. Name the file `slack-<project-slug>`:

```
deploy/secrets/slack-demo
```

Content: the `xoxb-…` token, no quotes, no trailing newline (use `[IO.File]::WriteAllText` on Windows PowerShell to avoid the default UTF-16 BOM).

Then declare the secret in `deploy/docker-compose.yml`:

```yaml
services:
  api:
    secrets:
      - pg_password
      - admin_token
      - gh_app_key
      - gh_webhook_secret
      - slack_demo     # <- add

secrets:
  ...
  slack_demo: { file: ./secrets/slack-demo }   # <- add
```

The docker-secret name `slack_demo` (underscore) is what the file is mounted as inside the container: `/run/secrets/slack_demo`. That's the path we'll reference next.

## 4. Recreate the api container so the new secret is mounted

```powershell
docker compose -f deploy/docker-compose.yml up -d
```

## 5. Register the Slack config against the project (via MCP)

Call the `set_project_slack` MCP tool. From PowerShell:

```powershell
$mcp = "http://localhost:8080/<your-token>/mcp"
$body = @{
  jsonrpc = "2.0"; id = 1; method = "tools/call"
  params = @{
    name = "set_project_slack"
    arguments = @{
      projectId         = "<demo-project-uuid>"
      workspaceId       = "<workspace-id-or-subdomain>"
      defaultChannelId  = "<channel-id>"
      botTokenSecretRef = "file:///run/secrets/slack_demo"
    }
  }
} | ConvertTo-Json -Depth 10 -Compress
Invoke-RestMethod -Method Post -Uri $mcp -Body $body -ContentType "application/json"
```

Field meanings:
- `projectId` — UUID of the project (`list_projects` returns these).
- `workspaceId` — the Slack workspace ID (`T01…`) or subdomain. Stored as metadata only; the actual API call uses the bot token.
- `defaultChannelId` — Slack channel ID (`C…`) the bot posts to by default.
- `botTokenSecretRef` — `file://` URL resolving to the in-container path of the secret. The `FileSlackBotTokenResolver` reads this on each post, so rotating the token is just rewriting the file.

A `plans.primary_slack_channel_id` override is supported if a specific plan should post somewhere other than the project default.

## 6. Verify

Trigger a notification by claiming + starting work on any task on that project. For example:

```powershell
# (claim first via claim_specific_task with role="developer", capture the claimToken)
$body = @{ jsonrpc="2.0"; id=1; method="tools/call"; params=@{
    name="start_work"; arguments=@{ claimToken = "<token>" } } } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post -Uri $mcp -Body $body -ContentType "application/json"
```

Within ~2 seconds (the `SlackNotifyWorker` polls every 2s), a message should appear in your channel. To inspect the outbox state directly:

```powershell
docker compose -f deploy/docker-compose.yml exec -T postgres `
  psql -U workstream -d workstream -x `
  -c "SELECT notification_type, channel_id, result, error, attempts, slack_ts FROM slack_notify_log ORDER BY id DESC LIMIT 5;"
```

A row with `result = success` and a populated `slack_ts` confirms the round trip worked.

## Notification taxonomy (which events post)

Each plan type's `default_notify_on` array decides which events produce Slack notifications. Defaults are in [`deploy/profiles/audit.json`](../deploy/profiles/audit.json) and [`deploy/profiles/development.json`](../deploy/profiles/development.json). The list per project can be overridden by setting `project_slack.notify_on`.

| Event | Audit default | Dev default |
|---|---|---|
| `task.created` | off | on |
| `task.claimed` | on | on |
| `task.in_progress` | on | on |
| `task.review` | on | on |
| `task.done` | on | on |
| `task.blocked` | on | on |
| `finding.confirmed` | on | n/a |
| `finding.rejected` | off | n/a |
| `fix.confirmed` | on | n/a |
| `fix.failed` | on | n/a |
| `plan.activated` | on | on |
| `plan.completed` | on | on |

## Actor identity (who a post is attributed to)

Every task/finding post carries an identity resolved from the **calling actor's `actor_type`**:

- `human` — the post shows the person's display name, role label, a `Human` tag, and their GitHub avatar (`github.com/<login>.png`) in the attachment author row.
- `orchestrator` / `subagent` — the post shows the plan-type's role persona for that event (audit: Auditor → Smith, Verifier → Jones, Fixer → Brown, Fix-Verifier → Davis; development: Developer, Reviewer), an `AI Agent` tag, and a role-coloured bar.

The persona and colour come from the plan profile's `role_personas`, keyed via `slack_roles` (notification type → role). An AI agent that runs a plan must therefore have its own `orchestrator`/`subagent` workstream user — running it under a human's URL token attributes every automated step to that person instead of the role persona.

## Threading

Multi-step lifecycles (a task moving through `claimed` → `in_progress` → `review` → `done`) thread under a single parent post. The first message for an entity is the root; subsequent messages reply in the thread. `slack_notify_log.thread_ts` carries the parent slack_ts so threading survives worker restarts.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `slack_notify_log` row stuck at `result=retry`, `error=invalid_auth` | Bot token wrong or revoked. Check `cat deploy/secrets/slack-<slug>` matches the App's Bot User OAuth Token. |
| `error=channel_not_found` | Bot isn't a member of the channel, or channel id is wrong. Re-`/invite` and re-check the channel ID. |
| `error=not_in_channel` | Same as above. |
| `error=missing_scope` | Add the missing scope on api.slack.com → OAuth & Permissions, then **Reinstall to Workspace** (Slack will give you a new token; replace the secret). |
| No row in `slack_notify_log` at all | The event type isn't in the plan type's `default_notify_on` (or `project_slack.notify_on`), or the project has no `project_slack` row. |
