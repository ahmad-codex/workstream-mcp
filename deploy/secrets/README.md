# Local secrets — NOT committed

Drop the following files into this directory before running `docker compose up`:

| File | What it is |
|---|---|
| `pg_password`        | Postgres password for the `workstream` user |
| `admin_token`        | Static bearer for `/admin/*` endpoints |
| `gh_app_key.pem`     | GitHub App private key (PEM) |
| `gh_webhook_secret`  | Secret used to verify GitHub webhook signatures |
| `slack-<project>`    | Per-project Slack bot token; referenced by `bot_token_secret_ref` |

The repository's `.gitignore` excludes everything in this directory except this README. In
production, the same files are mounted from the host (e.g. `/etc/workstream/secrets/...`)
via the compose `secrets:` block.
