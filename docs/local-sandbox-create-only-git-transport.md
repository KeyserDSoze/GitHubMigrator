# F-048 — real Git create-only transport to a persistent **local** bare target

F-048 is a genuine Git object and ref transfer, not an HTTP or CLI mock.
It deliberately supports **only a trusted, pre-provisioned local bare
repository**. It cannot receive GitHub URLs, install tokens, passwords,
repository names as paths, user-authored refspecs or generic Git arguments.

**Important:** F-048 is a new isolated adapter and integration-test surface.
It is **not wired to the F-047 write-barrier worker or an API that accepts
an execution request**. Production GitHub writes remain impossible.

## Execution contract

The internal `ILocalGitCreateOnlyTransferRunner` accepts:

- the server-generated, review-only F-044 packet (all remote-write and
  authorization flags must be false);
- the F-038 on-disk bundle view and the canonical complete source ref set;
- the exact canonical F-044 create-candidate set and baseline local-target
  ref set, with matching F-044 candidate SHA-256;
- an opaque `SandboxId` (UUID). The destination is derived as
  `<trusted sandbox root>/<uuid N format>.git`. No arbitrary path or URL
  can be selected.

The runner is **disabled by default** (`enabled=false`). No production
dependency-injection registration or HTTP route enables it.

### Step-by-step, real Git

1. Recalculate the safe create-only plan: all candidates are the exact
   `source − target` set; matched refs have identical OIDs; unexpected
   target refs are preserved; target-only refs count must match F-044.
   SHA-1 and SHA-256 Git object formats cannot be mixed. Cap at **16** refs.
2. Independently hash and size-verify the stored Git bundle through
   `LocalGitBundleArtifactStore`. Reject tampering before opening Git.
3. Validate the trusted absolute roots, target bare repository, objects,
   refs, configuration, symlink ancestry and object format. The target must
   already exist. A striped in-process mutex and a POSIX filesystem lock protect a target
   both within one worker replica and *across processes*; the process-level
   semaphore allows two transfers to different targets concurrently.
4. Clone the verified bundle into a fresh, unpredictable temporary **source**
   bare mirror and run `git fsck --full --strict` on that source. Compare
   every source head/tag and object ID to the signed request.
5. Re-read the entire destination ref set while locked and require it to
   equal the expected baseline exactly, including all pre-existing
   source-matching and target-only refs. Any collision or drift aborts
   before transmission.
6. Execute a **single** `git push --porcelain --atomic` with
   `--force-with-lease=<full-ref>:` for *every* missing ref, explicitly
   expecting each destination to **not exist**. Source OIDs are supplied
   directly; no `+` refspecs, deletion or force-overwrite fallback.
7. Independently re-read all destination refs and compare them to the
   entire exact expected union. Re-run `git fsck --full --strict` on the
   target and rehash the source bundle after the push. Only then issue a
   domain-separated SHA-256 sealed receipt with exact source/baseline/
   created/final ref fingerprints.

Every Git subprocess has a bounded runtime/output, cleared inherited
environment, no shell, no user config, no external protocol, no prompt
and hooks suppressed. The scratch mirror is removed after the operation;
the bare target remains intentionally persistent to test repeat calls.

The receipt explicitly reports
`localSandboxWritesPerformed=true` and
`remoteWritesPerformed=false`, `executionAuthorized=false`,
`readyForExecution=false`. No receipt is an authorization to push to
GitHub.

## Fail-closed uncertain outcome

After *entering* the Git push phase, any rejected push, timeout, lost
acknowledgement or verification failure is
`SandboxOutcomeRequiresReconciliation`. There is **no automatic retry**.
A separate read-only `InspectAsync` independently queries the local bare
repository, runs a strict target-object Git fsck, and returns either `ExpectedLocalRefsObserved` or
`LocalSandboxReconciliationRequired` with the observed refs digest and
candidate match count. It never clears an execution gate or retries a push.

A successful, repeated call is **not** interpreted as a successful
CREATE: its baseline no longer matches and it is rejected before another
push. A concurrent other runner cannot overwrite existing refs because the
filesystem lock serializes cooperating workers and the exact absent-ref
leases reject collisions.

## Tests and remaining boundary

Real Linux Git integration tests construct an annotated release tag,
feature branch, matching main branch and target-only ref; verify full
object transfer, SHA receipts, ref preservation, git fsck, target drift
rejection, parallel push contention, a deliberately lost response *after*
real push, subsequent read-only reconciliation without retry, corrupted
bundle, disabled feature and symlinked destination.

A later F-049+ must implement **separate** GitHub App execution authority,
repo allowlisting, target-wide durable write fencing (not the F-045 preview
lease), secure remote Git transport, ambiguous-result journal, provider
ruleset revalidation and independent remote OID verification before
production GitHub writes. F-048 does not make these claims.
