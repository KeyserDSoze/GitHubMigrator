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
