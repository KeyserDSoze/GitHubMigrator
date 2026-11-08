# F-069 — auditable, bounded recovery of expired incremental worker leases

## Goal

F-026 already permits a new worker to reclaim an expired five-minute
`Running` preparation. F-068 made database time authoritative.
Previously, the reclaimed attempt only produced a new `Claimed` event.
Operators and F-067's timing diagnostics could not distinguish a crash
recovery from a scheduled retry: recovered final attempts were recorded
with **incomplete phase evidence**, even when the local source review
preparation succeeded.

F-069 makes this path explicit and bounded, while retaining **existing
fail-closed source-only execution fences**.

## Atomic recovery journal

The next `ClaimAsync` after a lease expiry selects the previous state
with `FOR UPDATE SKIP LOCKED LIMIT 1` inside its PostgreSQL transaction.
The existing predicates still require `attempts < max_attempts`,
source-connection scope and PostgreSQL-authoritative expiry.

- **Fresh or delayed `Queued`** job: unchanged single `Claimed(N)`
  event. A normal retry retains its earlier `RetryScheduled(N-1)`.
- **Expired `Running`** job: increment to attempt N, create
  `LeaseReclaimed(N-1)`, then `Claimed(N)` **in that order**, within
  the same transaction. `LeaseReclaimed` describes the fenced-out
  attempt, not a second success or a GitHub migration.
- A late owner still cannot call `PrepareAsync` or `FailAsync` with
  its old token/attempt; matching the exact lease token and attempt
  remains compulsory.
- Any failed claim or audit insert rolls back the entire transaction.
  No new state, no orphan recovery event and no incremented attempt
  become visible independently.
- F-067 uses `LeaseReclaimed(N-1)` as the **origin** of the successor's
  queue-wait phase, instead of discarding a valid recovered completion.
  The recovery admission wait can be zero because the recovery marker
  and successor claim share a database timestamp; it does not include
  elapsed downtime since the previous owner's heartbeat.

## Bounded exhausted-attempt cleanup

Previous `ClaimAsync` swept **all** expired fifth-attempt
`Running` rows with a single unbounded UPDATE, potentially waiting on
a competing process and delaying unrelated work.

The F-069 cleanup selects at most **64 expired, exhausted** records in
each poll with `FOR UPDATE SKIP LOCKED`, and changes them to terminal
`Failed` with one `LeaseExpiredAfterMaxAttempts` event **per row**.
The UPDATE and audit INSERT commit atomically. Subsequent calls drain
the remaining backlog; no worker ever starts a sixth attempt.

A new partial PostgreSQL index
`ix_f069_incremental_running_expiry(lease_until_utc, id)
WHERE state = 'Running'` helps the expiry scan; the previously
introduced source/state index remains available for source-scoped work.
For a populated production database, the index requires a reviewed,
online versioned migration; the development bootstrap creates it
idempotently but is **not** a production migration strategy.

## Non-goals and safety

- This is **recovery of review-only incremental preparation**, not
  resumption of an outbound GitHub push. No GitHub write credentials,
  remote write scopes, migration-complete claim, automatic retry policy
  expansion or source artifact overwrite is introduced.
- Existing bounded attempts and F-026 worker polling remain in force.
  A crash is not recovered by an API call without an active worker;
  the next eligible worker poll performs the fenced reclaim.
- The F-066 health API is unchanged. F-067 can now calculate valid
  final-attempt timing for an expired-lease successor.
- PostgreSQL server time from F-068 remains authoritative. The
  previous owner must be treated as untrusted even if it reconnects.
- No new database columns are necessary: the event type fits the
  existing append-only `incremental_preparation_events` schema.

## Real PostgreSQL tests

1. Four independent replicas race to reclaim the same expired
   first-attempt lease. Exactly one receives the second attempt;
   immutable audit events are `Queued, Claimed, LeaseReclaimed,
   Claimed, Prepared` in order. The stale original owner cannot
   publish a result or schedule a retry. F-067 reports one **valid**
   timing chain for the recovered completion.
2. Four pollers race to close an expired fifth attempt. It becomes
   terminal `Failed` with exactly one matching audit event and
   no sixth claim; the old token is denied for both retry and
   completion.
3. A set of 67 exhausted leases demonstrates that a single poll
   finalizes **at most 64**; subsequent polls drain the other three,
   and repeat polling emits no duplicate terminal event.

Run all .NET 10, PostgreSQL 17 integration, Playwright and Terraform
CI gates before merging. The new branch is stacked on F-068; `main`
is unchanged until reviewed merges are intentionally performed.
