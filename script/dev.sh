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

if [[ "$ENV_FILE" == "$ROOT/.env.local" ]] && grep -q '^LOCAL_ENCRYPTION_KEY=GENERATE_ON_FIRST_RUN$' "$ENV_FILE"; then
  if command -v openssl >/dev/null 2>&1; then
    KEY="$(openssl rand -base64 32 | tr -d '\r\n')"
  else
    KEY="$(head -c 32 /dev/urandom | base64 | tr -d '\r\n')"
  fi
  sed -i "s|^LOCAL_ENCRYPTION_KEY=GENERATE_ON_FIRST_RUN$|LOCAL_ENCRYPTION_KEY=$KEY|" "$ENV_FILE"
  echo "Generated a unique local encryption key in .env.local." >&2
fi

compose() { docker compose --env-file "$ENV_FILE" -f "$ROOT/docker/docker-compose.yml" "$@"; }
case "$COMMAND" in
  up)
    compose up -d --build postgres azurite observability ado-sim gh-sim llm-sim api frontend
    compose ps
    ;;
  lab|lab-services)
    # Whole lab: source = ado-sim (Azure DevOps Server, or `lab-services`: the Azure DevOps Services stand-in),
    # target = gh-sim (GitHub Enterprise Server shape).
    if [[ "$COMMAND" == "lab-services" ]]; then
      export ADO_SIM_FLAVOR=services ADO_SERVICES_CORE_ROOT="http://ado-sim:8080" ADO_SERVICES_GRAPH_ROOT="http://ado-sim:8080/vssps" \
        ADO_SERVICES_GIT_ROOT="http://ado-sim:8080" ADO_SERVICES_ALLOWED_HOSTS="ado-sim"
    fi
    export GITHUB_API_ROOT="http://gh-sim:8080/api/v3" GITHUB_WEB_ROOT="http://gh-sim:8080" GITHUB_ALLOWED_HOSTS="gh-sim"
    # AI connections may point at llm-sim (http://llm-sim:8080/v1, key ${LLM_SIM_API_KEYS:-llm-sim-key}).
    export AI_ALLOWED_HOSTS="llm-sim"
    compose up -d --build postgres azurite observability ado-sim gh-sim llm-sim api frontend
    compose ps
    echo "Source: http://localhost:${ADO_SIM_PORT:-8081}$([[ "$COMMAND" == "lab-services" ]] && echo "/<organization>" || echo "/tfs") (PAT ${ADO_SIM_PAT:-sim-pat}) | Target: http://localhost:${GH_SIM_PORT:-8083}/api/v3 (PAT gh-sim-pat) | AI model: http://llm-sim:8080/v1 (key llm-sim-key, model gpt-sim)"
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
    compose up -d --build postgres azurite observability ado-sim gh-sim llm-sim api frontend
    compose --profile test run --build --rm backend-tests
    compose --profile test run --rm frontend-tests
    compose --profile test run --build --rm integration-tests
    ;;
  ado-server)
    echo "Starting the Azure DevOps Server VM (first boot downloads Windows and installs the server: ~1h)."
    echo "Console: http://localhost:8006  |  progress: docker compose logs -f ado-server"
    compose --profile ado-server up -d ado-server
    ;;
  *)
    echo "Usage: $0 {up|lab|lab-services|down|reset|logs|ps|test|ado-server} [local|test]" >&2
    exit 2
    ;;
esac
