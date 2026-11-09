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
