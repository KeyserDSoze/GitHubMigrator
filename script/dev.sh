#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMMAND="${1:-up}"
MODE="${2:-local}"
if [[ "$MODE" == "test" ]]; then
  ENV_FILE="$ROOT/.env.test"
elif [[ -f "$ROOT/.env.local" ]]; then
  ENV_FILE="$ROOT/.env.local"
else
  echo "No .env.local found; using committed test values. Copy .env.local.example to .env.local to customize." >&2
  ENV_FILE="$ROOT/.env.test"
fi

compose() { docker compose --env-file "$ENV_FILE" -f "$ROOT/docker/docker-compose.yml" "$@"; }
case "$COMMAND" in
  up)
    compose up -d --build postgres azurite observability api frontend
    compose ps
    ;;
  down)
    compose down
    ;;
  reset)
    echo "WARNING: this permanently deletes local Compose volumes." >&2
    compose down -v
    ;;
  logs)
    compose logs -f --tail=150
    ;;
  ps)
    compose ps
    ;;
  test)
    compose up -d --build postgres azurite observability api frontend
    compose --profile test run --build --rm backend-tests
    compose --profile test run --rm frontend-tests
    ;;
  *)
    echo "Usage: $0 {up|down|reset|logs|ps|test} [local|test]" >&2
    exit 2
    ;;
esac
