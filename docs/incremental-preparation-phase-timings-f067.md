# F-067 — event-backed phase timings for incremental preparation

## What F-067 measures

F-066 reports source-scoped queue health and enqueue-to-prepared turnaround.
That metric includes time spent waiting, retry backoff, and worker execution;
it cannot distinguish a slow queue from a slow preparation step.

F-067 adds **read-only phase timing diagnostics** based on F-026's existing
immutable PostgreSQL audit events, without introducing instrumentation that
could change worker commits, retries, fencing, or the sensitive source journal.

**API**: `GET /api/operations/incremental-preparations/{sourceConnectionId}/phases`

- Same `IncrementalQueueHealth:Enabled=false` default-off flag,
  password-rotated JWT **superadmin** requirement and dedicated
  12/minute zero-queue policy as F-066.
- A valid source GUID is bound as a **SQL parameter**. Unknown sources
  yield zero counters and null durations; an empty GUID fails validation,
  unless the feature is disabled (in which case all calls return 404).
- No identifiers, lease tokens, source refs, raw errors, GitHub repository
  names, credentials, filesystem paths or individual job timings leave
  PostgreSQL. The endpoint is purely diagnostic: **no mutation, claim,
  retry, Git operation or destination write**.

## Durable phase evidence

For each `Prepared` run within the past 24 hours, resolve a **single
final-attempt event chain** using the existing
`incremental_preparation_events(preparation_id,id)` index:

1. For attempt **1**, origin = `Queued` (attempt **0**). For a successful
   retry attempt **N > 1**, origin = `RetryScheduled` (attempt **N-1**).
2. Next = `Claimed` for **attempt N**, ordered by immutable event ID.
3. Final = `Prepared` for **attempt N**, with its event time **equal**
   to the persisted `prepared_at_utc` for that run.

The chain must be in increasing event-ID order and its timestamps must
be nondecreasing, end no later than the database's observation instant
(`clock_timestamp()`), and have claim-to-prepared elapsed time of **at
most five minutes** (the F-026 lease limit). A stale/reclaimed claim
cannot be paired to a later attempt.

Metrics returned:

| Field | Definition |
|---|---|
| `observedAtUtc` | Single materialized PostgreSQL observation clock |
| `preparedLast24Hours` | All prepared runs in the observation window |
| `validPhaseSamples` | Runs with complete, chronologically valid final-attempt chains |
| `missingOrInvalidSamples` | Other prepared runs; excluded from duration statistics |
| `retriedPreparedLast24Hours` | Prepared runs whose final attempt > 1 (may have invalid timing) |
| `averageQueueWaitSeconds` | Mean origin-to-claim elapsed time for valid final attempts |
| `averageClaimToPreparedSeconds` | Mean claim-to-prepared time for valid final attempts |
| `p95ClaimToPreparedSeconds` | PostgreSQL `percentile_cont(0.95)`, valid final attempts |
| `maxClaimToPreparedSeconds` | Maximum claim-to-prepared elapsed time in valid samples |
| `timingEvidenceIncomplete` | True if any in-window prepared run lacks valid evidence |

**Important:** the measured queue wait includes any scheduled retry backoff.
Claim-to-prepared is *lease occupancy*, not instrumented CPU, I/O,
individual validation/hash phase duration, or remote GitHub migration time.
The aggregate window is defined by **successful preparation time**, not
by claim time. It does not cover failed attempts or unfinished runs;
F-066 reports their counts and errors separately. No migration
percentage or end-to-end completion is inferred.

**Clock caveat:** F-026 events were recorded using worker-local
`TimeProvider`, while the observation and window use PostgreSQL's
clock. F-067 rejects negative/future or longer-than-lease intervals;
it cannot detect all positive-duration clock skew across replicas.
Timestamps should remain synchronized. Converting the F-026 queue
event clock to a database authority would be a distinct, explicitly
tested lifecycle change.

## Performance and compatibility

- One read-only SQL statement with a source-scoped 24-hour `Prepared`
  selection and indexed LATERAL lookups for **only the final attempt's**
  events. Each event lookup is bounded to one matching row.
- Partial PostgreSQL index
  `ix_f067_incremental_prepared_source_time
  (source_connection_id, prepared_at_utc DESC) WHERE state='Prepared'`
  and the preexisting indexed `(preparation_id,id)` event lookup.
- Existing F-026 database schema and events are not changed.
  Local bootstrap adds the index; a reviewed, online versioned
  production migration is still required for large live datasets.
- Real PostgreSQL regression tests cover successful preparation,
  final-attempt retry binding, 24-hour times, P95, hard-lease-time
  rejection, incomplete event evidence, empty source, disabled flag,
  no mutation and confidentiality.
- Full ASP.NET/JWT test verifies anonymous, ordinary-user, admin,
  unchanged-password and superadmin feature-disabled behavior.

Do not enable GitHub write scopes or claim that a `Prepared` source
has been migrated. The feature leaves all source/destination execution
fences and automatic retry policies unchanged.
