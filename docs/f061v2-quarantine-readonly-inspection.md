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
the **full ordered immutable lease-event-chain SHA-256** (not merely
the event counts) and the DB-time review timestamp.

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


## #122: immutable independent superadmin review (no recovery action)

`PostgresGitIncrementalLaterCheckpointMirrorRecoveryReviewJournal`
is a **separate, OFF-by-default** component controlled by
`GitIncrementalLaterCheckpointMirrorRecoveryReview:Enabled=false`.
A separately authenticated GET candidate and POST decision route are
available to **superadmin** JWT holders who have already changed
their password, under
`/api/incremental-preparations/{id}/operation-plans/{planId}/source-sync-checkpoints/{checkpointId}/mirror-recovery`.
Both routes are rate-limited. The reviewer UUID is obtained from
the verified JWT `NameIdentifier` claim, never the JSON payload.

A decision requires a fresh, independently rebuilt #121 diagnostic
with current F-091/F-097b/F-031/F-038v2/F-035v2, the real SHA-256
UUID.bundle and physical UUID.git double-fsck, the **same** original
F-061v2 `RecoveryRequired` lease and entire acquired/renewed/
quarantined audit chain. The client supplies no Git paths, refs,
object IDs or lease token. The API GET omits the opaque lease token.

GET returns both the time-stamped inspection evidence hash and
`reviewCandidateSha256`, a separate SHA over the *immutable*
source/worker/lease/event-chain/physical closure portion of the
diagnostic. This distinction is deliberate: the two real Git/SQL
inspections occur at different database-clock instants, so using the
inspection timestamp's hash as optimistic concurrency would make
every legitimate POST stale. POST verifies the **stable** candidate
SHA against a newly rerun trusted audit, then acquires short
SOURCE-first and PLAN-second PostgreSQL locks and rechecks source,
independent F-031 human reviewer and F-061 terminal lease scope.

Only the previously approved checkpoint F-031 reviewer may sign
the quarantine decision, and this identity must differ from the
original bundle actor and failed worker. Valid values are
`ApprovedForIsolationReview` (requires independently **Intact**
UUID.git) or `Rejected` (may also describe an uninspectable
orphan). `ApprovedForIsolationReview` is an approval for **later
human inspection/isolation planning**, not authority to rename,
adopt, delete, import, reacquire or push a mirror.

One immutable `git_later_checkpoint_mirror_recovery_decisions` row
and one append-only decision event are written in a single source-
locked transaction. PostgreSQL binds the decision to the *exact*
RecoveryRequired mirror/workpack/checkpoint/source-snapshot,
worker/lease-token/generation, original bundle actor, F-031
reviewer, frozen stage/evidence/ordered-event-history digests and
object-set digest. The SQL triggers refuse UPDATE/DELETE and
accept no already Verified mirror. All capability fields are
persistently false: `can_readmit`, `can_materialize`, `can_push`,
`can_retry`, `can_dispatch`, `migration_complete`.
Even a successfully recorded decision leaves the terminal lease
`RecoveryRequired` and UUID.git unchanged.

The integration suite covers real SHA1/SHA256 two-epoch source Git,
both already-promoted **Intact** and missing **Uninspectable**
orphans, stale optimistic SHA, unauthorized self-review, JWT role
gating, durable PostgreSQL immutability, replay and zero remote
provider I/O.

**Still missing for #121:** reviewed safe POSIX-locked physical
orphan segregation and new-generation history/re-admission. This
separate step must explicitly recheck both the decision and the
current source under SQL source lock and must not weaken the current
terminal lease/Verified triggers. No F-095v3 ancestry, GitHub
push/retry/dispatch or migration-complete authority is added here.


## #123: local physical no-overwrite orphan isolation primitive (NOT wired)

A separate component,
`LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator`,
now implements only the **offline filesystem operation**, independently
disabled by default, and deliberately **not registered in DI, hosted
workers or HTTP routes**. It is NOT yet a production recovery
coordinator. No input path/refspec/OID is accepted, only the
independently sealed later F-091/F-097b staging, immutable F-035v2
bundle, current quarantine inspection and exact #122 reviewed
decision `ApprovedForIsolationReview`.

The local primitive reuses **the same** process gate and POSIX
`.f060-source-materialization.lock` as the F-060 importer, and
revalidates the trusted absolute mirror root and all symlinks. While
holding that lock it reruns the server-owned bundle SHA/length audit
and two full local Git SHA1/SHA256 fsck, branch/tag ref/OID and reachable
object-set checks against the exact original F-097b refs. It refuses
a changed closure SHA/count, foreign scope, rejected decision,
symlinked root, missing/corrupt UUID.git or a pre-existing quarantine
destination.

Only after these checks may it perform a **same-directory atomic
no-overwrite rename**:
`UUID.git` -> `UUID.DECISION_SHA256.quarantine.git`.
This deterministic name allows later crash diagnosis; a replay with
the same approved decision refuses rather than overwriting the old
quarantine. The original `UUID.bundle` is never moved or deleted,
and neither directory is re-imported or adopted by this component.

The returned
`GitIncrementalLaterCheckpointMirrorOrphanIsolationReceipt`
SHA-256-seals the exact approved decision, source/workpack/checkpoint,
original worker/generation/token, ordered SQL event-history SHA and
physically measured object-set/count, plus the bounded deterministic
quarantine name. Crucially,
`DurableIsolationRecorded=false`, `CanReAdmit=false`,
`CanMaterialize=false`, `CanPush=false`, `CanRetry=false`,
`CanDispatch=false` and `MigrationComplete=false`. It is a
**non-authorizing local witness, not a SQL recovery receipt**.

The next #123 step still requires an independently trusted SQL
coordinator: live **source-first** lock, authenticated reread of the
immutable #122 human reviewer and latest F-091/F-097b/F-031/F-035v2,
pre-registered crash-safe physical isolation intent, actual POSIX
operation, post-move recovery reconciliation and immutable SQL
isolation evidence. This must prevent the filesystem rename from
being interpreted as an F-061 new-generation license even if
PostgreSQL fails between the move and final audit commit.

A separate read-only `AuditIsolatedAsync` now supports
diagnosing a crash **after** the atomic directory move but **before**
PostgreSQL can record the operation. It derives the exact quarantine
path from the reviewed decision SHA, checks that no original UUID.git
has been recreated, locks with F-060, rehashes the original UUID.bundle
before and after, and applies two strict bare-Git fsck/ref/OID/
object-closure passes to the isolated directory. It refuses missing,
symlinked, corrupted or changed Git objects; it never renames back,
imports or issues a durable recovery receipt. The implementation
reuses the same bounded F-056 object graph verifier, rather than
trusting the filename or an earlier signed checksum.

A still later step under #121 must design append-only generation
history and **separate reviewer-approved** re-admission, without
editing or reopening historical terminal F-061v2 lease rows or
weakening any existing Verified triggers. No destination GitHub
operation or F-095v3 ancestry is enabled by #123. Keep both #123
and parent #121 OPEN until all required 4/4 CI and SQL crash/race
tests pass.
