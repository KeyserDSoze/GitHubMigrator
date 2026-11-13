# GitHub test environment (gh-sim)

`tools/GhSim` is a GitHub Enterprise Server-shaped simulator: the **migration target** for local and end-to-end tests. It is the counterpart of [`ado-sim`](ado-test-environment.md) (the source). Both are real HTTP services backed by real bare git repositories, so tests exercise real refs, objects, SHAs and `git push`.

Final verification against github.com stays an online step: the same code path runs with the default configuration (no endpoint override), only the base URL and token differ.

## Pointing the migrator at it

Every GitHub reader and the GitHub App credential flow take their endpoints from `GitHubEndpoints` (`Business/GitHubEndpoints.cs`). Without configuration that is `https://api.github.com` + `https://github.com`, exactly as before. An override is operator configuration only and must pass the egress allow-list used for self-hosted sources:

| Setting | Meaning | Example (compose) |
| --- | --- | --- |
| `Providers:GitHub:ApiRoot` | REST root | `http://gh-sim:8080/api/v3` |
| `Providers:GitHub:WebRoot` | web/git root | `http://gh-sim:8080` |
| `Providers:GitHub:AllowedHosts` | allowed hosts (required with an override) | `gh-sim` |
| `Providers:GitHub:AllowHttp` | plain HTTP, honoured only in Development | `true` |

`ApiRoot` and `WebRoot` must be set together; a host that is not allow-listed (or link-local, e.g. `169.254.169.254`) makes the API refuse to start. In compose the three values come from `GITHUB_API_ROOT`, `GITHUB_WEB_ROOT`, `GITHUB_ALLOWED_HOSTS` and are empty by default; `./script/dev.sh lab` (or `dev.ps1 lab`) sets them to the `gh-sim` service and starts the whole lab (ado-sim as source, gh-sim as target).

The git transports use the same endpoints: `GitHubSandboxCreateOnlyPushRunner`, `GitHubReadOnlyObjectMirrorRunner` and `GitHubReadOnlyObjectMirrorIntegrity.RemoteUrl` derive the remote, the `http.<root>/.extraheader` scope and `GIT_ALLOW_PROTOCOL` (`https`, or `http` only for an allow-listed plain-HTTP endpoint) from `GitHubEndpoints`. With no override the environment is byte-identical to before.

## Service

| | |
| --- | --- |
| Start | part of `./script/dev.sh up` (service `gh-sim`, host port `GH_SIM_PORT`, default 8083) |
| Auth | `Authorization: Bearer <token>`, `token <token>` or HTTP Basic (git: the password is the token) |
| Seed | `tools/GhSim/seed/default-seed.json`, selected with `GH_SIM_SEED` (`default` or a file path) |
| Data | `GH_SIM_DATA` (`/data`, volume `gh_sim_data`); state survives restarts while the seed hash is unchanged, a different seed rebuilds the estate |
| Determinism | seeded repositories use a fixed identity/date: same seed, same commit SHAs |

Seed contents: users `sim-admin` (`gh-sim-pat`, owner of `orion-org`), `sim-member` (`gh-sim-member-pat`) and `sim-readonly` (`gh-sim-readonly-pat`, cannot push); organizations `orion-org` (members may create private repositories) and `locked-org` (they may not); repositories `existing-repo` (name collision), `archived-repo`, `protected-repo` (ruleset blocking deletion and non-fast-forward on the default branch) and `configured-repo` (an Actions secret and a variable already present).

## What is simulated

REST under `/api/v3`, using GitHub's documented shapes (snake_case, explicit nulls, `Link` pagination, `X-RateLimit-*`), because the product's readers validate them strictly:

- account/org: `/user`, `/orgs/{org}`, `/user/memberships/orgs/{org}`, `/repositories/{id}`
- repositories: create (`POST /orgs/{org}/repos`, name/case/policy validation), get, patch (default branch, archive), delete, branches, tags, commits
- Git data: `matching-refs` (annotated tags reported as type `tag`), `git/ref(s)` create/update/delete with fast-forward checks, `git/commits|tags|trees|blobs`
- contents: get (base64/raw/dir), put/delete with `sha` conflict rules, real commits on a branch (creating the first commit of an empty repository)
- pull requests (create, list — filtered by `state`, `head=owner:branch` and `base` —, get, patch, real merge/squash) and issues (+comments), sharing the number space like GitHub
- Actions: repository secrets (public key, **sealed-box size validation: plaintext uploads are rejected**), variables, environments (+ environment secrets/variables by repository id), workflows discovered from `.github/workflows` on the default branch
- rulesets (`/rulesets`, `/rules/branches/{branch}`), enforced on REST writes **and on pushes**
- GitHub App: JWT (RS256, signature checked against the registered public key), `/app`, `/app/installations/{id}`, `POST .../access_tokens` (one-repository, `contents` read/write, `metadata: read`, 60-minute expiry, never more than the installation grants). Installation tokens see only their repositories and cannot manage secrets
- git smart HTTP (`/{org}/{repo}.git`): real `git upload-pack` / `git receive-pack`, `fsck` on receive, gzip bodies, the first pushed branch becomes the default of an empty repository. Anonymous 401, hidden 404, read-only credential 403
- push rules are enforced by a `pre-receive` hook against the real incoming objects (`GH013` messages, atomic pushes rejected as a whole)

## Test and diagnostics endpoints (`/_sim`, authenticated)

| Endpoint | Use |
| --- | --- |
| `GET /_sim/state` | repositories with refs, workflows, secret/variable names, counts |
| `GET /_sim/requests?method=&path=&limit=` | every request served, with the principal: prove what a client did (e.g. read-only phases); `DELETE` clears it |
| `POST /_sim/faults` | `{pathRegex, method?, status, message?, rateLimited?, remaining?}` injected failures and rate limits; `DELETE` clears |
| `POST /_sim/apps` | register a GitHub App at runtime (`id`, `slug`, `publicKeyPem`, `installations`) with a freshly generated key pair |
| `POST /_sim/repos/{org}/{repo}/rulesets` | add a ruleset at runtime |
| `POST /_sim/reset` | back to the seeded estate |

## Known differences from github.com

No GitHub Actions execution, webhooks, teams, collaborators, branch protection (classic) or code scanning; no 100 MB per-file push limit (only `receive.maxInputSize`, `GH_SIM_MAX_PUSH_BYTES`); git protocol v0 only; repository names with spaces become hyphens, other invalid characters are rejected (github.com may rewrite them); secret values are never decrypted, only their sealed-box size is checked. Where the simulator was written from documentation it can differ in edge cases: **confirm any behaviour a migration decision depends on online.**

## Tests

- `dotnet test tests/simulators/GhSim.Tests` — 22 contract tests, only the `git` CLI required (no PostgreSQL)
- `tests/integration/GitHubSimReaderTests.cs` — the product's own GitHub readers and the GitHub App inspector against the simulator (17 tests, no PostgreSQL)
- `tests/integration/GitTransportsAgainstSimsTests.cs` — the real git transports over HTTP, no mocks: Azure DevOps fetch, guarded create-only push with a real GitHub App installation token, read-only closure verification, replay refusal and an atomic push rejected by a ruleset
- `tests/integration/GitHubDestinationAgainstSimApiTests.cs` — the real API host (PostgreSQL) configured only through `Providers:GitHub:*`; a destination connection is created, verified and access-checked, and the request log proves it only read

### Found by the simulator

`GitHubTraceabilityReader` treated GitHub's `422 "No commit found for SHA"` for an absent commit as "lookup unavailable" instead of "not found"; it now reports the commit as absent (any other 422 stays unknown).
