#!/bin/sh
# pgbouncer entrypoint for the workstream-mcp stack.
# Reads the Postgres password from /run/secrets/pg_password, generates
# /tmp/pgbouncer/{userlist.txt,pgbouncer.ini}, then execs pgbouncer.
#
# We're invoked as root (compose sets `user: "0"` on the service) so we can
# read /run/secrets/pg_password — Docker mounts secrets as root:root 0400 on
# Linux. We write the userlist + ini, chown to postgres, then su-exec down to
# the postgres user because pgbouncer refuses to run as root.
#
# Auth type is `plain` because the API and pgbouncer share a Docker network — the
# client→pgbouncer hop never crosses an untrusted boundary. The pgbouncer→postgres
# hop still negotiates SCRAM via libpq (pgbouncer hands the plain password to
# Postgres and Postgres performs the SCRAM exchange).

set -eu

PG_PASS=$(cat /run/secrets/pg_password)

CFG_DIR=/tmp/pgbouncer
mkdir -p "$CFG_DIR"

cat > "$CFG_DIR/userlist.txt" <<EOF
"workstream" "$PG_PASS"
EOF
chmod 600 "$CFG_DIR/userlist.txt"

cat > "$CFG_DIR/pgbouncer.ini" <<EOF
[databases]
workstream = host=postgres port=5432 dbname=workstream

[pgbouncer]
listen_addr = 0.0.0.0
listen_port = 6432
auth_type = plain
auth_file = $CFG_DIR/userlist.txt
pool_mode = transaction
max_client_conn = 500
default_pool_size = 25
reserve_pool_size = 5
reserve_pool_timeout = 3
server_reset_query = DISCARD ALL
ignore_startup_parameters = extra_float_digits,search_path
admin_users = workstream
stats_users = workstream
log_connections = 0
log_disconnections = 0
EOF

# Hand ownership of the generated config to the postgres user so su-exec'd
# pgbouncer can read the auth file.
chown -R postgres:postgres "$CFG_DIR"

# Busybox `setpriv` doesn't support --reuid/--regid and the image has neither
# su-exec nor gosu, so we drop privileges with `su` and `exec`-replace the
# wrapping shell so pgbouncer ends up as the supervised process.
exec su -s /bin/sh postgres -c "exec pgbouncer $CFG_DIR/pgbouncer.ini"
