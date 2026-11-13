# F-061v2: source-first, non-authorizing later-checkpoint admission read (#117)

Tracked in [#117](https://github.com/KeyserDSoze/GitHubMigrator/issues/117);
parent [#115](https://github.com/KeyserDSoze/GitHubMigrator/issues/115)
under [#100](https://github.com/KeyserDSoze/GitHubMigrator/issues/100).
Changes go directly to `main` without PR or additional branches.

## What this scoped step implements

`PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight` is
registered as a scoped internal service, independently default **OFF**
(`GitIncrementalLaterCheckpointMirrorAdmission:Enabled=false`).
It does not create any public endpoint, background job, lease, Git
subprocess, GitHub remote transport or SQL evidence write.

Given only server-selected IDs, exact source repository, expected immutable
checkpoint F-031 receipt SHA and original bundle actor, it:

1. Reconstructs the latest genuine F-091/F-097b **full source ref/OID**
   and independently approved, current checkpoint-specific F-031 via the
   separately gated v2 staging preflight, including real UUID.bundle
   file SHA-256 and length. The original F-029 baseline stays stale.
2. Reads the F-035v2 immutable historical record and events from PostgreSQL,
   independently rechecking the actual on-disk bundle bytes.
3. Acquires *short-lived transaction-scoped* PostgreSQL advisory locks,
   SOURCE connection **first** (`732911`), plan **second** (`732990`),
   consistent with the local F-035v2 writer. While locked it compares the
   exact immutable F-032v2/F-038v2 workpack and F-035v2 evidence row:
   source snapshot, checkpoint, target observation, SHA-256 manifests,
   source repository, original actor, bundle length and all false
   migration/write authority flags.
4. Releases the transaction **before** repeating the live source/approval
   preflight and full F-035v2 historical file rehash; requires identical
   complete stage/ref list and immutable bundle receipt.
5. Returns a `GitIncrementalLaterCheckpointMirrorAdmissionCandidate`
   sealed by a new domain-separated SHA-256. The candidate binds the
   exact later source snapshot, workpack, target observation, physical
   mirror UUID, F-097b full-source proof, F-035v2 checksum, source-ref set,
   review timestamp and all false authority flags.

Because the F-091/F-097/F-031 history readers each use their own source
transactions, **the entire sequence is not an atomic lease claim**. The
locked SQL segment only fences the exact immutable workpack/bundle row
comparison. The before/after current-epoch readers limit stale acceptance
but the result can become stale immediately after returning. Future F-061
acquisition **must** revalidate all live proofs inside its own short,
source-first DB transaction before creating a new generation.

## Security boundary

`DurableLeasePersisted=false`, `F061LeaseGeneration=0`,
`CanMaterialize=false`, `CanPush=false`, `CanRetry=false`,
`CanDispatch=false` and `MigrationComplete=false` are enforced
cryptographically. **A matching candidate hash cannot grant a capability**.
It is not a trusted lease token, worker ownership, physical import
approval, an immutable `Verified` row, F-095 ancestry or proof of GitHub
migration. The local-only importer in [#116](https://github.com/KeyserDSoze/GitHubMigrator/issues/116)
remains separately gated; no endpoint connects these two features.

Real PostgreSQL17 two-epoch SHA-1/SHA-256 integration and default-OFF
JWT-host tests verify this scoped source/bundle rereview; unit tests
verify deterministic sealing and rejection of changed scope, SHA and
lease/write flags. The tests do not exercise a final F-061 claim because
**that writer does not yet exist**.

## Still missing for #115

- Durable F-061v2 generation/actor/worker token and DB-time expiry/heartbeat
  under the source-first lock, with immutable scope and replay fences.
- Source-locked post-import live proof revalidation plus exact Git closure,
  atomically saved `Verified` evidence/event only while the same worker's
  lease remains valid.
- Crash recovery and quarantine, repeated/restarted and three-way
  PostgreSQL concurrency tests, and a separate full green CI.

Until completed, keep #117 (if CI/tests remain incomplete), #115,
#100, #96 and #92 open. No destination GitHub push, dispatch, retry
or migration success may be inferred from this step.


## #118: additive PostgreSQL F-061v2 lease and audit *schema* (no writer)

`GitIncrementalLaterCheckpointMirrorLeaseSchema` installs independent
`git_later_checkpoint_mirror_leases` and
`git_later_checkpoint_mirror_lease_events` tables after the F-035v2
bundle evidence/lease schemas. These are deliberately **not** the
legacy F-029/F-061 tables. The new workpack FK references exactly the
sealed F-035v2 evidence's workpack, while other FKs bind plan,
preparation, F-091 checkpoint/snapshot, source connection and reviewed
checkpoint F-031 observation. The row stores the exact stage SHA-256,
F-097b source proof and ref-set digests, F-038v2/F-035v2 proof and
binary digest, target receipt, physical mirror UUID, worker, actor,
lease token, generation, bounded lease timestamps and local proof
fields.

Database checks require homogeneous source-bound SHA-256 digests,
positive generation, nonzero IDs, constrained
`Held/RecoveryRequired/Verified` states, bounded physical proof counts
and false materialization/push/retry/dispatch/completion flags.
The scope is immutable even across lease transitions. A terminal row
cannot be silently reclaimed; event rows reject UPDATE and DELETE.
The schema does **not** claim that a lease is durably acquired: no
claim/renew/complete/recover writer is registered, and an internal
SQL row inserted by integration tests is not a runtime admission.

Tests exercise the exact F-035v2 FK (not legacy F-061), PostgreSQL
checks and append-only triggers, and a manually inserted **test-only**
lease bound to a real two-epoch F-091/F-097b/F-035v2 SHA-1/SHA-256
scenario. The production writer must still acquire its own
source-first transaction, re-read the latest full source and approval
evidence, use database clock/generation fencing and never run Git
while holding SQL locks. Immutable `Verified` commits and physical
quarantine remain **not implemented**. Keep #118/#115/#100 open until
those requirements and full CI are demonstrated.


## #118: opt-in F-061v2 **claim / renew** coordinator

`PostgresGitIncrementalLaterCheckpointMirrorLeaseStore` is now
registered as an *internal* service with a separately default-OFF
`GitIncrementalLaterCheckpointMirrorLease:Enabled` gate. It does not
expose an HTTP route, start a worker, invoke local Git or send to GitHub.

A new `ClaimAsync`:

- Obtains the non-authorizing latest #117 review with actual F-035v2
  binary rehash, then opens a **source-first / plan-second PostgreSQL**
  advisory transaction (matching F-035v2).
- While locked, independently rechecks that the latest physical source
  snapshot and F-091 checkpoint, F-097b receipt, still-approved
  checkpoint F-031, human review and recent destination validation,
  immutable F-038v2/F-035v2 and its already `Recorded` lease all
  belong to the exact immutable source / workpack / target scope.
- Uses `clock_timestamp()` from the **PostgreSQL server** rather than
  worker clock or caller-supplied timestamps. It inserts at most one
  random opaque lease token, generation 1, one worker ID and a bounded
  deadline (30 seconds–3 minutes) and an immutable `Acquired` event
  in the same transaction. Competing writers serialize on source lock;
  a second worker is refused.
- If the old generation is already past its deadline, transitions to
  `RecoveryRequired` plus one append-only `Quarantined` event. It
  never silently reclaims, deletes or trusts a possibly orphaned
  UUID.git; a separate reviewed recovery flow is still necessary.

`RenewAsync` independently repeats the full live admission and
source-first locked latest-source/approval checks. It requires the
**same** mirror/workpack/snapshot, worker ID, generation, token, SHA
and previous expiry, using an atomic SQL compare-and-swap. The token
and generation are unchanged. Individual renewals are capped at 3
minutes, the total lifetime at 2 hours from first acquisition, and
every renewal appends a separate immutable `Renewed` event. A stale
worker cannot renew a newer deadline or another worker's lease.

The integration suite exercises real two-epoch SHA-1/SHA-256 source
proofs and bundles, multiple contention attempts through independent
DbContexts, repeated renewals, wrong-worker and old-deadline refusal,
tamper/append-only triggers and an expired worker's quarantine. The
runtime lease and its records are **coordination only**:
`CanMaterialize=false`, `CanPush=false`, `CanRetry=false`,
`CanDispatch=false`, `MigrationComplete=false`. The coordinator is
not connected to the #116 physical importer yet; it does **not** create
a durable `Verified` result or authorize F-095. F-061v2
Verified/complete, explicit recovery of quarantined orphans, lease
heartbeat wrapper during import, crash/restart validation and F-095v3
physical ancestry remain #115/#100/#96.

Do not close #118 before the real test suite and CI are green 4/4, and
do not claim production readiness from a passing lease test.


## #119: physically re-audited, source-locked F-061v2 Verified journal

New `GitIncrementalLaterCheckpointMirrorVerifiedReceipt` is a
versioned, domain-separated SHA-256 physical source proof binding the
exact later F-091/F-097b mirror, F-038v2 workpack/F-035v2 binary,
full ref/OID SHA, physical object closure SHA/count, worker, token,
generation and verification/recording timestamps. Its
`DurableEvidencePersisted=true` differs deliberately from the
non-authorizing F-060v2 and #117 offline witnesses. All
`CanMaterialize/CanPush/CanRetry/CanDispatch/MigrationComplete`
fields remain **false**, including in the SQL row and its constraints.

`PostgresGitIncrementalLaterCheckpointMirrorVerifiedJournal` is an
**independently default-OFF, internal only** writer:
`GitIncrementalLaterCheckpointMirrorVerified:Enabled=false`.
Its trusted physical reader also defaults OFF and receives only
deployment-configured `GitSourceBundles:BundleRoot` and
`GitIncrementalLaterCheckpointMirrorVerified:MirrorRoot`, never
client-provided Git paths. It does not auto-import or register a worker.

`CompleteAsync` reads current sealed F-091/F-097b/F-031/F-038v2/F-035v2
stage and actual UUID.bundle first, independently checks the worker's
fenced `Held` lease, and invokes the trusted local Git auditor
**outside PostgreSQL transactions**. The auditor rehashes the bundle
before and after double local bare-Git fsck/full object closure and
exact branch/tag ref/OID comparison. A caller cannot pass a
self-signed digest or fabricated offline receipt to complete the lease.

The journal rereads current approval and file provenance after fsck,
then enters a brief **source-first -> plan-second** SQL advisory
transaction. Under lock it independently checks newest source snapshot,
checkpoint, full F-097b proof, reviewer, F-031 validation TTL,
F-038v2/F-035v2 and finished F-035v2 lease, and requires the precise
worker, generation, token and **latest deadline** before recording.
It rehashes the actual UUID.bundle while locked (no Git subprocess)
and requires the physical fsck timestamp to follow lease acquisition
and be no older than two minutes according to the database clock.
The SQL receipt INSERT, `Held -> Verified` CAS, and unique immutable
`Verified` event commit atomically in one transaction.

The new `git_later_checkpoint_mirror_verified_evidence` table has
an immutable BEFORE INSERT/UPDATE/DELETE trigger that additionally
requires every scoped digest/ID/worker/token/generation to match the
live **Held** F-061v2 row and the physical proof to have occurred
after lease acquisition and before its deadline. Non-authorizing
flags and SHA/graph count limits are enforced by PostgreSQL. It is
isolated from historical baseline F-061 and F-029.

A real local failure in the physical fsck/bundle audit triggers
`QuarantineAsync`: one worker-generation-fenced, source-locked
`RecoveryRequired` transition plus one append-only
`Quarantined` event; no partial `Verified` proof or automatic
orphan adoption/overwrite. Replay of a terminal Verified or
quarantined lease is refused.

The original F-060v2 local importer **remains separately gated**
and is not yet called by any F-061 worker/heartbeat coordinator.
Tests invoke it explicitly in sandbox-only SHA-1/SHA-256 scenarios
to exercise the new Verified writer. An explicit heartbeat across
physical import, operator-reviewed quarantine recovery, and F-095v3
physically bound ancestry are separate remaining work under #115
and #96. No remote GitHub destination operation was added.


## #120: explicitly invoked local import and lease heartbeat worker

`GitIncrementalLaterCheckpointMirrorImportWorker` composes the
source-first PostgreSQL lease (#118), the hardened local-only F-060v2
bundle importer (#116), and the physically re-audited immutable
F-061v2 `Verified` journal (#119). Its registration is separate from
the legacy F-029 source mirror and is **OFF by default**:
`GitIncrementalLaterCheckpointMirrorImportWorker:Enabled=false`.
Actual local import requires a SECOND explicit gate,
`GitIncrementalLaterCheckpointMirrorImportWorker:LocalImportEnabled=false`,
in addition to all the earlier F-091/F-097b/F-031/F-035v2,
admission, lease, Verified and trusted physical auditor switches.

No HTTP route or hosted background service invokes the worker. An
internal caller must supply the exact server-known checkpoint,
workpack, source repository, immutable F-031 receipt digest, original
F-035v2 actor and worker ID. No path, Git ref/OID, remote host or
bundle contents may be provided by the caller.

After the live source-locked `ClaimAsync`, the coordinator starts a
single periodic renewal loop in **new DI/DbContext scopes** (never on
the importer thread's context), then independently reconstructs the
source-only stage, retrieves the original real UUID.bundle F-035v2
provenance and invokes the hardened materializer. The heartbeat uses
the worker's exact generation, opaque token, previous expiry and
PostgreSQL clock, up to 3 minutes per lease and 2 hours overall. Its
interval is configuration-bounded to below one-third the lease
duration. On a lost renewal, the same linked cancellation token
interrupts local filesystem/Git work; no `Verified` transition is
permitted.

On successful physical import, the worker stops and **joins** its
heartbeat before obtaining one final source-locked renewal of the
last persisted handle. The separate Verified journal then rechecks
current live source and reviewed target, actual F-035v2 bytes and
the already promoted UUID.git with two full Git fsck/ref/OID/closure
passes outside SQL locks. The final F-061v2 `Verified` row + event
is committed only while the same generation/token/worker/expiry
remains held. If any part fails, a short source-first, exact-owner
quarantine attempt marks `RecoveryRequired`; the mirror and bundle
are **never** deleted, adopted or overwritten.

These guarantees are fail-closed: slow final physical verification
may exhaust the last 3-minute lease, in which case the journal must
refuse to write `Verified` and require quarantine/recovery. A later
optimization can add a DB-clock heartbeat within the final re-audit
phase with a distinct freeze handshake, but must not permit a race
between heartbeat and final CAS.

Real PostgreSQL17 two-epoch SHA-1/SHA-256 integration tests invoke
a delayed **real** local importer, asserting multiple immutable
`Renewed` audit events followed by exactly one `Verified` event,
no `Quarantined` event, exact physical refs/object proof, and no
legacy F-061 or provider write. Unit/API tests keep the worker OFF
by default. The import worker remains unrelated to F-095v3 source
ancestry authority and GitHub destination migration.

**Open safety work:** explicit operator-reviewed recovery of
`RecoveryRequired` orphans; additional cancellation/DB-failure/
deadline tests; cold-process restarts; load/performance bounds on
repeated F-035v2 SHA rehash; complete CI. Nothing in this worker
enables GitHub pushes, dispatch, retries or migration completion.
