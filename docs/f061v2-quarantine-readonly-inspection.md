# F-061v2 quarantined local mirror inspection (issue #121)

Status: **internal read-only diagnostic implemented, pending full CI**.
The broader [#121](https://github.com/KeyserDSoze/GitHubMigrator/issues/121)
still owns explicit reviewer authorization, securely segregating orphans
and generation-fenced re-admission. Work happens directly on `main`.

## Why this must be separate

The source-only [#120](https://github.com/KeyserDSoze/GitHubMigrator/issues/120)
worker now acquires an F-061v2 PostgreSQL lease, renews it during Git
import, and invokes the [#119](https://github.com/KeyserDSoze/GitHubMigrator/issues/119)
real physical `Verified` writer only while the same worker token and
deadline remain valid. If an import fails, loses its heartbeat or a
second replica revokes the held generation, it becomes terminal
`RecoveryRequired`. The mirror may be **missing, corrupted, or
physically intact**, depending on where the worker crashed. None of
those conditions permits automatically reclaiming the original
`UUID.git` or creating a new generation.

## Independently OFF-by-default read

`PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector` is
registered only as an internal scoped service, with a separately
disabled setting:

```text
GitIncrementalLaterCheckpointMirrorRecoveryInspection:Enabled=false
```

The existing trusted offline Git auditor has its own independent
gate and server-owned `BundleRoot` / `MirrorRoot`. This inspector
adds **no HTTP endpoint, worker, GitHub provider request, SQL write or
local Git mutation**. It takes only the scoped preparation, plan,
checkpoint, target observation, workpack, source repository,
previous F-031 immutable receipt hash and historical F-035v2 actor.
It never accepts caller-selected Git refspecs, object IDs, paths or
`UUID.git` root.

The inspector:

1. Revalidates the real latest source F-091/F-097b full Git ref/OID
   graph, original stale F-029 mapping, independently human-reviewed
   target F-031, sealed blocked F-038v2 and immutable F-035v2 record
   and current physical UUID.bundle SHA/bytes.
2. Under a **short source-first / plan-second PostgreSQL advisory
   transaction**, rereads current source/reviewer/destination freshness,
   and requires the *same* quarantined F-061v2 mirror, original bundle
   actor, generation, worker, token and exact immutable SHA-256 scope.
   `Held` or `Verified` rows, a non-null active deadline, any
   destination/migration authority, or an existing Verified receipt
   are refused.
3. Loads at most 1,001 ordered append-only lease events. The bounded
   chain must be `Acquired`, followed by zero or more `Renewed`,
   ending in **exactly one** `Quarantined`. All events must match
   the original worker, generation, stage SHA and monotonically
   ordered PostgreSQL timestamps. An excess or inconsistent chain is
   rejected, not silently truncated.
4. Releases the transaction **before** performing a full trusted
   double-Git fsck/object-closure/ref-OID read of an existing
   `UUID.git` and rechecking the immutable UUID.bundle bytes.
   A missing/invalid/corrupt mirror is marked `Uninspectable`, never
   `Verified`. Symlink or unsafe-root rejection from the physical
   auditor never produces an intact proof.
5. Rereads current F-091/F-097b/F-031/F-038v2/F-035v2 after the
   filesystem work and performs a second short locked PostgreSQL
   review. Any changed source snapshot, bundle file, lease,
   quarantine state or event chain causes refusal.

A SHA-256 versioned, domain-separated
`GitIncrementalLaterCheckpointMirrorRecoveryInspection` binds the
source snapshot, mirror/workpack/checkpoint/target, worker/token/
generation, original failure code, event counts/quarantine timestamp,
the actual physical graph SHA/count if independently inspectable,
and the DB-time review timestamp.

`DurableRecoveryApprovalPersisted=false`,
`CanReAdmit=false`, `CanMaterialize=false`,
`CanPush=false`, `CanRetry=false`, `CanDispatch=false`
and `MigrationComplete=false` are verified against the receipt
digest. An **intact** Git mirror after a failed final journal is
**not** an F-061 `Verified` result and can never be adopted based on
this inspection alone. This read-only result is not persisted as an
approval event or accepted by the importer/lease writer.

## Real integration cases

The independent PostgreSQL17 two-epoch SHA1/SHA256 scenarios create
a real source checkpoint and F-035v2 bundle. Cases include:

- import aborted before physical promotion: `RecoveryRequired`,
  precisely one `Quarantined` event, no Verified and an
  `Uninspectable` diagnostic;
- import fully materialized and validated as a real Git bare mirror,
  then abort before Verified: the same terminal quarantine plus an
  **Intact** object-set digest/count, with **no** re-admission right;
- a second PostgreSQL replica revokes the worker generation during a
  blocked Git operation: linked cancellation and read-only inspection
  of the original actor and event history;
- terminal `Verified` refusal, foreign repo/actor refusal,
  default-OFF and no provider/HTTP calls; domain tampering with
  event counts, tokens, source snapshots and all permission flags.

All CI job results must be checked before treating these cases as
verified.

## Not yet implemented

The **next** #121 steps need a separate, authenticated, role-checked
reviewer decision with an immutable audit record and a new
schema/lease-generation model. The current F-061v2 trigger deliberately
forbids rewriting the token/worker/generation of a terminal lease.

A future recovery writer must first prove source currentness and
checkpoint F-031 review *again under the source lock*, then isolate
an orphan in a distinct no-overwrite physical path under POSIX file
lock, and obtain independently reviewed new-generation authority.
It must not mutate/erase the existing Verified evidence or automatically
retry from a matching self-sealed SHA. No F-095v3 ancestry, destination
push, dispatch, retry, remote Git or migration completion is granted
by this inspector. Keep #121/#115/#100/#96/#92 open.
