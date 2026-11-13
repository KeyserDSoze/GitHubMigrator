# Azure DevOps Server test environment

`docker compose up -d` (or `./script/dev.ps1 up`) starts **ado-sim**, an Azure DevOps Server-compatible service with a deterministic, pre-populated estate. A real Windows VM running Azure DevOps Server Express is available as an optional profile.

| | ado-sim (default) | ado-server (profile) |
| --- | --- | --- |
| What it is | .NET service (tools/AdoSim) exposing the REST subset the migrator reads (pipelines, service connections, policies, pull requests, ...), plus real git repositories over smart HTTP (fetch and push) | Windows Server 2022 in `dockurr/windows`, Azure DevOps Server Express installed by script |
| Needs | Docker | Linux + `/dev/kvm`, or Windows 11 with nested virtualization; 10 GB RAM, 100 GB disk |
| Start | `./script/dev.ps1 up` | `./script/dev.ps1 ado-server` (first boot ~1 h) |
| URL from the API container | `http://ado-sim:8080/tfs` | `http://ado-server:8080/tfs` |
| Credentials | PAT `sim-pat` (`ADO_SIM_PAT`) | PAT written to `C:\OEM\lab-pat.txt` (see below) |
| Verified here | yes (21 product tests + 139 simulator tests + container healthcheck) | **no**: scripts follow Microsoft's documented installer/`TfsConfig unattend`; adjust the failing stage using `C:\OEM\setup.log` |

## Server and Services flavours

`ADO_SIM_FLAVOR` (`server`, default, or `services`) selects which Azure DevOps the simulator imitates; the estate is the same.

| | `server` | `services` (stand-in for dev.azure.com) |
| --- | --- | --- |
| Core REST and git | `{origin}/tfs/{collection}/...` | `{origin}/{organization}/...` |
| Identities | Identities API (`/_apis/identities`) | Graph API under `{origin}/vssps/{organization}/_apis/graph` (users, groups, `Memberships/{descriptor}`, continuation tokens, `ADO_SIM_GRAPH_PAGE`); `legacyDescriptor` links Graph subjects to the descriptors ACEs use |
| Product provider | `azure-devops-server` | `azure-devops-services` |
| Default max api | 7.0 | 7.1 |

The product reaches the stand-in only through `Providers:AzureDevOpsServices:CoreRoot|GraphRoot|GitRoot|AllowedHosts|AllowHttp` (`AzureDevOpsServicesEndpoints`). These overrides need all three roots, allow-listed hosts, and are **refused outside the Development environment** (the API does not start): in production Services is always Microsoft's cloud. `./script/dev.sh lab-services` starts the lab that way.

The Graph shape, `legacyDescriptor` linking and the other Services assumptions are documentation-based: confirm them with the [live smoke tests](live-smoke-tests.md).

## Connecting the migrator

Connections -> provider **Azure DevOps Server**: server URL (`http://ado-sim:8080/tfs`), collection `DefaultCollection`, PAT. Only hosts in `Providers__AzureDevOpsServer__AllowedHosts` are ever contacted (compose default: `ado-sim,ado-server,host.docker.internal`); link-local addresses are always refused. `AllowHttp`/`SkipTlsValidation` are lab settings (TLS skip is ignored outside Development).

## Choosing an estate

`ADO_SIM_SEED` selects what the simulator serves (compose passes it through, default `default`):

| Value | Estate |
| --- | --- |
| `default` (or unset) | the original estate, `tools/AdoSim/seed/default-seed.json` (also consumed by the VM script) |
| `orion` | the "Orion Commerce" estate with real pipeline YAML, templates and integrations (below) |
| a file path | your own seed; a `files/` folder next to it is searched first for `filesFrom` folders |

`ADO_SIM_BULK="projects,reposPerProject,pipelinesPerRepo[,commitsPerRepo]"` adds generated projects to whichever seed is loaded and overrides a `generate` block of the seed. Example: `ADO_SIM_BULK=50,10,3` adds `Bulk 001` ... `Bulk 050`, each with `bulk-001-repo-01` ... `bulk-001-repo-10`, three simple valid YAML pipelines per repository (`pipelines/ci-1.yml` ...) and 3 commits per repository (the fourth value changes that). Generation is deterministic: names, pipeline ids (from 10001) and SHAs are the same on every run. Everything is guarded against typos (more than 20000 repositories is refused).

```bash
ADO_SIM_SEED=orion docker compose up -d ado-sim
ADO_SIM_SEED=orion ADO_SIM_BULK=20,5,2 docker compose up -d ado-sim   # orion plus 20 generated projects
```

Other knobs: `ADO_SIM_PAT` (default `sim-pat`), `ADO_SIM_REFS_PAGE` (default 1000; a smaller value forces ref pagination), `ADO_SIM_MAX_API` (default 7.0; newer api-versions are rejected with HTTP 400 like an older server), `ADO_SIM_DATA` (data directory, `/data` in the image).

### Seed hash and persisted volumes

`<data>/seed.sha256` holds the hash of the effective seed (after `generate`/`ADO_SIM_BULK`), the bytes of every embedded `filesFrom` file and the repository builder version. When it differs at start (or the marker is missing) `<data>/repos` and `<data>/work` are wiped and every repository is rebuilt; with the same hash the existing repositories are reused, including commits created through the write API or `git push`. So changing the seed on a volume that already exists now takes effect. `POST /_sim/reset` does the same wipe-and-rebuild at runtime.

### Determinism

Authors, dates, messages and file contents come from the seed only, so the same seed gives the same SHAs on every machine (the default seed produces exactly the SHAs of the first simulator version). The write API stamps commits with `Sim Author` and a date one hour after the newest commit or tag of the repository (never before 2024-06-01), so a given call sequence gives the same SHAs too.

## What the default seed contains

- Projects: Git with 6 repos (empty, disabled, dotted and spaced names, annotated tags, nested branches, per-repo ACL with Deny, non-inherited ACL, orphan ACL principal), unicode project/branch/tag names, TFVC project with nested branch roots, same repo name in two projects, a repo with 150 branches + 30 tags (ref pagination), an empty project.
- Identities: 7 users (one without email, two with the same display name), nested groups.
- Boards: Epic -> Feature -> Story -> Task chain, bug with commit and PR links (the pull request 301 it points at exists), HTML/script description, a link cycle. Pipelines: YAML with tasks/template, secret variables, variable group, service connections.

Commits use fixed dates/authors, so SHAs are reproducible. `GET /_sim/expected` returns the counts a correct discovery must find (it is computed on every call, so it also follows commits and pushes); tests compare against it. It now also reports `yamlPipelines`, `classicPipelines`, `serviceEndpoints`, `variableGroups`, `environments`, `policies`, `hooks`, `pullRequests` and `runs`.

## The orion estate

`ADO_SIM_SEED=orion`: collection `DefaultCollection`, three Git projects, 13 repositories, 10 users (the default 7 plus `svc-build` without mail, `frank`, `gina`), 11 groups with nested membership (Contributors, Readers, Build Administrators, Release Managers per project; one Deny ACL on `orion-api`, one non-inherited ACL on `orion-infra`, an orphan principal on `orion-archive`). Expected totals: 51 refs, 10 YAML + 1 classic pipelines, 12 service connections, 5 variable groups, 6 environments, 12 policies, 5 hooks, 9 pull requests, 32 runs, 15 work items.

| Project / repository | Content | Git shape |
| --- | --- | --- |
| Orion Commerce / `orion-api` | .NET 8 service, `Dockerfile`, tests; two pipelines | 14 commits, 6 branches (one has an extra pipeline file that exists only there), 2 lightweight + 2 annotated tags, Deny ACL |
| `orion-web` | React, 3 Node versions | 40 commits, 5 branches, `public/images/hero.png` (PNG), `data/catalog-snapshot.bin` (1.5 MB binary) |
| `orion-lib` | NuGet library `Orion.Core` | 15 commits, `develop`, `release/1.4`, tags |
| `Orion.Contracts` | contracts package (dots in the name) | 5 commits |
| `orion-windows` | WPF client | 8 commits |
| `orion-legacy` | .NET Framework site, **no YAML** (classic definition only), default branch `master` | 6 commits |
| `orion mobile app` | React Native shell (spaces in the name) | default branch of 80 characters, branches `feature/приветствие`, `feature/日本語-ロケール`, `feature/rocket-🚀`, tags `mobile-0.8`, `v0.9.0-β` |
| `orion-docs` / `orion-archive` | empty repository / disabled repository | |
| Orion Platform / `orion-templates` | step, job, stage and variable templates | tags `v1`, `v2`, annotated `v3` |
| `orion-infra` | Terraform and Bicep, two pipelines | non-inherited ACL |
| Orion Sandbox / `sandbox-playground`, `orion data tools` | scratch pipeline; a YAML file that was never registered as a pipeline | |

Integrations (all names are cross-checked against the YAML by the tests):

- Service connections: `azurerm` (`orion-azure-dev`, `orion-azure-prod`, `orion-azure-sandbox`), `dockerregistry` (`orion-acr`), `github` (`orion-github`), `ssh` (`orion-ssh-web`), `sonarqube` (`orion-sonarqube`), `npm` (`orion-npm-feed`), `externalnpmregistry` (`orion-npm-registry`), `externalnugetfeed` (`orion-nuget-feed`), `generic` (`orion-webhook-deployer`, not ready), `kubernetes` (`orion-aks-prod`). Each project sees only the connections shared with it. Responses carry names, ids, urls, authorization scheme and non-secret parameters; secret parameters are listed as `null`, never with a value.
- Variable groups: `orion-shared-config`, `orion-api-secrets`, `orion-web-secrets`, `orion-keyvault-prod` (type `AzureKeyVault`, `providerData` with the vault and the `orion-azure-prod` connection), `orion-infra-config`. Secret variables have no value.
- Environments: `dev`, `staging`, `prod` (approval by Release Managers + branch control), `infra-dev`, `infra-prod` (approvals), `sandbox`.
- Branch policies on `main` of `orion-api` (minimum reviewers, build validation of pipeline 2001, work item linking, comment resolution, required reviewers) and `orion-web`, plus `orion-lib`, `orion-infra`, `orion-templates`.
- Service hooks: webhook (`git.push`), Slack (`build.complete`), Teams-style webhook (`git.pullrequest.created`), a platform webhook and one disabled hook. Slack/Teams targets are masked (`********`).
- Pull requests 1001-1009 (active, draft, completed, abandoned) between existing branches, linked to work items; 32 runs with `succeeded`/`failed`/`canceled`; Epic/Feature/User Story/Bug/Task work items linked to commits and pull requests.

### Pipelines and how hard they are to convert to GitHub Actions

| Id | Pipeline | Level | Why |
| --- | --- | --- | --- |
| 2007 | `Orion.Contracts CI` | easy | three `script` steps, list-style `trigger` |
| 3003 | `orion-templates Validation` | easy | scripts, `pr:` list |
| 4001 | `sandbox hello` | easy | `trigger: none`, a string parameter, `pool: name: Default` |
| 2004 | `orion-lib Pack and Publish` | medium | .NET and NuGet tasks have action or CLI equivalents; `name:` run-number format, `##vso[build.updatebuildnumber]`, `$(Build.BuildId)` / `$(Build.SourceBranchName)` and an internal feed connection need mapping |
| 2002 | `orion-api nightly scan` | medium | cron `schedules` with `always: true`, SonarQube tasks bound to a service connection |
| 2001 | `orion-api CI-CD` | medium/hard | four stages, `trigger`/`pr` filters with `batch`, variable groups plus a variables template, parameters with `values`, a local template and `template: ...@templates` from another project, Docker@2 with a registry connection, `deployment` jobs with `environment` + `runOnce`, `AzureWebApp@1` / `AzureCLI@2`, runtime (`$[ ]`) expressions and `condition:` |
| 3002 | `orion-infra Bicep` | medium/hard | `dependsOn` with several stages, parameters with `values`, `${{ if }}` steps, environments with approvals |
| 2003 | `orion-web CI-CD` | hard | `matrix` over three Node versions, `Cache@2` key syntax, `schedules`, a **self-hosted pool with `demands`**, SSH and npm service connections, a GitHub-type repository resource, a variables template from another repository |
| 3001 | `orion-infra Terraform` | hard | compile-time `${{ each }}` / `${{ if }}` / `${{ else }}` that generate stage names and variables, `checkout: self` + `checkout: templates`, an extension task (`TerraformInstaller@1`), approvals, a Kubernetes connection |
| 2005 | `orion-windows Desktop CI` | hard | `windows-latest`, `VSBuild@1`, `VSTest@2`, `PowerShell@2`, a **container job with `services:`**, `resources.containers` with a private registry, a **pipeline resource trigger** |
| - | `orion-api` branch-only `azure-pipelines-experimental.yml` | medium | exists only on `feature/loyalty/points`, uses a stage template pinned to the `v3` tag |
| 2006 | `orion-legacy Build` | not convertible automatically | classic (designer) definition: no YAML, `process.type` 1 with phases and steps, a schedule trigger and a secret variable |

YAML in the seed is real Azure Pipelines YAML (`tools/AdoSim/seed/files/<repo>/**`). It uses Microsoft-hosted `vmImage` names because that is what a converter has to map, although a real Azure DevOps Server only has self-hosted agents.

## Seed format additions

Repositories accept (all optional, the old fields are unchanged):

| Field | Meaning |
| --- | --- |
| `filesFrom` | folder under `tools/AdoSim/seed/files/` (embedded; or next to a seed file) written into the first commit, paths and LF endings preserved, dot-directories included |
| `files` | inline `{ path: text }`, written after `filesFrom` (wins on conflicts) |
| `branchFiles` / `branchFilesFrom` | files that exist only on a branch (`{ branch: { path: text } }` / `{ branch: folder }`); the branch is created if `branches` does not list it |
| `blobs` | `[{ path, size, kind }]` deterministic pseudo-random content (`binary`, `png`, `text`) so big files never live in git |
| `authors`, `start`, `historyDir` | rotate commit authors over seed users; first commit date (default 2024-01-01); folder of the synthetic `fileN.txt` history files (default `src`) |

A seed has optional `variableGroups` (with `projects`, `keyVault`, `keyVaultEndpoint`), `serviceEndpoints` (with `scheme`, `url`, `projects`, `data`, `parameters`), `environments` (with `checks`), `policies`, `hooks`, `pullRequests`, `runs`, classic pipeline definitions (`pipelines[].classic`) and `generate`. See `tools/AdoSim/Seed.cs` for the exact records and `orion-seed.json` for a complete example. A connection or group without `projects` is visible in every project (the default seed predates scoping).

## REST surface (Azure DevOps Server 2022, api-version up to `ADO_SIM_MAX_API`)

All under `/tfs/{collection}`, PAT as Basic auth (401 otherwise); lists use `{ count, value }`; `$top` + `X-MS-ContinuationToken` page where noted.

| Endpoint | Notes |
| --- | --- |
| `GET {project}/_apis/pipelines`, `/{id}` | `id`, `name`, `revision`, `folder`; detail has `configuration.type` (`yaml` / `designerJson`), `path`, `repository { id, type: azureReposGit }`; `$top`, `orderBy` |
| `GET {project}/_apis/pipelines/{id}/runs`, `/runs/{runId}` | newest first; detail adds `resources.repositories.self { refName, version }` (the version is resolved against the repository) |
| `GET {project}/_apis/build/definitions`, `/{id}` | `name` (wildcard), `path`, `definitionIds`, `includeAllProperties`; YAML: `process { type: 2, yamlFilename }`; classic: `process { type: 1, phases[].steps[] }` with task id/displayName/inputs (credential-looking inputs are blanked), `repository`, `triggers`, `queue`, `variables`; `variableGroups` is the legacy list of group ids the product reads |
| `GET {project}/_apis/serviceendpoint/endpoints`, `/{id}` | filters `type`, `endpointNames`, `endpointIds`, `authSchemes` |
| `GET {project}/_apis/distributedtask/variablegroups`, `/{id}` | `groupName` (wildcard), `ids`, `$top`, `queryOrder` (default `IdDescending`), `continuationToken` (last id of the previous page); key-vault groups have `providerData` |
| `GET {project}/_apis/distributedtask/environments`, `/{id}` | `name`, `$top` |
| `GET {project}/_apis/pipelines/checks/configurations?resourceType=environment&resourceId={id}` | approval and branch-control checks; `settings` only with `$expand=settings`, like the real API (`7.0-preview.1` is accepted) |
| `GET {project}/_apis/policy/configurations`, `/{id}` | filters `repositoryId`, `refName`, `policyType` |
| `GET _apis/hooks/subscriptions`, `/{id}` | filters `publisherId`, `eventType`, `consumerId`, `consumerActionId` (collection level) |
| `GET {project}/_apis/git/repositories/{repo}` | adds `defaultBranch`, `remoteUrl`, `webUrl` (the list gained `defaultBranch` and `remoteUrl`) |
| `GET .../pullrequests?searchCriteria.status=active\|completed\|abandoned\|all`, `/{id}`, `/{id}/threads`, `/{id}/workitems` | `searchCriteria.sourceRefName` / `targetRefName`, `$top`, `$skip`; `lastMergeSourceCommit` / `lastMergeTargetCommit` follow the refs |
| `GET .../items` | the product call (`path`, `$format=json`, `includeContent=true` -> `{ path, content }`) is unchanged; also `$format=text`, `download=true`, `includeContent`, `versionDescriptor.version` + `.versionType=branch\|tag\|commit`, `scopePath`, `recursionLevel=None\|OneLevel\|Full` (`value[]` of `{ objectId, gitObjectType, commitId, path, isFolder }`, the folder itself first); binary files return no `content` in JSON |
| `GET .../commits`, `/commits/{id}`, `/stats/branches` | `searchCriteria.itemVersion.version(+versionType)`, `.itemPath`, `.author`, `.fromDate`, `.toDate`, `.$top`, `.$skip`; branch stats take `name` and `baseVersionDescriptor.version` |

Existing endpoints (projects, repositories, refs, identities, ACLs, work items, TFVC branches) are unchanged.

## Write path for incremental tests

```bash
# a real commit (PAT required); creates the branch from createBranchFrom when it does not exist, optional lightweight tag
curl -u :sim-pat -H 'Content-Type: application/json' \
  -d '{"branch":"main","message":"add file","files":{"docs/new.md":"# new\n"},"delete":["old.txt"],"createBranchFrom":"main","tag":"v9"}' \
  http://localhost:8081/_sim/repos/Orion%20Commerce/orion-api/commits
# -> 201 { "commitId": "...", "branch": "main", "refs": [ { "name": "refs/heads/main", "objectId": "..." }, ... ] }

git push http://x:sim-pat@localhost:8081/tfs/DefaultCollection/Orion%20Commerce/_git/orion-api HEAD:refs/heads/my-branch
curl -u :sim-pat -X POST http://localhost:8081/_sim/reset      # back to the pristine seed, returns the expected totals
```

Errors: 400 (missing branch/message, unknown `createBranchFrom`, invalid ref or path), 401, 403 (disabled repository, also for `git push`), 404, 409 (tag exists). An empty repository accepts its first commit on any branch. `git push` uses `git receive-pack` over smart HTTP and accepts any update (no branch policies are enforced). The simulator does not merge: completed pull requests keep their source branches.

Differences worth knowing: the commit and ref state is real git, everything else (pull requests, runs, policies, ...) is read-only seed data; `git clone` of a disabled repository still works (only REST and writes are refused, as before).

## Windows VM notes

- `install.bat` -> `setup.ps1` (git, Express installer, `TfsConfig unattend`, firewall, seed). Stages are idempotent markers in `C:\OEM\state`; follow `docker compose logs -f ado-server` and the console at http://localhost:8006.
- Group membership uses `TfsSecurity`; work items/links, projects, repos and pushes use REST/git. TFVC *content* and pipeline definitions are not created on the VM (no REST for TFVC branches); use the simulator for those.
- Server has no Graph API: identities come from the Identities API and are rewritten into the Graph shape (`AzureDevOpsEndpoints`). The empty-filter user query (`searchFilter=General&filterValue=`) is the first thing to confirm against the VM.
- PAT creation: the script tries the token API and falls back to a message; create a PAT in the UI (scopes: Code read, Project read, Graph read, Security manage) if needed.

## Tests

`dotnet test tests/integration` runs the simulator in-process (needs only the `git` CLI) and PostgreSQL. In Docker: `./script/dev.ps1 test` also runs the integration suite against a disposable database. On Windows use `Host=127.0.0.1` in `GITHUBMIGRATOR_TEST_POSTGRES`; `localhost` makes each test wait ~50 s on the IPv6 fallback.

`dotnet test tests/simulators/AdoSim.Tests` tests the simulator on its own (no PostgreSQL, no dependency on `src/backend`, about 5 seconds): both profiles, `/_sim/expected` against what the REST API serves, every endpoint (401, api-version gate, filters, paging), `git clone` and `git push`, the commit API and reset, seed-hash behaviour on a persisted directory, bulk generation, determinism, and a YamlDotNet pass over every YAML file in the seed plus a cross-reference check (templates, repository resources, service connections, variable groups, environments, pipeline resources all have to exist in the seed). The project is not part of the solution file yet.

## Found by the simulators

- `AzureDevOpsWorkloadReader` only resolved variable groups listed as bare integers in `build/definitions/{id}`, whereas Azure DevOps lists `VariableGroup` objects (`{ "id": 3, "name": ... }`, [Definitions - Get](https://learn.microsoft.com/en-us/rest/api/azure/devops/build/definitions/get?view=azure-devops-server-rest-7.0)); against a real server the groups were silently ignored. Both shapes are read now and the simulator emits the real one.
- Product tests against the orion estate: `tests/integration/OrionPipelinesWorkloadTests.cs` (the real workload reader reads every pipeline, analyses the YAML and resolves variable groups and service connections).
