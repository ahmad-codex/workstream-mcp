# Roadmap

Known gaps in the current code, roughly in order of usefulness. Each item names where to
start. Open an issue before starting anything marked **larger** so the design can be agreed first.

## Good first issues

These are small, self-contained, and covered by the existing test setup.

1. **Use `WORKSTREAM_PUBLIC_BASE_URL` in token rotation.** `POST /admin/users/{username}/rotate-token`
   builds the returned URL from the request host, while `POST /admin/users` uses
   `WORKSTREAM_PUBLIC_BASE_URL`. Behind a reverse proxy the rotated URL can be wrong.
   Start in `src/Workstream.Api/Endpoints/AdminEndpoints.cs`.
2. **Admin CLI commands for boards.** The server already has `POST /admin/boards/list`,
   `POST /admin/boards/discover` and `POST /admin/plans/{planId}/board`; the CLI has no
   commands for them. Add `workstream-admin board list|discover|bind` in
   `tools/workstream-admin/Program.cs`, with typed request records like `CreateUserBody` and a
   contract test like `AdminCliContractTests`.
3. **Integration test for `StuckWorkJob`.** Seed an expired claim and a stale finding, run one
   iteration, and assert the row in `stuck_work_reports`. Make `RunOnceAsync` reachable from
   tests (for example `internal` plus `InternalsVisibleTo`). See `src/Workstream.Data/StuckWorkJob.cs`.
4. **`release_claim` for findings.** It only releases task claims today; a finding claim can
   only expire. Add the finding path in `ReleaseClaimTool`
   (`src/Workstream.Mcp/Tools/Claims/ClaimTools.cs`) and an integration test.
5. **Publish the API image to GHCR** from a GitHub Actions workflow on tags, so the quick start
   does not need a local build.

## Planned

- **Declarative bootstrap.** `workstream-admin apply bootstrap.yml` only prints the file. It
  should diff `deploy/bootstrap.example.yml`-style YAML against server state and apply changes
  (spec section 10.2). **Larger.**
- **Hash URL tokens at rest.** `users.mcp_url_token` is stored in plain text. Store a SHA-256
  hash and look up by hash, with a migration for existing rows. **Larger**, security-sensitive.
- **Attempt review queue.** `claim_next_attempt_for_review` is a v1 stub; orchestrators pass
  attempt ids directly today.
- **Post the stuck-work report to Slack** through the existing Slack outbox.
- **More plan types** (testing, migration, release) as profiles under `deploy/profiles/`, with
  no C# changes. See [state-machine.md](./state-machine.md).
