#!/usr/bin/env bash
# #133: one-shot ownership change, NEVER a long-lived API startup hook.
# Requires trusted bootstrap libpq env or protected .pgpass/PGSERVICE.
# This script does not accept or print a password.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
role="${GITHUBMIGRATOR_F061_OWNER_ROLE:?Supply a dedicated NOLOGIN owner role name}"
if [[ ! "$role" =~ ^[a-z][a-z0-9_]{2,62}$ ]]; then
  echo "Invalid F-061 owner role identifier" >&2
  exit 2
fi
if ! command -v psql >/dev/null 2>&1; then
  echo "PostgreSQL psql required" >&2
  exit 2
fi

# Role selector is never trusted for auth: the SQL checks current and
# session user against pg_roles. Entire owner transfer and role create
# must succeed/rollback together; no plaintext role credentials.
{
  printf "SET LOCAL githubmigrator.f061_review_owner = '%s';\n" "$role"
  cat "$ROOT/script/postgres_f061_second_review_owner.sql"
} | psql -X --set ON_ERROR_STOP=1 --single-transaction --file -
