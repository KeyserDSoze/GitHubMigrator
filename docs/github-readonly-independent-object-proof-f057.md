# F-057 — Independent read-only GitHub full-object mirror proof

F-056 checks that two physically present, trusted **local** bare repositories
have matching, complete reachable Git objects. F-057 adds a new **opt-in,
unregistered internal** GitHub smart-HTTPS fetcher that downloads the live
GitHub destination into a new unpredictable disposable bare mirror, then
invokes F-056 on that copy and the source.

It never issues a Git push, GitHub write API request, ref deletion,
server-side repository creation, retry, or sender authorization.

## Controlled input and authentication

- A source bare snapshot identified only by a server-side UUID under a
  trusted root, a numeric GitHub repository ID, a validated organization
  and repository matching an explicitly enabled target allowlist, a full
  fresh SHA-bound F-031/F-051 typed GitHub ref observation, and the exact
  canonical source branch/tag refs. F-057 refuses target drift, missing
  source refs, case aliases, mixed object-format OIDs, forged receipt SHA,
  stale snapshots and near-expiry installation-token evidence.
- The caller supplies an **ephemeral GitHub App installation token in
  memory only**. Neither token nor HTTP Basic header is persisted in a
  database, Git config, file, receipt, process arguments, error or log.
  The short-lived auth header is injected through the isolated Git child
  process environment. The token is not returned in any DTO.
- Production remote URL is derived ONLY from
  `https://github.com/{validatedOwner}/{validatedRepo}.git`.
  The Git child process is noninteractive, HTTPS-only, disables credential
  helpers and hooks, ignores global/system Git config, disables redirect
  following, disallows other protocols and lazy object fetch.
  No caller-controlled path, Git URL, refspec, command or SSH endpoint.
- Internal integration-test-only constructor is friend-scoped to
  `GitHubMigrator.Integration.Tests` and swaps in a private local bare
  origin, exercising real `git ls-remote`/`git fetch` without GitHub
  network, rate limits or live credentials. It is not available as an
  API or production DI option.

## Verification protocol

1. Recalculate the typed GitHub observation seal, verify that every source
   ref OID is present in the expected target ref list, then establish a
   fresh UUID-named scratch bare repository in a validated absolute root.
2. Execute **read-only** `git ls-remote --refs` against the pinned
   origin and check the *entire heads/tags ref set* against the original
   F-031/F-051 observation (not just the candidate refs).
3. `git fetch` only heads/tags to this disposable bare repository,
   without implicit tags, partial filters, submodules, worktree checkout
   or force/ref deletions. Observe the complete remote refs again, and
   inspect every physical fetched ref's SHA and Git object type. This
   detects forged annotated-tag/commit type claims.
4. F-056 performs real, strict full-object `git fsck` on BOTH the trusted
   source and independent downloaded target, compares sorted SHA-1/SHA-256
   object closure for exactly the source-reachable refs, and repeats the
   object and ref readback to detect local drift. Extra target-only refs
   are preserved and excluded from source-only closure comparison.
5. Run a final complete `ls-remote` to detect a GitHub ref change during
   the fetch/verification window. Recheck the original SHA-bound receipt
   and token expiry; seal a read-only SHA-256 receipt including remote
   typed-ref fingerprint, F-056 closure evidence, identity, format and
   object count. Always delete the temporary mirror on normal/error paths.

## What the proof means

A successful F-057 receipt means **the independent Git smart-HTTPS copy**
obtained by this run contained the same Git objects reachable from the
selected source refs, and that GitHub's reported heads/tags matched the
pinned snapshot before and after this operation. The final observation
is still subject to GitHub changing later. A completed migration or the
safety of auto-resending after F-055 MayHaveTransmitted is NOT inferred.

The receipt permanently sets `RemotePushInvoked=false`,
`RemoteWritesAuthorized=false` and `MigrationComplete=false`.
There is no HTTP endpoint, registered background worker, or production
configuration enabling this runner automatically. F-047/F-050/F-052/F-055
write barriers remain unchanged.

## Explicit limitations before general production deployment

- This phase assumes a short-lived, separately issued and properly scoped
  GitHub App installation token from a trusted caller; it does not mint
  tokens or attest that the token itself has ONLY read permission.
- Git object history closure excludes Git LFS external bytes and Git
  submodule repositories, and does not verify signing-key trust, GitHub
  branch rules, user identity, issues, boards, permissions or releases.
- F-056 enforces a bounded in-memory object set (200k OIDs) and command
  output limits. A large Git `fetch` can consume arbitrary disk/network
  bytes *before* the object ceiling is measured. A production deployment
  must enforce separate container/cgroup disk, memory, CPU and egress
  quotas and use durable cross-worker admission before opening the
  runner to larger targets or executing automatically.
- The remote's ref set can change immediately after final observation;
  there is no remote transactional snapshot lock. Provider visibility
  and object access are limited by the GitHub App installation token.
- Test coverage uses real local Git transport; the GitHub.com HTTPS path
  is not exercised in CI and requires an explicit test-only integration
  environment with an allowlisted repository and read-scoped credential.
  No actual GitHub copy was fetched during development.

## Tests

xUnit covers strict repo-ID/org/repo allowlisting, typed refs, original
SHA seal, token expiry, remote origin pinning, and non-executable proof.
Linux integration exercises the **same actual Git ls-remote/fetch commands**
against private bare origins: SHA-1 and SHA-256 merge/branch/tag history,
a target-only ref, reference drift, forged annotated tag type, credential
injection rejection, missing source, cancelled fetch, symlinked scratch,
no push and temporary directory cleanup. CI also covers PostgreSQL 17,
Playwright, and Terraform.
