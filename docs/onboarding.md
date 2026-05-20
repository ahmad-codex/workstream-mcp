# Plug-and-play onboarding

The headline workflow (§14): the user adds one URL to Claude's config and is done.

## Operator side — once per developer

```bash
workstream-admin user create --github-username new-dev --display "New Developer" --type human
# Output:
#   User created.
#   URL: https://mcp.trycrbrl.xyz/Yk8j2_aBcDeFgHiJkLmNoPqRsTuVwXyZ1234567890aB
#   Copy this URL exactly. It will not be shown again.
```

Optionally grant permissions:

```bash
workstream-admin user grant --github-username new-dev --permission can_override_verdict
```

## Developer side — one-time

Add to `~/.claude/mcp.json` (or via the Claude UI):

```json
{
  "mcpServers": {
    "workstream": {
      "url": "https://mcp.trycrbrl.xyz/Yk8j2_aBcDeFgHiJkLmNoPqRsTuVwXyZ1234567890aB/mcp",
      "transport": "http"
    }
  }
}
```

Verify the URL before adding it:

```bash
./scripts/verify-mcp.sh https://mcp.trycrbrl.xyz/Yk8j2_aBcDeFgHiJkLmNoPqRsTuVwXyZ1234567890aB
# OK. Server identifies you as:
# { "ok": true, "actor": { "github_username": "new-dev", "is_admin": false }, ... }
```

That's it. No PAT. No OAuth flow. No token prompts inside Claude.

## Daily flow

Developer to Claude: "What can I pick up on the MemTurbo dev plan?"

Behind the scenes Claude calls:

1. `list_plans({ project_id: <memturbo> })` → sees the dev plan id.
2. `get_plan_dashboard(<plan_id>)` → next 5 claimable tasks + their priorities.
3. The developer chooses one; Claude calls `claim_specific_task(<task_id>, "developer")`.
4. `start_work(<claim_token>)` → board card moves to "In Progress", Slack posts "🔧 In progress: …".
5. The developer codes; Claude helps; commit lands.
6. `submit_attempt(<claim_token>, { files_changed, approach_summary, ..., diff_ref: "commit:abc123" })` → board moves to "Review", Slack posts.
7. A reviewer (different actor, same URL pattern) sees the notification, claims and reviews.
8. Reviewer's Claude calls `submit_review_decision(<task_id>, "approved", commit_hash: "abc123")` → board moves to "Done", Slack thread reply confirms.

If the developer's Claude session dies anywhere between steps 4 and 6, the claim expires after its TTL (4h for dev) and the task is reclaimable. The work artifacts survive in git — the MCP server only holds workflow state.

## Audit flow (same surface, different roles)

An orchestrator agent runs against the same URL pattern. Because one URL token can be shared between a human operator and their automated runs, each tool call that posts to Slack may carry an optional `as_agent` field naming the role it acts for (`"auditor"`, `"verifier"`, `"fixer"`, `"fix_verifier"`). The server then renders that role's persona — the role label plus an agent name drawn from a per-role pool (stable per task/finding, so parallel agents in the same role read as distinct) — instead of the token owner's name and avatar. Omit `as_agent` for genuine manual actions so they stay attributed to the person. It calls:

- `claim_next_task(plan_id, "auditor")` to start auditing a feature.
- `submit_findings(claim_token, [...])` to record findings.
- `claim_next_finding_for_verification(plan_id)` to verify findings (different subagent / role).
- `submit_verification_verdict(claim_token, "confirmed", evidence)` to record confirmation.
- `claim_next_finding_for_fix(plan_id)` to fix confirmed findings.
- `submit_attempt` + `submit_attempt_verdict` to close the loop.

All board moves and Slack notifications follow the same outbox-driven path.
