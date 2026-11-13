#!/usr/bin/env bash
# Smoke test of the product adapters against REAL Azure DevOps Services, GitHub and/or a model endpoint.
# See docs/live-smoke-tests.md. Nothing is written to Azure DevOps or GitHub; the model endpoint only receives fixed prompts.
#
#   export GITHUBMIGRATOR_LIVE_ADO_ORG=my-org        GITHUBMIGRATOR_LIVE_ADO_PAT=...   # Azure DevOps Services
#   export GITHUBMIGRATOR_LIVE_GH_ORG=my-github-org  GITHUBMIGRATOR_LIVE_GH_PAT=...    # GitHub (optional)
#   export GITHUBMIGRATOR_LIVE_AI_ENDPOINT=https://api.openai.com/v1 GITHUBMIGRATOR_LIVE_AI_MODEL=... GITHUBMIGRATOR_LIVE_AI_KEY=...  # a model (optional; it sends a few fixed prompts)
#   ./script/live-smoke.sh            # real services (tests without their variables are skipped)
#   ./script/live-smoke.sh dry-run    # same tests against tools/AdoSim (services flavour) and tools/GhSim
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPORT="${GITHUBMIGRATOR_LIVE_REPORT:-$ROOT/live-smoke-report.json}"
export GITHUBMIGRATOR_LIVE_REPORT="$REPORT"

if [[ "${1:-}" == "dry-run" ]]; then
  WORK="$(mktemp -d)"; PIDS=()
  cleanup() { for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null || true; done; rm -rf "$WORK"; }
  trap cleanup EXIT
  dotnet build "$ROOT/tools/AdoSim/AdoSim.csproj" -v q >/dev/null
  dotnet build "$ROOT/tools/GhSim/GhSim.csproj" -v q >/dev/null
  dotnet build "$ROOT/tools/LlmSim/LlmSim.csproj" -v q >/dev/null
  ADO_SIM_SEED=orion ADO_SIM_FLAVOR=services ADO_SIM_DATA="$WORK/ado" ADO_SIM_PAT=sim-pat ASPNETCORE_URLS=http://127.0.0.1:18091 \
    dotnet "$ROOT/tools/AdoSim/bin/Debug/net10.0/AdoSim.dll" >"$WORK/ado.log" 2>&1 & PIDS+=($!)
  GH_SIM_DATA="$WORK/gh" ASPNETCORE_URLS=http://127.0.0.1:18092 \
    dotnet "$ROOT/tools/GhSim/bin/Debug/net10.0/GhSim.dll" >"$WORK/gh.log" 2>&1 & PIDS+=($!)
  ASPNETCORE_URLS=http://127.0.0.1:18093 \
    dotnet "$ROOT/tools/LlmSim/bin/Debug/net10.0/LlmSim.dll" >"$WORK/llm.log" 2>&1 & PIDS+=($!)
  for port in 18091 18092 18093; do
    for _ in $(seq 1 60); do curl -fsS "http://127.0.0.1:$port/healthz" >/dev/null 2>&1 && break; sleep 1; done
  done
  export GITHUBMIGRATOR_LIVE_ADO_ORG=DefaultCollection GITHUBMIGRATOR_LIVE_ADO_PAT=sim-pat GITHUBMIGRATOR_LIVE_ADO_SIM_ORIGIN=http://127.0.0.1:18091
  export GITHUBMIGRATOR_LIVE_GH_ORG=orion-org GITHUBMIGRATOR_LIVE_GH_PAT=gh-sim-pat GITHUBMIGRATOR_LIVE_GH_REPO=existing-repo GITHUBMIGRATOR_LIVE_GH_SIM_ORIGIN=http://127.0.0.1:18092
  export GITHUBMIGRATOR_LIVE_AI_SIM=1 GITHUBMIGRATOR_LIVE_AI_ENDPOINT=http://127.0.0.1:18093/v1 GITHUBMIGRATOR_LIVE_AI_MODEL=gpt-sim GITHUBMIGRATOR_LIVE_AI_KEY=llm-sim-key
fi

dotnet test "$ROOT/tests/live/LiveSmoke.Tests/LiveSmoke.Tests.csproj" --logger "console;verbosity=normal"
if [[ -f "$REPORT" ]]; then
  echo; echo "Fidelity report: $REPORT"
  python3 - "$REPORT" <<'PY'
import json, sys
for e in json.load(open(sys.argv[1]))["entries"]:
    print(f"[{e['status']:9}] {e['area']:12} {e['name']:55} {e['detail'][:160]}")
PY
fi
