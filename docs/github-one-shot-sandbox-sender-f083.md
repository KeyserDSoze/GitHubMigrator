# F-083 — guarded, one-shot HTTPS GitHub sandbox sender

## Scope and activation

F-083 adds a **real implementation of GitHub HTTPS Git create-only push**
and an internal orchestrator that consumes F-082's atomic PostgreSQL
possible-send barrier **before the first outbound Git push**. The code is
composed in API dependency injection but **is not mapped to any HTTP route,
hosted worker or scheduled dispatcher**. `GitHubSandboxOneShot:Enabled`
is **false by default**. F-083 is an opt-in, internal-only sandbox
experiment, NOT a public remote migration launch control.

Before any controlled sandbox experiment, operators must configure:

- `GitHubSandboxOneShot:Enabled=true` AND exact
  `AllowedRepositoryId`, `AllowedOrganization`, `AllowedRepository`.
- F-055 `GitHubRemoteTransmitHazard` enabled for that same numeric repo.
- F-043 `GitHubAppWriteCredential` enabled with exact approved App,
  installation, secret reference, and allowed test repository.
- F-035 physical Git bundle enabled with trusted `BundleRoot`.
- Absolute, private, precreated `GitHubSandboxOneShot:ScratchRoot` on Linux,
  with Git at `/usr/bin/git`.

**Setting these switches alone does not start a send**: the internal
`GitHubSandboxOneShotSender.SendOnceAsync` must be deliberately invoked
with only the confirmed F-081 intent and plan IDs plus an independent
operator. It is not exposed via web, queue or API. Full production
activation and unattended scheduling remain outside F-083.

## Safety protocol

1. Fail-closed configuration and exact numeric repository / case-insensitive
   org/repo allowlist check before network or secret reads.
2. Rebuild sealed F-044 packet and candidate list from server stores; get
   F-035 bundle by persisted workpack ID and verify physical SHA and size.
3. F-043/F-072 mint a **one-repository, Contents:write-only installation
   token** after validating the real GitHub App installation organization,
   numeric repository and non-archived/disabled target. Token stays inside
   a typed internal callback; never in an HTTP response, durable audit,
   Git command-line argument or remote URL.
4. Read live GitHub refs via authenticated GET, compare the entire typed
   head/tag fingerprint with F-055 baseline, verify every create candidate
   is absent; inspect candidate rulesets; GET refs again and refuse drift.
5. Rebuild F-044, compare packet+candidate SHA and rehash on-disk bundle
   **after** provider GETs. These checks do not consume F-082 on failure.
6. F-082 acquires advisory repository locks **732911 → 733050**, checks
   current F-081/F-080/F-078 and F-055/F-052/F-050/F-047/F-045 state,
   then commits irreversible `MayHaveTransmitted` **with its event and
   immutable F-082 receipt** before Git transport can begin. Every
   second invocation, crash recovery attempt or lost acknowledgment
   is refused, even for the same operator.
7. Only after F-082 commit, the writer checks the SHA-sealed receipt,
   re-verifies the local bundle, mirrors it to a private scratch directory,
   runs `git fsck --full --strict`, confirms candidate OIDs, and calls
   real Git with `push --porcelain --atomic`,
   `--force-with-lease=refs/heads/<name>:` (empty expected remote ref)
   and explicit `<oid>:<ref>` for no more than 16 heads/tags.
   It targets exclusively `https://github.com/{approvedOrg}/{approvedRepo}.git`.
   There is no `--force` fallback, non-atomic fallback or retry.
8. Git runs as an argument-list-only subprocess, with stripped environment,
   `GIT_TERMINAL_PROMPT=0`, no system/global config or hooks, HTTPS-only
   protocol for the push, disabled redirects, TLS certificate checks and
   a scoped HTTPS Authorization extraHeader **only in child environment**.
   Git stdout/stderr are bounded and discarded from error responses.
9. A successful Git exit is NOT migration completion. F-053 fetches the
   typed remote ref set and commit/tag tips via GET and runs exact-ref +
   tip-object reconciliation. Only the state
   `SandboxRemoteTipsObservedNeedsClosureAudit` is possible on a fully
   matching tip observation. A failed push, timeout, process crash or
   failed GET returns/requires
   `SandboxRemoteOutcomeRequiresReconciliation`. Both statuses have
   `CanRetry=false`, `MigrationComplete=false`.

The F-083 process-local duplicate guard is merely defense in depth;
the durable across-replica guarantee is F-082's unique hazard and
transactional state. An aborted push **never resets** the marker.

## Verification and known boundaries

Offline .NET tests use an injected token scope, GitHub GET reader,
rulesets, F-044 source, on-disk verifier, PostgreSQL marker and writer
to verify execution order, two GET snapshots, policy drift refusal,
bundle tampering, forced writer failure, no automatic retry and required
F-053 exact-ref/tip proof. PostgreSQL integration tests verify that the
real F-083 baseline adapter consumes F-082 once and all repeated attempts
are refused. Git adapter offline tests fail closed before any process when
the feature is disabled, the receipt is forged or the token is invalid.
Existing F-048/F-071 integration tests exercise actual local bare Git
create-only push and object `fsck`.

**CI deliberately does not push to GitHub**: executing F-083 against a
real test-repo with a temporary approved installation token requires a
separate controlled environment and explicit operator authorization.
GitHub may reject atomic push or installed rules; there is no fallback.
Real-remote operational validation and full Git object-closure proof
remain separate future hardening steps. F-083 never claims the migration
is complete.

Stacked on F-082; `main` is unchanged.
