# F-068 — PostgreSQL time authority for incremental preparation

F-026 persisted review-only incremental preparations with five-minute
leases, bounded retries, immutable events and an execution deadline.
F-066/F-067 made this observable: F-067 computes queue and final-attempt
durations from event timestamps. Previously these timestamps were
provided by the executing replica's `TimeProvider`, leaving distributed
claims, backoff and evidence dependent on local clock synchronization.

## Scope and behavior

F-068 makes PostgreSQL `clock_timestamp()` the **production
authority** for all F-026 queue lifecycle state timestamps:

- `EnqueueAsync`: availability, creation, update and immutable `Queued`
  event timestamps come from a live database time reading.
- `ClaimAsync`: live database time **inside the transaction** decides
  whether a queued retry is ready or a running lease expired. The
  five-minute deadline and `Claimed` event share that time.
- `PrepareAsync`: a second live database clock reading **after acquiring
  the source advisory lock** fences worker ownership and, at successful
  completion, stamps both the `Prepared` row and event.
- `FailAsync`: locks the preparation row first (`SELECT ... FOR UPDATE`)
  and then reads the PostgreSQL clock. This prevents a worker blocked
  behind a completing/recovering owner from using a pre-lock time to
  extend or retry an already expired lease. Retry scheduling and
  `RetryScheduled` / `Failed` events share the same database instant.

These are **separate, fresh** `clock_timestamp()` readings, not
`now()` / `transaction_timestamp()`, which freeze at the transaction
start and can be stale after waiting for locks.

The source-only `PostgresStagingRepository` still receives the
`TimeProvider` for its existing independent journal logic. Production
dependency injection explicitly constructs
`PostgresIncrementalPreparationRepository(..., useDatabaseClock: true)`.
The optional `false` parameter is an explicit deterministic test
override only, applied to preexisting F-026/F-067 manual-clock tests.
There is no API/configuration flag that disables authoritative queue
time on a running application.

## Integrity and limitations

- All existing exact lease token, snapshot, source, destination, attempt,
  state and expiration predicates remain intact. No automatic retry is
  added: only preexisting bounded F-026 retry rules can schedule retries.
- No source Git operation, GitHub write scope, migration execution,
  complete flag, credentials, public diagnostics or new database
  tables/columns/indexes. `main` remains unchanged.
- The previous F-067 diagnosis can now compare `Queued`,
  `RetryScheduled`, `Claimed` and `Prepared` in the same database
  clock domain. In-flight jobs that started before rollout can contain
  historical local timestamps, so F-067 continues to discard invalid or
  inconclusive chains rather than fabricate elapsed durations.
- PostgreSQL itself still needs well-synchronized time. Manual clock
  steps or failover to a server with large clock drift remain operational
  risks. This change does **not** implement logical monotonic time or
  an independent external time oracle.
- There is no schema migration required. Keep the application's
  existing transaction isolation levels and PostgreSQL advisory lock
  strategy.

## Verification

Two real PostgreSQL 17 integration scenarios are added:

1. Inject a worker clock twelve hours **ahead**, then twelve hours
   **behind** database time. Enqueue, claim, retry backoff, reclaim and
   completion must all use actual PostgreSQL time; event ordering and
   F-067 final-attempt timing coverage remain valid.
2. Hold the preparation row lock while the owner's lease expires.
   A blocked `FailAsync` may neither append a retry event nor change
   the running status. A successor then claims a fresh generation;
   the old lease is denied for both failure and preparation.

All preexisting manual-clock tests explicitly preserve their test-only
time source; the real API and TestServer use PostgreSQL authority by
default. Validate all xUnit, PostgreSQL/Git, Playwright and Terraform
CI gates.

This is still source preparation **for review**, not a completed
migration to GitHub.
