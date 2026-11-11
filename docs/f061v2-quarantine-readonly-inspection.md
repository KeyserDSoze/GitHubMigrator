# F-061v2 quarantined local mirror inspection (issue #121)

Status: **internal read-only diagnostics, immutable human review and
PREPARED journal, plus a separately gated physical-to-SQL terminal
quarantine coordinator are implemented on main; latest full CI is still
pending**. This does NOT implement F-061 generation re-admission.
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

## What is still not implemented

Issue #122 already provides independently authenticated, immutable
reviewer decisions; #123/#124 now provide a separately gated
no-overwrite physical quarantine and a source-locked, immutable
terminal SQL isolation receipt/event. These operations do **not**
release the original F-061v2 `RecoveryRequired` lease.

The remaining #121 scope is a **separate human-approved re-admission
with append-only new generation/worker/token history**. The existing
F-061v2 trigger deliberately forbids rewriting a terminal lease.
No generation-fenced re-admission or F-095v3 ancestry authority is
implemented. Do not restore `UUID.git`, auto-retry, push, dispatch,
send remote Git or set migration completion based on a quarantined
directory or the terminal isolation receipt. Keep #121/#115/#100/
#96/#92 open until their separate acceptance and full CI verification.


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


## #123: legacy offline isolation primitive (separate from trusted coordinator)

A separate component,
`LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator`,
provides an independently disabled-by-default **offline filesystem
operation**. That older local-only `IsolateAsync` primitive is not
itself an SQL writer and does not authorize recovery. A **separate,
default-OFF DI-registered trusted coordinator and physical session**
now exist, described in the #124 section below. Neither is exposed
by an HTTP route or hosted/background worker. No input path/refspec/
OID is accepted, only the
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
On Linux the filesystem move uses `renameat2(RENAME_NOREPLACE)`,
which atomically refuses even a competing process creating the
destination **after** the application's existence check. An ordinary
`Directory.Move` or an unchecked POSIX `rename` would not protect this
race (an empty target directory can be replaced). Unsupported OS/kernel
or filesystem behavior fails closed, without falling back to an unsafe
rename. The shared POSIX lock still protects against cooperating F-060
import workers, but it is not the sole no-overwrite guarantee.
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

The separate #124 trusted SQL coordinator now does the live
**source-first** reread of #122 reviewer and latest F-091/F-097b/
F-031/F-035v2, crash-safe pre-move isolation intent, POSIX
no-overwrite operation, dual Git closure check, post-move PostgreSQL
isolation receipt/event and crash reconciliation. **This does not
give the older offline `IsolateAsync` return value SQL authority.**
All of these operations still prohibit an F-061 new generation,
including when PostgreSQL fails after the kernel move.

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


## #123: durable PRE-MOVE SQL isolation intent (separate from actual isolation)

The additive source-only
`GitIncrementalLaterCheckpointMirrorIsolationSchema` now creates
**four isolated, append-only PostgreSQL tables**: isolation intents and
their immutable `Prepared` event, plus separate future physical isolation
receipts and their immutable `Isolated` event. Both pairs are FK-bound
to the *exact* F-061v2 mirror/workpack and the immutable #122 reviewed
decision, not to a baseline F-061, destination operation or any client path.
The SQL guards refuse a decision other than
`ApprovedForIsolationReview`, wrong original worker/token/generation,
stale/non-quarantined leases, a previous F-061v2 `Verified` record,
unmatched SHA-256 evidence, invalid quarantine filenames and mutations of
historical journal rows. SQL CHECK constraints persistently forbid
re-admission, materialization, push, retry, dispatch and completion.

`PostgresGitIncrementalLaterCheckpointMirrorIsolationIntentJournal` is an
independent **default-OFF** scoped service, controlled by
`GitIncrementalLaterCheckpointMirrorIsolationIntent:Enabled=false`.
It is not callable via HTTP or an unattended/background worker.
`PrepareAsync` takes only source/plan/checkpoint/target/workpack identities
and the original bundle actor; it takes **no filesystem path, Git ref/OID,
reviewer override, signed intent object or lease token**. It reruns the real
#121 double-Git SHA1/SHA256 physical inspector and sealed bundle reads
outside PostgreSQL, then reads the latest F-091/F-097b/F-031/F-038v2/
F-035v2 candidate. Under a short source-first, plan-second PostgreSQL
transaction, it rereads current source and the exact terminal F-061v2
lease, recomputes the **entire ordered worker event chain**, verifies the
immutable original #122 human-review receipt SHA against its stable
freshly re-audited physical candidate and checks the decision audit event.
Only then does it atomically write a versioned domain-separated SHA-256
`Prepared` intent and its single event. Replays fail closed.

**Very important:** an existing `Prepared` row with no matching
`Isolated` receipt is unresolved, regardless of whether `UUID.git`
still exists or a `UUID.DECISION_SHA.quarantine.git` directory has
appeared. It never means the filesystem was moved, and it cannot grant
a new F-061 generation or authorize any GitHub destination operation.
The separate #124 default-OFF internal SQL writer is now DI-registered
along with its independently gated physical session. It requires
actual on-disk Git and F-035 bundle proof under the same F-060 POSIX
root lock before it records the single immutable `Isolated` receipt
and audit event. A crash between the kernel rename and final SQL commit
can be reconciled only by re-reading the same source-locked `Prepared`
and reviewer history and freshly auditing the exact quarantined Git
graph and durable intent marker. Its receipt is evidence of physical
segregation **only**, not of `Verified`, re-admission or a new
generation. The final 4/4 PostgreSQL CI and additional adversarial
checks remain acceptance gates; no remote GitHub writes.


## #124: PREPARED → filesystem crash-state inspection (read-only phase)

`PostgresGitIncrementalLaterCheckpointMirrorPreparedReconciliationReader`
is an independently **OFF-by-default** source-first PostgreSQL
reader. It does not accept caller-supplied SQL receipts, Git ref/OIDs,
worker token, local root or quarantine directory. It reads immutable
`Prepared` intent+event and original #122 human-reviewed
`ApprovedForIsolationReview` decision+event, checks current
F-091/F-097b/F-031/F-038v2/F-035v2 latest checkpoint and real
`UUID.bundle` bytes, and locks source→plan in short SQL transactions
to check terminal original F-061v2 `RecoveryRequired` owner/token/
generation, **all** ordered worker events and their SHA-256 digest,
no F-061v2 `Verified` and no existing `Isolated` receipt.
It rechecks current source after releasing the transaction.

Its `ExpectedInspection` field is deliberately reconstructed from
persisted, domain-separated SQL evidence: it is **not a fresh physical
Git observation**. The reader does not classify absence of `UUID.git`
as a successful quarantine, and it cannot create a lease or record an
`Isolated` SQL receipt. It provides expected immutable facts only for
the independently re-audited on-disk phase.

The new `LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator.
InspectPreparedPathsAsync` is a separate disabled-by-default
**read-only**, POSIX root-locked crash diagnostic. Inputs are bounded
staged F-035v2 bundle/source evidence, the historical reviewed candidate,
and an immutable intent whose deterministic quarantine pathname is
fully server-derived. It verifies the sealed expected root, UUID.bundle
SHA+bytes and actual Git SHA1/SHA256 ref/OID closure **twice** while
the same process/POSIX root lock used by the F-060 importer is held.
It distinguishes:
- `PreparedOriginalIntact`: original `UUID.git` alone exists,
  matches the exact reviewed graph and has either no pre-rename
  marker or the exact durable reviewed intent marker.
- `PreparedOriginalUntrusted`: original `UUID.git` still exists
  but its pre-rename marker is partial, forged, dangling or otherwise
  mismatches immutable `Prepared` + #122 reviewer history.
- `PreparedIsolatedIntact`: deterministic quarantine directory
  alone exists, its graph matches **and** its fsynced causality
  marker agrees with the reviewed intent (possible crash after
  rename, still **not** a durable SQL receipt).
- `PreparedIsolatedUntrusted`: original `UUID.git` is absent but
  a byte-identical foreign/legacy quarantine has no valid causality
  marker. Matching Git graph SHA alone is insufficient.
- `PreparedBothPresent`, `PreparedBothMissing`: unresolved collisions/
  disappearances. No unlink, overwrite or automatic reimport.
- `PreparedOriginalCorrupt`, `PreparedIsolatedCorrupt`: source or
  quarantined graph/bundle mismatch or fsck failure.
- `PreparedUnsafePath`: foreign file/symlink/path changed; fail closed.

Real local Git tests exercise SHA1 and SHA256 original, isolated,
both-present, corrupt and missing crash conditions plus re-sealed
fresh physical inspection of a historical reviewer decision. PostgreSQL
integration checks re-reading the exact immutable `Prepared` row
after simulated restart and rejection of SQL mutations.

**Implemented but not yet accepted (#124):** the independent
source-first OS/SQL coordinator holds the F-060 process/POSIX
no-overwrite quarantine fence across the post-rename double-fsck,
source rechecks and short immutable `Isolated` receipt+event SQL
transaction. PostgreSQL17 integration tests now cover real SHA-1
crash/restart and SHA-256 independent three-replica contention.
They require a completed **4/4 CI on the final main SHA**; canceled
intermediate workflows are not passes. There is no public API or
hosted worker for this coordinator. The source `RecoveryRequired`
lease remains terminal; separately reviewed generation-history
re-admission still belongs to #121.


## #124: optional trusted physical-to-SQL isolation coordinator

The independently default-OFF internal
`PostgresGitIncrementalLaterCheckpointMirrorIsolationCoordinator`
(`GitIncrementalLaterCheckpointMirrorIsolationCoordinator:Enabled=false`,
`GitIncrementalLaterCheckpointMirrorIsolationCoordinator:PhysicalEnabled=false`)
now joins the **source-locked** SQL `Prepared` history reader to the
server-owned `LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator`
physical session. **No HTTP route, hosted scheduler, remote GitHub
sender, F-061 new generation or automatic re-admission is registered.**

The coordinator accepts only source/plan/checkpoint/workpack identifiers,
repository identity and original bundle actor. The SQL reader checks the
actual immutable F-031 approved human reviewer, #122 signed
`ApprovedForIsolationReview` and its event, #123 immutable
`Prepared` intent+event and the *original terminal* F-061v2
`RecoveryRequired` worker/token/generation plus full
`Acquired/Renewed*/Quarantined` ordered audit SHA. It independently
rechecks latest F-091/F-097b, F-038v2 and physical F-035v2 bundle bytes.

**Filesystem phase**: the typed physical session holds the same
process/POSIX volume lock as the F-060 importer. With an intact original
`UUID.git`, it freshly audits real SHA1/SHA256 refs/OIDs, then calls
Linux `renameat2(RENAME_NOREPLACE)` on strictly server-derived paths.
When the same prepared intent is found with only the deterministic
quarantine directory after a process crash, it may instead continue
as a *reconciliation*, never re-create `UUID.git`. Both paths,
neither path, symlinks, outsider files and graph/bundle mismatch
fail closed. After the move (or discovered quarantine), the physical
session checks the entire quarantined Git graph **twice**, checks
`UUID.bundle` SHA and length before/after and verifies that the
original path remains absent.

**Database phase**: with that same physical volume lock still held,
the session passes a typed, independently measured receipt to a short
source-first, plan-second SQL transaction. The writer repeats the
actual current source/lease/decision/prepared/event proof, compares
the *complete* ordered worker-event SHA and verifies there is neither
an F-061v2 `Verified` record nor a completed isolation receipt. It
inserts a versioned, domain-separated SHA-256
`git_later_checkpoint_mirror_isolation_receipts` record and exactly
one immutable `Isolated` event in **one SQL transaction**.
SQL CHECK/FK/triggers bind original worker/token/generation, immutable
bundle SHA/bytes, decision/intent hashes, physically measured object
set/count, and `reconciled_existing_quarantine` flag. The
`isolated_at_utc` field represents **database-time confirmation of
the physical audit**, not a fictional original kernel rename timestamp
after a crash. Every write or migration authority flag remains FALSE.
The original `RecoveryRequired` lease is **never reopened**.

A process failure **after** the no-replace rename but **before** SQL
commit leaves a prepared row with no isolated receipt. A subsequent
explicit invocation can re-audit the existing isolated Git and persist
the terminal SQL proof; it never treats a missing original directory,
a caller-supplied receipt, a guessed directory name or a matching SHA
alone as authority. Repeat calls after the final immutable receipt
refuse rather than duplicate audit events. Real PostgreSQL17 two-epoch
test cases exercise normal SHA256 rename and SHA1 crash-after-rename
reconciliation; SQL UPDATE/DELETE of terminal receipt/events must fail.

This is a safely gated physical isolation **only**. It is not a new
F-061 generation, a new lease, an F-095v3 ancestry proof, a remote GitHub
push, retry/dispatch or migration completion. A *separately approved*
generation-fenced re-admission remains #121. Validating the latest
complete 4/4 CI and adversarial concurrent-replica coverage remains
essential before #123/#124 are considered accepted.


### Crash-safe physical causality and directory durability

The final source-only coordinator now creates an exclusive
`agic-f061v2-reviewed-isolation-intent` marker **inside the original
bare Git directory only after the actual source-locked SQL
`Prepared` event has been verified and the original Git closure
has passed real checks**. The small marker binds the exact immutable
`Prepared` SHA-256 and #122 independently reviewed decision SHA-256;
it is not a credential or migration authority. Creation is
`FileMode.CreateNew` with write-through/file flush and an fsync of
the original directory to persist the marker's directory entry.
After Linux `renameat2(RENAME_NOREPLACE)`, the server fsyncs the
shared parent mirror directory **before** claiming a terminal SQL
`Isolated` receipt. Unsupported OS/fsync operations fail closed.

An interrupted operation with the original directory still present
and the exact marker may repeat the *source-locked*, freshly audited
rename. If the isolated directory alone exists on restart,
`WithVerifiedIsolationAsync` **requires the matching persisted marker**
in addition to real double-fsck Git closure, exact bundle SHA/length,
the immutable SQL `Prepared` and signed #122 review event and the
unchanged terminal `RecoveryRequired` lease. An otherwise
byte-identical foreign quarantine, an existing directory generated
by the earlier offline primitive, a changed marker or an absent marker
is NOT accepted for durable SQL isolation. Both paths, neither path
and unexpected symlinks/files also reject. No directory is deleted
or overwritten. Tests cover SHA1/SHA256 Git, simulated OS-success/
SQL-failure replay and refusal of graph-identical but unmarked
preexisting quarantine.

This protects against accidental/foreign on-disk lookalikes
within the application's trusted filesystem model; it is not a
cryptographic defense against a fully privileged host attacker
who can change both the database and all filesystem contents.
The SQL receipt still does not authorize a new generation, a
GitHub destination push or migration completion.

### Cancellation, contested writes and CI bounds

The physical session checks cancellation after its **second** Git
closure/fsck and final F-035 bundle read, immediately before invoking
the immutable SQL writer. A canceled handoff leaves the exact
reviewed quarantine bytes and `Prepared` evidence untouched:
the SQL callback is not invoked and **no `Isolated` event is created**.
A later explicitly invoked coordinator must re-verify the complete
source, reviewer/intent, marker and Git graph.

Integration tests use real SHA-1 and SHA-256 local Git object formats,
including an original path with a forged marker before rename,
a quarantined path with a forged/missing marker after rename,
crash-after-rename/before-commit reconciliation, cancellation at the
physical/SQL boundary and three independent PostgreSQL contexts racing
to record the same terminal event. The three-replica test has a
**three-minute cancellation deadline** to fail if an advisory/POSIX
lock deadlock occurs, instead of silently hanging CI. These are
test requirements and code paths, **not proof of a green PostgreSQL
run** until the final workflow completes all four quality gates.

Issue #125 preserves exactly four gates while canceling older
superseded runs on the same GitHub ref. A canceled PostgreSQL job is
not evidence that #123 or #124 passed. Isolation remains disabled by
default, and #121's separate generation-fenced re-admission remains
unimplemented.
