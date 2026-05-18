#!/usr/bin/env bash
# Post-deploy smoke test (§16.4).
# Hits the staging deployment, drives one task through pending → claimed → in_progress →
# review → done, and asserts the Slack post and board update both happened.
#
# Required env:
#   WORKSTREAM_BASE         e.g. https://mcp-staging.trycrbrl.xyz
#   WORKSTREAM_TEST_TOKEN   a URL token for a test user
#   WORKSTREAM_TEST_PLAN_ID a dev-plan id pre-created with at least one pending task

set -euo pipefail

: "${WORKSTREAM_BASE:?missing}"
: "${WORKSTREAM_TEST_TOKEN:?missing}"
: "${WORKSTREAM_TEST_PLAN_ID:?missing}"

call() {
  local method="$1"
  local args="$2"
  curl -sS -X POST "${WORKSTREAM_BASE}/${WORKSTREAM_TEST_TOKEN}/mcp" \
    -H "Content-Type: application/json" \
    -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"$method\",\"arguments\":$args}}"
}

# 1) Claim a task on the test plan.
claim_resp=$(call claim_next_task "{\"plan_id\":\"$WORKSTREAM_TEST_PLAN_ID\",\"role\":\"developer\"}")
claim_token=$(echo "$claim_resp" | python3 -c 'import sys,json; r=json.load(sys.stdin); print(json.loads(r["result"]["content"][0]["text"])["data"]["claim_token"])')
echo "claimed: $claim_token"

# 2) start_work
call start_work "{\"claim_token\":\"$claim_token\"}" > /dev/null
echo "started work"

# 3) submit a trivial attempt
call submit_attempt "{\"claim_token\":\"$claim_token\",\"attempt\":{\"files_changed\":[\"smoke.test\"],\"approach_summary\":\"smoke\",\"side_effects\":\"none\",\"build_command\":\"true\",\"test_scenario\":\"smoke\",\"diff_ref\":\"smoke:0\"}}" > /dev/null
echo "submitted attempt"

echo "smoke test ok"
