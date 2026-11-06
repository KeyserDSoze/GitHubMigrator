# F-060 — Verified F-038 source bundle → atomic bare Git source mirror

F-059 could select F-029/F-014's frozen source snapshot but assumed that
an operator had already provisioned a bare repository at
`<trustedSourceRoot>/<plan.SnapshotId:N>.git`. That assumption was unsafe
for plans containing more than one source repository and did not establish
that the local object store actually came from a stored Git bundle.

F-060 closes this gap with an **independent, default-disabled local
materialization** step that consumes only an already verified, persisted
**F-038 source bundle** and publishes an exact, read-only full-history
Git mirror under a unique, deterministic UUID.

## Provenance chain

`F-029 immutable plan + snapshot → F-014 frozen source refs →
F-038 current bundle evidence and physical SHA-256 → F-060 real Git
bare import and object-set hash → F-059 provider observation →
F-057 independent target Git fetch / F-056 full history →
F-058 immutable reconciliation journal`.

The F-059 plan reader now retrieves the unique current
`TransferGitHistory` step for the target, its corresponding F-039
workpack and a current F-038 `GitSourceBundleView` from the existing
PostgreSQL evidence store. It refuses missing, ambiguous, expired,
cross-repository or no-longer-current workpacks/bundles. The original
F-014 SHA of provider source refs must equal the SHA bound to the
persisted bundle, and the expected refs and count must agree.

**Two different repositories in one F-029 snapshot MUST NOT share a
physical directory.** F-060 derives `PhysicalMirrorId` from the ordered
pair `(plan.SnapshotId, bundle.WorkpackId)` with an explicit
domain-separated SHA-256. F-059 preserves `SourceSnapshotId` as the
F-029 snapshot GUID and passes only the derived physical mirror ID
into F-057. The source plan fingerprint now covers that ID, workpack,
source repository, reconciliation, SHA of the complete bundle, its
byte size and complete source ref digest.

## Local import safety and crash semantics

1. Require the separate `GitHubSourceSnapshotMirror:Enabled` flag;
   F-060 is also blocked unless `GitSourceBundles:Enabled` is true.
   F-059's own opt-in flag and F-058's numeric target allowlist remain
   independently required.
2. Validate absolute trusted bundle and mirror roots; reject symlink
   ancestry, overlapping roots and unapproved `refs/notes` or other
   namespaces. Accept only the F-038-generated
   `<workpackId:N>.bundle` filename, never a caller-supplied path.
3. Rehash the complete persisted bundle bytes against the PostgreSQL
   SHA-256/length using `IGitSourceBundleArtifactVerifier`, including
   the storage safety rules already implemented in F-038.
4. Serialize materialization within the process and with a
   **POSIX advisory file lock** across worker processes sharing the
   same lock-capable volume. The lock is scoped to the physical
   mirror root. The lock file never contains credentials.
5. Use `git init --bare` and `git fetch` **only from the verified
   local bundle** into a random staging directory. Allow file protocol
   only; disable system/global Git config, credential helpers,
   prompts, hooks, lazy fetching and object replacement.
   Never execute `git push`, upload to a remote or invoke GitHub.
6. Reuse **F-056** strict complete-ref readback, `git fsck --full
   --strict`, SHA-1/SHA-256 object-format verification,
   no-shallow/promisor/alternate/symlink checks and bounded full
   reachable Git-object-set fingerprinting. Hash the object set and
   rehash the original bundle once more after import.
7. Atomically rename the validated temporary bare directory into
   `<physicalMirrorId:N>.git`, never overwriting an existing
   directory. Re-run the same full-object verification after rename.
   On failure, only this call's random temp directory is deleted.
8. If the final mirror already exists from a previous replica or a
   crash after rename, **never overwrite it**. Rehash the F-038
   bundle and re-run F-056 full-object and ref checks. If corrupted,
   refuse reuse and require operator review.
9. Return a SHA-sealed internal receipt containing F-029 snapshot,
   F-038 workpack/bundle, verified source refs and the **actual Git
   object-set hash and count**. No URL/token, remote push,
   write authority, automatic retry or migration-complete flag.

F-059 now calls F-060 **before any GitHub network I/O**, and calls it
again after independent F-057 remote fetch and before F-058 journal
commit. The physical object proof must remain unchanged; no
successful remote receipt is recorded when the source changes or a
bundle is damaged during verification.

## Configuration

All features are OFF by default:

- `GitSourceBundles:Enabled=false`; `BundleRoot` points to a
  trusted local/volume root containing F-038 evidence bundles.
- `GitHubSourceSnapshotMirror:Enabled=false`.
- `GitHubTrustedRemoteRead:Enabled=false`,
  `SourceRoot=""` (physical mirrors), `ScratchRoot=""`.
- `GitHubRemoteObjectReconciliation:Enabled=false`, with exact
  numeric repository ID and `org/repo` allowlist.

The service is registered **only internally**. No route, queue,
background worker, remote GitHub send, impersonation, Git credential
or automatic retry is introduced. Existing F-055
`MayHaveTransmitted` remains permanently non-retriable.

## Limitations

- A POSIX advisory lock requires a shared lock-capable filesystem for
  multi-process coordination. For cloud workers on independent
  volumes a future PostgreSQL/lease-backed artifact storage layer
  should coordinate materialization; the implementation does not
  claim cross-host atomicity across unrelated disks.
- The physical file and F-038 bundle are reused only after rehashing
  and complete Git object/ref proof. Untrusted OS-level writers who
  can race or replace the protected trusted root remain outside this
  trust boundary; a sealed immutable read-only volume is preferred.
- F-056 limits verification to 200k reachable objects; F-038 limits
  bundles to 512MiB. Disk/cgroup quotas, interrupted-copy cleanup
  across crashes, LFS/submodules and GitHub.com read-only smoke
  tests are still needed for large production repositories.
- The F-060 receipt is not a durable **new** PostgreSQL row: it is a
  deterministic, independently recomputable proof over F-029,
  F-014 and F-038 already persisted source evidence and the actual
  local Git object set. F-058 still records only remote
  reconciliation, not a physical mirror lifecycle history.

## Verification

- Domain: stable per-workpack physical IDs, cryptographic source
  ref/bundle/object fingerprints, SHA tampering, forbidden refs,
  write-disabled flags.
- Real Linux Git: SHA-1 and SHA-256 commit/merge/tree/blob/tag
  bundle imports; F-060 physical source used by real F-057
  read-only full-object mirror acquisition; six concurrent
  materializers, restart reuse, two workpacks sharing a snapshot,
  damaged bundle and damaged pack rejection, symlink and
  cancellation denial. PostgreSQL F-059 provenance tests remain.
- Existing .NET 10 xUnit, PostgreSQL 17 integration,
  React/Playwright and Terraform gates run on the PR.
