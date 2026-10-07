# GitHub Projects V2 setup

Per project. Once configured, every task transition pushes the card on a Projects V2 board (Backlog / In Progress / Review / Done) via the outbox worker (§7).

How a task appears on the board depends on whether the project has a registered repo:

- **Repo-backed project** — each task is created as a real GitHub **issue** in the primary repo, assigned to the audit run's milestone, then added to the board. Status changes flip the board column *and* open/close the issue.
- **Repo-less project** — a Projects V2 **draft item** is created lazily on the first transition (the legacy path).

Either way you do not need to pre-populate the board.

## 1. Create the GitHub App (on your org)

For an org-owned board, the App must be created **under the org** so the installation is org-scoped and the App has access to org-level Projects.

1. **`https://github.com/organizations/<ORG>/settings/apps/new`** (replace `<ORG>` with the org slug, e.g. `example-org`).
2. Name: `Workstream`. Homepage URL: any placeholder. Webhook → **uncheck "Active"** for now (webhooks need a public URL — defer until you expose the API behind a hostname).
3. **Permissions**:
   - **Repository permissions** → `Contents: Read-only`, `Issues: Read & write`
   - **Organization permissions** → `Projects: Read & write` *(this is the load-bearing one — the **Repository → Projects** entry is for the legacy V1 boards and does not apply to V2)*
4. **Where can this app be installed** → **Only on this account**.
5. Click **Create GitHub App** → note the **App ID** (numeric, top of the page).
6. Scroll down → **Generate a private key** → downloads `<app>.<date>.private-key.pem`.
7. Top right → **Install App** → choose the org → **All repositories** (or a subset) → **Install**. After install, the URL is `…/settings/installations/<INSTALLATION_ID>` → note the **Installation ID**.

## 2. Create the Projects V2 board

1. `https://github.com/orgs/<ORG>/projects/new` → board view → name it.
2. Make sure it has a **Status** single-select field with options exactly: `Backlog`, `In Progress`, `Review`, `Done`, and optionally `Blocked`. The system maps these column names to internal task states.
3. Note the **project number** from its URL (`/orgs/<ORG>/projects/N`).

## 3. Drop the secrets

```
deploy/secrets/gh_app_key.pem        ← the downloaded PEM (replace placeholder)
deploy/secrets/gh_webhook_secret     ← random hex; placeholder is fine until you wire webhooks
```

## 4. Set env vars

Create / append to `deploy/.env` (gitignored):

```
WORKSTREAM_GH_APP_ID=<app-id>
WORKSTREAM_GH_INSTALLATION_ID=<installation-id>
```

`docker-compose.yml` already references these via `${WORKSTREAM_GH_APP_ID:-0}` etc. so they're picked up automatically.

## 5. Restart the API so the App auth picks up the new credentials

```powershell
docker compose -f deploy/docker-compose.yml up -d --build
```

## 6. Discover the board's node IDs

V2 boards are accessed by GraphQL node ids, not by name. Use the discover endpoint to resolve them in one call:

```powershell
$adminToken = Get-Content deploy/secrets/admin_token -Raw
$body = @{ org = "<ORG>"; projectNumber = <N> } | ConvertTo-Json -Compress
$h = @{ Authorization = "Bearer $adminToken" }
Invoke-RestMethod -Method Post -Uri "http://localhost:8080/admin/boards/discover" `
                  -Headers $h -Body $body -ContentType "application/json"
```

The response gives you:
- `project_node_id` (`PVT_…`)
- `status_field_node_id` (`PVTSSF_…`)
- `status_options` — map of `{ Backlog: <id>, "In Progress": <id>, Review: <id>, Done: <id> }`

If you get an empty list from `/admin/boards/list` or `NOT_FOUND` from `/admin/boards/discover`, see [Troubleshooting](#troubleshooting) — almost always permission acceptance.

## 7. Register the board against a workstream project

```powershell
# add_project_board MCP tool
$mcp = "http://localhost:8080/<your-token>/mcp"
$body = @{
  jsonrpc = "2.0"; id = 1; method = "tools/call"
  params = @{
    name = "add_project_board"
    arguments = @{
      projectId              = "<workstream-project-uuid>"
      githubProjectV2NodeId  = "<from discover>"
      githubProjectNumber    = <N>
      githubOwner            = "<ORG>"
      displayName            = "<board title>"
      statusFieldNodeId      = "<from discover>"
      statusOptionBacklog    = "<from discover>"
      statusOptionInProgress = "<from discover>"
      statusOptionReview     = "<from discover>"
      statusOptionDone       = "<from discover>"
    }
  }
} | ConvertTo-Json -Depth 10 -Compress
$resp = Invoke-RestMethod -Method Post -Uri $mcp -Body $body -ContentType "application/json"
$resp.result.content[0].text | ConvertFrom-Json
```

The returned id is the workstream `board_id`.

## 8. Bind the board to a plan

```powershell
$body = @{ boardId = "<board-uuid-from-step-7>" } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post -Uri "http://localhost:8080/admin/plans/<plan-uuid>/board" `
                  -Headers @{ Authorization = "Bearer $adminToken" } `
                  -Body $body -ContentType "application/json"
```

A plan with `primary_board_id` set will enqueue board-sync rows on every task transition.

## 9. Verify

```powershell
# Claim → start_work on any task in the bound plan. The BoardSyncWorker creates a
# draft item on first sync and stores its node id (PVTI_…) + numeric databaseId on
# tasks.github_board_item_id / .github_board_item_number.
```

Inspect:

```sql
SELECT external_key, status, github_board_item_id, github_board_item_number
FROM tasks
WHERE plan_id = '<plan-uuid>';

SELECT target_column, target_status, result, github_response_id, last_attempted_at
FROM board_sync_log
ORDER BY id DESC LIMIT 5;
```

Within ~2 seconds of `start_work`, a draft item should be visible on the GitHub board in **In Progress**. Subsequent transitions move the same card; the worker reads `github_board_item_id` rather than recreating items.

## Audit milestones

An `audit` plan drives a GitHub repo **milestone** for its run:

- `create_plan` (audit type) enqueues a milestone-create on `milestone_sync_log`. The `BoardSyncWorker` creates the milestone in the project's primary repo, titled `<plan name> (<UTC timestamp>)`. Its `created_at` is the run's start time. The number is stored on `plans.github_milestone_number`.
- Every task on the plan becomes an issue assigned to that milestone.
- `archive_plan` closes the milestone: GitHub's `closed_at` records the end time, the title gains a `— Completed` / `— Canceled` suffix (from the tool's `outcome`), and the reason goes in the description. GitHub milestones have no native "canceled" state, so the outcome is encoded in the title.

The App needs **Repository permission → Issues: Read & write** (already in the permission list above) for issue and milestone writes.

## Lazy item creation

Tasks created **before** a board is registered against the plan still get items — the worker creates the issue (or draft) on the first transition that triggers a sync. You do not need to backfill or re-activate the plan after binding a board.

## How card titles in Slack become deep links

When a plan has a bound board, the `OutboxSlackNotifyEnqueue` adapter resolves `{task_title_link}` to a Slack mrkdwn link pointing at the board (`<URL|title>`). The `SlackNotifyWorker` upgrades that link at dequeue time to the per-item pane URL once `github_board_item_number` is populated:

```
https://github.com/orgs/<ORG>/projects/<N>/views/1?pane=issue&itemId=<DB_ID>
```

If the slack worker dequeues before the board worker has created the draft (the race window is typically <2 s), the slack post is deferred a few seconds and retried — so the *first* notification on a fresh task already contains the deep link.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `/admin/boards/list` returns `{"projects": []}` | App lacks **Organization permission → Projects: Read and write**. Edit the App's permissions on `/settings/apps/<name>`, save, then **Accept new permissions** on the installation page (`/organizations/<ORG>/settings/installations/<INSTALLATION_ID>`). |
| `/admin/boards/discover` returns `NOT_FOUND` for `projectV2` | Either the project number is wrong, or the App can't see the project — same permission fix as above. Verify with the list endpoint first. |
| `board_sync_log.error = "GitHub returned 401"` | The installation token expired and refresh failed. Check `WORKSTREAM_GH_APP_ID` and `WORKSTREAM_GH_INSTALLATION_ID`, then restart the api container so the cached token is dropped. |
| `board_sync_log.error = "GitHub GraphQL errors: …Resource not accessible by integration…"` | Permission accepted, but the App can't write to **this specific board**. Confirm the project is in the org the App is installed on (not under a user). |
| Card created in Backlog instead of the intended column | The `board_column_mapping` in `plan_types.config` doesn't include the target column. Inspect with `SELECT board_column_mapping FROM plan_types WHERE id = '<plan-type>'` and fix via a migration. |
| `target_column = "backlog"` but `target_status = "in_progress"` | Old build: the parser used to ignore the separate `board_column_mapping` column. Pull the latest code (`PostgresPlanTypeCache` merges it now) and rebuild. |
