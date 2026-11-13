# F-070 — atomic source snapshot admission and multi-cycle incremental continuity

## Problem addressed

F-025 persists an immutable source-change journal per snapshot, F-026
prepares review-only changes, and F-068/F-069 fence and recover worker
leases with PostgreSQL time. Before F-070, `EnqueueAsync` checked
the latest staged snapshot **outside** the source advisory lock that
protects `PostgresStagingRepository.ApplyAsync`.

A concurrent staging commit could publish snapshot B after the
producer checked A but before A's queue row was inserted. The
preparation worker would ultimately detect supersession, but the
admission itself was not atomic against the source's evolving snapshot
head. This wastes resources and can mislead operators observing a
newly queued obsolete source cycle.

## F-070 code change

`PostgresIncrementalPreparationRepository.EnqueueAsync` now opens a
transaction and acquires the same advisory lock held by F-013 staging:

`pg_advisory_xact_lock(hashtext(sourceConnectionId.ToString()), 732911)`

**Only after acquiring this lock** does the method:

1. Verify the requested staged snapshot exists, belongs to the
   requested source and uses the immutable F-025 journal version 1.
2. Look up the latest committed source snapshot.
3. Reject a superseded snapshot with `IncrementalSnapshotSuperseded`.
4. Insert at most one `Queued` row per (snapshot, destination),
   together with its immutable first audit event using
   `ON CONFLICT DO NOTHING`.
5. Read the existing or newly inserted preparation, verify its source
   and commit the transaction.

There is no serialization across unrelated source connections: only
jobs for the **same source** contend on its advisory lock. The source
lock is also used in F-026 `PrepareAsync`; F-068's fresh PostgreSQL
clock remains the authoritative timestamp for queue admission.
The transaction's rollback discards partial queue/audit writes.

**Consequence:** every successful enqueue is ordered either before
or after a given staged snapshot commit. It cannot commit a stale A
when B was already published under the same source lock.

No new tables, columns, remote APIs, write permissions or operation
plan authorizations are introduced.

## Multi-cycle end-to-end local integration

A real PostgreSQL 17 scenario performs **five successive source scans**
for the same destination using the actual normalization, change journal,
queue, lease, item preparation and audit machinery:

| Cycle | Source observation | Expected source delta |
|---|---|---|
| A | Two source Git refs | Initial additions (project, repo, 2 refs) |
| B | Main ref advances | One `Changed` ref, revision 2 |
| C | Complete source scan without feature ref | One `Missing` ref, revision 2 |
| D | Complete scan with feature ref restored | One `Restored` ref, revision 3 |
| E | Same complete inventory | Zero changes, SHA-256 of empty review journal |

During cycle B a worker crashes after acquiring the lease. Two
independent PostgreSQL replicas race to recover it; **only one**
receives the fenced next attempt, and its event history records
`LeaseReclaimed` and `Claimed` before `Prepared`. A stale owner
cannot schedule a failure after recovery.

Additional assertions verify:

- Snapshot A's prepared evidence digest and items remain unchanged
  after the four later source cycles;
- prior source journals retain their exact counts and provenance;
- the final snapshot and queue can be replayed without duplicate
  source journal rows, queue entries or audit events;
- older snapshot B cannot be enqueued after E becomes current,
  including for a previously unseen target destination;
- a zero-delta source snapshot is a genuine, hash-sealed review-only
  preparation, not permission to skip authorization or push to GitHub.

A separate concurrency test holds the staging source lock,
starts an enqueuer for A, commits a newer B source marker while the
enqueuer waits and checks that **no A job** is created. Only B can
then enter the queue.

## Boundaries

- This is **incremental source discovery / review preparation
  continuity** only, not an executable destination synchronization
  loop. `Prepared` is not `MigrationComplete`.
- A complete source observation is required to record a deletion;
  partial scans never infer missing refs.
- Existing F-069 crash recovery, bounded retries and the reviewed
  execution authorization barrier remain unchanged.
- The staged collection timestamp establishes the ordering of source
  snapshots. Producer retries use the same source lock and deduplicate
  by (snapshot, destination). No automatic destination replay occurs.
- No schema/index migration is required for F-070; the existing
  source-staging lock and unique queue index are reused.

Run .NET 10/xUnit, PostgreSQL17 integration, React/Playwright and
Terraform validation in GitHub Actions. Changes are stacked on
F-069 PR #70; `main` remains untouched.
