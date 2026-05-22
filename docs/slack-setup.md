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

## Interactivity — the "View details" modal

Notification cards carry a **View details** button that opens a modal with the full
task / finding / plan detail. This is **app-level** setup (one Slack app, shared across
every workspace), done once — not per project:

1. Copy the app's signing secret from **api.slack.com → your app → Basic Information →
   Signing Secret** into `deploy/secrets/slack_signing_secret`, then recreate the api
   container so it is mounted.
2. On **api.slack.com → your app → Interactivity & Shortcuts**: toggle Interactivity on
   and set the Request URL to `https://<host>/webhooks/slack/interactivity`, then Save.

The endpoint verifies Slack's request signature (HMAC over `v0:{timestamp}:{body}`, with
a 5-minute replay window) and opens the modal via `views.open`. It is exempt from the
URL-token middleware — the signature is the auth. Without the signing secret configured
every inbound click is rejected with 401 and the button is simply inert.

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

## Card layout

Each notification posts as a **Block Kit card**: a header with a lifecycle emoji, a
two-column field grid (Project / Plan / Task / Status, plus Severity for findings), a
description / reason body, a context line identifying the actor, and a primary **View
details** button. Plan, task and finding notifications all use this layout; the plain
text body is kept as the notification fallback.

## Actor identity (who a post is attributed to)

Every task/finding post carries an identity in the card's context line. By default it
follows the **calling actor's `actor_type`**:

- `human` — the context line shows the person's display name, role label and a `Human` tag.
- `orchestrator` / `subagent` — the context line shows the plan-type's role persona for that event and an `AI Agent` tag.

A tool call may override this with an optional **`as_agent`** field in its `arguments`, naming the role the call acts for (e.g. `"auditor"`). When present, the post renders that role's persona — the role label plus an agent name drawn from a per-role pool — and the `AI Agent` tag, even when the URL token belongs to a human. The name is picked deterministically from the work item's id (task id for auditor/developer posts, finding id for verifier/fixer/fix-verifier), so every post for one item shows the same name while parallel items read as distinct agents. This is how an orchestrator that shares a human's token still posts as the role persona. It is display-only: the `events` table always records the real actor.

The persona and colour come from the plan profile's `role_personas`; without `as_agent` the role is derived from `slack_roles` (notification type → role).

## Posts are top-level

Per operator preference, each state change is its own top-level post — notifications do
**not** thread under a parent. The spec's threaded design (§8.4) is preserved as data
(the first `slack_ts` is still recorded on `slack_notify_log`) but no thread replies are
sent.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `slack_notify_log` row stuck at `result=retry`, `error=invalid_auth` | Bot token wrong or revoked. Check `cat deploy/secrets/slack-<slug>` matches the App's Bot User OAuth Token. |
| `error=channel_not_found` | Bot isn't a member of the channel, or channel id is wrong. Re-`/invite` and re-check the channel ID. |
| `error=not_in_channel` | Same as above. |
| `error=missing_scope` | Add the missing scope on api.slack.com → OAuth & Permissions, then **Reinstall to Workspace** (Slack will give you a new token; replace the secret). |
| No row in `slack_notify_log` at all | The event type isn't in the plan type's `default_notify_on` (or `project_slack.notify_on`), or the project has no `project_slack` row. |
