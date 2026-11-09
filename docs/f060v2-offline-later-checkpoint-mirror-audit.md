# #114 — F-060v2 offline audit of an existing later-checkpoint mirror

Tracked by [#114](https://github.com/KeyserDSoze/GitHubMigrator/issues/114),
under [#100](https://github.com/KeyserDSoze/GitHubMigrator/issues/100),
[#96](https://github.com/KeyserDSoze/GitHubMigrator/issues/96)
and [#92](https://github.com/KeyserDSoze/GitHubMigrator/issues/92).

## What exists

`GitIncrementalLaterCheckpointMirrorStagingIntegrity.VerifyOfflineCandidate`
checks the versioned **non-authorizing** F-060v2 staging proof against the
full immutable F-035v2 historical receipt, binding the genuine *later*
F-091 source snapshot, F-097b full SHA-1/SHA-256 ref/OID list, source
repository, F-031 checkpoint target observation, F-038v2 workpack, the
deterministic UUID.git mirror, exact bundle bytes/sha and evidence hashes.
Both hashes are verified; canonical refs are bounded to 1–128 and all
execution, lease, dispatch and migration-success flags are false.

The separately default-**OFF**
`LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor` accepts **only
server-selected identifiers and immutable evidence**, not user-supplied
paths, URLs, Git refspecs or credentials. Given an **already existing**
UUID.git, it:

1. Verifies the historical F-035v2 receipt and the complete staging
   checksum/binding; the *live* F-091/F-097b/F-031 source-locked PostgreSQL
   preflight is still the caller's responsibility.
2. Rehashes the existing workpack UUID.bundle file, rejecting unsafe
   symlinks, missing file, mismatched length or SHA-256.
3. Performs the existing F-056 strict bare-Git repository check, exact
   branch/tag ref/OID comparison, `git fsck` and bounded reachable graph
   closure **twice**. Any corrupt, non-self-contained, changed or missing
   local graph fails closed.
4. Rehashes the bundle **again** and returns a versioned
   `GitIncrementalLaterCheckpointMirrorOfflineAudit` whose SHA-256 binds
   staging proof identity, target observation, source snapshot, F-035v2
   evidence, physical mirror UUID, object graph digest/count and time.
   It does not write a PostgreSQL receipt or event.

Local SHA-1/SHA-256 integration tests use actual Git repositories with
merged commits and annotated tags, check absent mirror, corrupt Git
alternates, bundle modification and forged refs; domain tests check
receipt tamper, flags and lease generation. These fixture tests are **not**
substitutes for a full two-epoch PostgreSQL F-061 workflow.

## Boundaries and next implementation

This **read-only verification** does **not** import or materialize
UUID.git. Existing F-060 test helpers may create a test mirror, but no
production F-060v2 import is exposed by the new class. Do not use a
legacy F-029/F-035 `IsCurrent=true` workaround for a later epoch:
the real baseline plan remains stale.

The SHA-256 is not independent source provenance merely because it is
self-consistent. The live source-locked F-091/F-097b/F-031 evidence reader
must still approve the selected epoch; no F-061 acquisition, generation
fencing, Verified immutable event, quarantine or restart recovery is
implemented here. **Never** reinterpret `DurableEvidencePersisted=false`,
`F061LeaseGeneration=0`, `CanMaterialize=false`, `CanPush=false`,
`CanRetry=false`, `CanDispatch=false` or `MigrationComplete=false` as
authorization.

[#100](https://github.com/KeyserDSoze/GitHubMigrator/issues/100) remains
**OPEN** for real source-locked later-epoch F-060v2 physical import
and F-061 fenced durable lifecycle. [#96](https://github.com/KeyserDSoze/GitHubMigrator/issues/96)
remains **OPEN** for F-095v3 ancestry, and
[#92](https://github.com/KeyserDSoze/GitHubMigrator/issues/92)
remains **OPEN** for full end-to-end source checkpoint proof.
No destination GitHub writes, push, send/retry or completion flags.

## #116 — separately gated offline physical import (no F-061 admission)

The existing `LocalGitSourceSnapshotMirrorMaterializer` now exposes a
**distinct, default-OFF** internal
`MaterializeLaterCheckpointAsync(stage, durable, ct)` method (constructor
switch `laterCheckpointEnabled`, independent of legacy `enabled`).
It does **not** reuse the legacy F-029 `IsCurrent` claim or edit its
historical bundle/receipt format. Unlike `AuditAsync` above, this method
**does write** an isolated, local-only UUID.git directory; it never pushes
to GitHub or contacts a remote Git server.

The two paths share the same hardened *filesystem and Git implementation*:
fully trusted bundle root; symlink ancestry checks; POSIX shared-volume
lock; per-process concurrency gate; local-only `file` protocol Git import
with sanitized environment; exact canonical refs and source object OIDs;
strict self-contained `git fsck` / full reachable closure verification
before and after atomic `UUID.git` promotion; no final-repo overwrite;
and bundle checksum/length checks before and after Git operations.
If an already existing UUID.git is present, the path only verifies it;
it never deletes or repairs physical evidence.

Before importing, the v2-specific boundary verifies the F-060v2 staging
SHA-256 against the historical F-035v2 receipt (including exact F-091
later snapshot, F-097 ref/OID list, checkpoint F-031 target, F-038v2
workpack and immutable F-035v2 physical file SHA). The filesystem method
returns only the versioned #114 **non-authorizing offline audit** with
`DurableEvidencePersisted=false`, `F061LeaseGeneration=0`, and
`CanMaterialize/CanPush/CanRetry/CanDispatch/MigrationComplete=false`.

**Critical trust limit:** this is a local sandbox producer consuming a
*previously verified* stage/receipt, not a runtime source-locked admission.
The stage SHA by itself cannot establish the latest source epoch, human
approval, lease ownership, or PostgreSQL provenance. No API, background
worker, GitHub transport, F-061 SQL lease or immutable Verified row is
registered by #116. Production use remains **blocked** until parent
[#115](https://github.com/KeyserDSoze/GitHubMigrator/issues/115)
integrates the live F-091/F-097b/F-031 lock and F-061 generation-fenced
lifecycle, rechecks the current context after Git, and writes an
independently verified append-only event. This is why
[#100](https://github.com/KeyserDSoze/GitHubMigrator/issues/100)
remains open.

Real local Git SHA-1/SHA-256 tests create a local bundle, then independently
replay and concurrently verify the same UUID.git; they reject v2 disabled,
legacy disabled, stale baseline rebinding, forged flags, altered receipt,
cancelled import, symlinked root, absent file and changed bundle bytes.
The tests do not grant runtime PostgreSQL F-061 authorization.
