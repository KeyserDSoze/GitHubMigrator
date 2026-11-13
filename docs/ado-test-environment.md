# Azure DevOps Server test environment

`docker compose up -d` (or `./script/dev.ps1 up`) starts **ado-sim**, an Azure DevOps Server-compatible service with a deterministic, pre-populated estate. A real Windows VM running Azure DevOps Server Express is available as an optional profile.

| | ado-sim (default) | ado-server (profile) |
| --- | --- | --- |
| What it is | .NET service (tools/AdoSim) exposing the REST subset the migrator reads, plus real git repositories over smart HTTP | Windows Server 2022 in `dockurr/windows`, Azure DevOps Server Express installed by script |
| Needs | Docker | Linux + `/dev/kvm`, or Windows 11 with nested virtualization; 10 GB RAM, 100 GB disk |
| Start | `./script/dev.ps1 up` | `./script/dev.ps1 ado-server` (first boot ~1 h) |
| URL from the API container | `http://ado-sim:8080/tfs` | `http://ado-server:8080/tfs` |
| Credentials | PAT `sim-pat` (`ADO_SIM_PAT`) | PAT written to `C:\OEM\lab-pat.txt` (see below) |
| Verified here | yes (21 automated tests + container healthcheck) | **no**: scripts follow Microsoft's documented installer/`TfsConfig unattend`; adjust the failing stage using `C:\OEM\setup.log` |

## Connecting the migrator

Connections -> provider **Azure DevOps Server**: server URL (`http://ado-sim:8080/tfs`), collection `DefaultCollection`, PAT. Only hosts in `Providers__AzureDevOpsServer__AllowedHosts` are ever contacted (compose default: `ado-sim,ado-server,host.docker.internal`); link-local addresses are always refused. `AllowHttp`/`SkipTlsValidation` are lab settings (TLS skip is ignored outside Development).

## What the seed contains

`tools/AdoSim/seed/default-seed.json` (also consumed by the VM script):

- Projects: Git with 6 repos (empty, disabled, dotted and spaced names, annotated tags, nested branches, per-repo ACL with Deny, non-inherited ACL, orphan ACL principal), unicode project/branch/tag names, TFVC project with nested branch roots, same repo name in two projects, a repo with 150 branches + 30 tags (ref pagination), an empty project.
- Identities: 7 users (one without email, two with the same display name), nested groups.
- Boards: Epic -> Feature -> Story -> Task chain, bug with commit and PR links, HTML/script description, a link cycle. Pipelines: YAML with tasks/template, secret variables, variable group, service connections.

Commits use fixed dates/authors, so SHAs are reproducible. `GET /_sim/expected` returns the counts a correct discovery must find; tests compare against it. Larger estates: raise `extraBranches`/`extraTags` or add projects in a copy of the seed (`ADO_SIM_SEED=/path/seed.json`). `ADO_SIM_REFS_PAGE` (default 1000) forces smaller pages, `ADO_SIM_MAX_API` (default 7.0) makes the simulator reject newer api-versions like an older server.

## Windows VM notes

- `install.bat` -> `setup.ps1` (git, Express installer, `TfsConfig unattend`, firewall, seed). Stages are idempotent markers in `C:\OEM\state`; follow `docker compose logs -f ado-server` and the console at http://localhost:8006.
- Group membership uses `TfsSecurity`; work items/links, projects, repos and pushes use REST/git. TFVC *content* and pipeline definitions are not created on the VM (no REST for TFVC branches); use the simulator for those.
- Server has no Graph API: identities come from the Identities API and are rewritten into the Graph shape (`AzureDevOpsEndpoints`). The empty-filter user query (`searchFilter=General&filterValue=`) is the first thing to confirm against the VM.
- PAT creation: the script tries the token API and falls back to a message; create a PAT in the UI (scopes: Code read, Project read, Graph read, Security manage) if needed.

## Tests

`dotnet test tests/integration` runs the simulator in-process (needs only the `git` CLI) and PostgreSQL. In Docker: `./script/dev.ps1 test` also runs the integration suite against a disposable database. On Windows use `Host=127.0.0.1` in `GITHUBMIGRATOR_TEST_POSTGRES`; `localhost` makes each test wait ~50 s on the IPv6 fallback.
