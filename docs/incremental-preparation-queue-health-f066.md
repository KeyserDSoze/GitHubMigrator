# F-066 — source-scoped incremental preparation queue health

## Purpose and scope

F-026 persists review-only incremental preparation jobs in PostgreSQL.
F-066 introduces **read-only source-scoped diagnostics** for operators to
identify queue backlog, delayed retries, expired worker leases, terminal
error categories and completed review preparation throughput.

This is **not a migration progress percentage**, a GitHub write operation,
or a scheduler. `Prepared` means that source change metadata is ready for
human review; it does **not** mean the repository was migrated or pushed.
No new execution permissions, retry, change application or migration-complete
flags are introduced.

## API, authorization and safety

`GET /api/operations/incremental-preparations/{sourceConnectionId}/health`

- Requires JWT, `password_changed=true`, **superadmin** role and an isolated
  12/minute, zero-queue limiter (`incremental-queue-health`).
- Configuration: `IncrementalQueueHealth:Enabled=false` by default.
  When disabled, HTTP 404 contains no queue information, including whether
  the requested source exists.
- A non-empty GUID is required. For an unknown source GUID, an enabled
  reader returns zero aggregates rather than exposing source metadata.
- Only PostgreSQL is accessed; no third-party API, Git execution or
  destination write adapter is resolved.
- Source connection IDs are **used as a bound SQL parameter**, never
  concatenated into SQL. The response contains no source IDs, actor IDs,
  destination IDs, snapshot IDs, Git refs, raw error codes, lease tokens,
  credentials, repository names or individual job records.
- A composite PostgreSQL index
  `ix_f066_incremental_health_source_state(source_connection_id, state)`
  is created by the existing local bootstrap schema and reflected in EF's
  mapping. Production deployment should use a reviewed versioned migration,
  considering lock time on a populated table.

## Metrics

All metrics are calculated by **one PostgreSQL statement** using a single
materialized `clock_timestamp()` observation and filtering by source:

| Field | Meaning |
| --- | --- |
| `totalRuns` | All persisted F-026 preparation jobs for the source |
| `queuedReady` | `Queued`, with `available_at_utc <= observedAtUtc` |
| `queuedDeferred` | `Queued`, waiting on future retry/backoff |
| `runningCurrent` | `Running` with an unexpired lease |
| `runningExpired` | `Running` with a deadline already reached |
| `prepared` | Review-only metadata preparation succeeded |
| `failed` | Terminal F-026 failures, not GitHub transfer failures |
| `retryingQueued` | Queued runs with `attempts > 0`, subset of queued |
| `preparedLast24Hours` | Prepared runs completed within the last day |
| `preparedReviewItemsLast24Hours` | Sum of source review-item counts for those runs |
| `failedLast24Hours` | Terminally failed runs updated within the last day |
| `failedSourceEvidence` | Terminal failures from known source consistency/journal checks |
| `failedTimeoutOrLease` | Terminal timeout or exhausted-lease failures |
| `failedOther` | All other terminal failures, including unknown codes |
| `averageTurnaroundSecondsLast24Hours` | Average time between queue insertion and successful preparation for runs prepared within 24h, or null |
| `oldestReadyWaitSeconds` | Maximum age since insertion of a currently ready queued run, or null |
| `attentionRequired` | Expired `Running` lease or terminal failure within the last day |

**Partition invariant:**

`totalRuns = queuedReady + queuedDeferred + runningCurrent + runningExpired + prepared + failed`

The retry count is a subset of queued rows and is not added to this sum.
Terminal failure categories partition `failed`. Null turnaround/wait
means *no qualifying rows*, not a claim of zero seconds of processing.

Turnaround includes time spent waiting in the queue and is **not**
the Git worker's active processing duration. The F-026 queue currently
does not checkpoint per-phase runtime. This feature therefore does not
invent per-phase execution timings or a migration completion percentage.
Unknown error codes are grouped into `failedOther`, not exposed to API.

## Operational boundaries and test coverage

- An expired `Running` lease can be reclaimed only by existing F-026
  fenced worker logic. Health **never** reclaims, retries or quarantines it.
- The diagnostic query does not take per-job advisory locks, does not
  select individual rows into .NET and does not mutate audit events.
  It is source-filtered and uses a standalone low request budget.
- Real PostgreSQL integration tests insert representative F-026 state
  snapshots (queued/backoff/running/expired/prepared/three failure classes)
  on a committed staging source. They verify the data partition, source
  scoping, database timestamp, throughput, age, nullable empty-source
  semantics, default-off refusal and that no identifiers are serialized.
- Full ASP.NET/JWT tests verify the anonymous, ordinary user, admin,
  pending-password and superadmin feature-off access matrix.
- The full CI suite must pass .NET/xUnit, PostgreSQL 17 real integration,
  React/Playwright and Terraform validation.

This is an operational visibility slice only. No GitHub push, remote
write grant, automatic retry, migration completion or `main` merge.
