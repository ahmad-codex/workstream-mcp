#!/usr/bin/env bash
# verify-mcp — quick sanity-check a workstream MCP URL before adding it to Claude (§14.5).
# Usage:  ./scripts/verify-mcp.sh https://mcp.wrkstream.xyz/<your-token>

set -euo pipefail

URL="${1:-}"
if [[ -z "$URL" ]]; then
  echo "usage: $0 <mcp-url>"
  exit 2
fi

# Hit the bare /<token> endpoint. On success the server returns a small JSON page that
# names the actor (§10.3).
status=$(curl -sS -o /tmp/workstream-status -w "%{http_code}" "$URL")
if [[ "$status" != "200" ]]; then
  echo "URL did not resolve (HTTP $status). Check the URL and try again."
  exit 1
fi

echo "OK. Server identifies you as:"
cat /tmp/workstream-status | python3 -m json.tool || cat /tmp/workstream-status
