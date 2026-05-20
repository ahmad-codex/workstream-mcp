-- Raise audit-profile role TTLs so verifier and fix_verifier subagents can finish
-- their Hetzner-routed reproductions before the claim expires. The old 15m TTL was
-- shorter than a typical verifier run (which builds Crbrl, ships artifacts via the
-- hetzner MCP, deploys, reproduces, and captures invariant evidence). When the
-- claim expired mid-run, submit_verification_verdict / submit_attempt / submit_findings
-- all failed with stale_claim and the work could not be written back.
--
-- The new 120m floor covers the longest verifier we have measured (98 min for the
-- F-R-6-1 TurboQuant fidelity reproduction including the source-tarball ship). The
-- canonical source is deploy/profiles/audit.json; this migration keeps existing
-- deployments aligned.
--
-- Subagents that finish quickly still release the claim early via submit_*, so the
-- raised TTL only affects the worst case.

BEGIN;

UPDATE plan_types
SET role_ttls = $${
  "auditor":      "120m",
  "verifier":     "120m",
  "fixer":        "120m",
  "fix_verifier": "120m"
}$$::jsonb
WHERE id = 'audit';

-- The migration runner keys "applied" off this row existing; without it the
-- runner re-executes 0010 on every startup (harmless but wasteful).
INSERT INTO __migrations (version) VALUES ('0010_raise_audit_role_ttls')
ON CONFLICT (version) DO NOTHING;

COMMIT;
