#!/usr/bin/env bash
# #133: one-shot, operator-controlled Identity-only grants.
# Requires libpq PGHOST/PGPORT/PGDATABASE/PGUSER/PGPASSWORD (or a
# secure PGSERVICE/PASSFILE). Never include credentials in argv/CI logs.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
role="${GITHUBMIGRATOR_RUNTIME_PGROLE:?Set an existing, restricted runtime LOGIN role}"
if [[ ! "$role" =~ ^[a-z][a-z0-9_]{2,62}$ ]]; then
  echo "Invalid runtime role identifier" >&2
  exit 2
fi
if ! command -v psql >/dev/null 2>&1; then
  echo "PostgreSQL psql client required" >&2
  exit 2
fi

# The literal runtime role is restricted to the regex above; no
# untrusted SQL syntax or secret is injected. --single-transaction
# guarantees the role grants are all-or-nothing.
{
  printf "SET LOCAL githubmigrator.runtime_role = '%s';\n" "$role"
  cat "$ROOT/script/postgres_runtime_identity_grants.sql"
} | psql -X --set ON_ERROR_STOP=1 --single-transaction --file -
