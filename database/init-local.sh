#!/usr/bin/env bash
# Initializes a FRESH local PostgreSQL database with the full app-platform schema, in the
# order database/README.md specifies: roles and default privileges, then each service's
# generated migrations (as ap_owner, so views and tables end up owned the way the grant
# model expects), then the grants, then verification.
#
# NOT idempotent. 01-schemas.sql creates the schemas unconditionally and fails on a second
# run against the same database — that is deliberate (see its header).
# Run this once per fresh database, same as a real deployment would.
#
# Usage:
#   PGDATABASE=appplatform ./database/init-local.sh
#
# Connects with plain `psql` and whatever PGHOST/PGPORT/PGUSER/PGPASSWORD you already have
# in your environment (or a matching ~/.pgpass), as a superuser able to CREATE ROLE and
# CREATE SCHEMA. Create the database first if it doesn't exist yet: `createdb appplatform`.

set -euo pipefail
cd "$(dirname "$0")"

: "${PGDATABASE:?Set PGDATABASE to the target database name, e.g. PGDATABASE=appplatform}"

echo "==> privileges/00-roles.sql (superuser; idempotent)"
psql -v ON_ERROR_STOP=1 -f privileges/00-roles.sql

echo "==> privileges/01-schemas.sql (superuser, once)"
psql -v ON_ERROR_STOP=1 -f privileges/01-schemas.sql

for schema in platform core tickets ledger; do
  echo "==> ${schema}/migrations-all.sql (as ap_owner)"
  psql -v ON_ERROR_STOP=1 -c "SET ROLE ap_owner;" -f "${schema}/migrations-all.sql"
done

echo "==> privileges/02-grants.sql (superuser)"
psql -v ON_ERROR_STOP=1 -f privileges/02-grants.sql

echo "==> privileges/99-verify.sql (zero rows below means the boundary holds)"
psql -f privileges/99-verify.sql

cat <<'EOF'

Schema applied.

Runtime and migration roles (ap_platform_rt, ap_core_rt, ap_tickets_rt, ap_ledger_rt, and
their _migrate counterparts) were created with NO PASSWORD, so none of them can log in
yet — deliberate, per 00-roles.sql's header. Before pointing an API at this
database, set a password for the role it connects as, e.g.:

  psql -c "ALTER ROLE ap_core_rt PASSWORD 'pick-your-own';"

then set that API's ConnectionStrings__<Name> env var to a connection string using that
role and password against this database.
EOF
