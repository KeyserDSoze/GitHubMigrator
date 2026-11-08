# F-065 — read-only PostgreSQL source-mirror fleet health

## Purpose

F-061–F-064 introduced durable, fenced leases for physical local source
Git mirrors, heartbeat renewal and a PostgreSQL-authoritative clock.
F-065 gives a **superadmin** a safe operational snapshot of how many
source-mirror leases are active, nearing expiry, expired, verified or
quarantined. This helps diagnose stuck/disconnected workers without
granting new GitHub write permissions or exposing credentials.

This is a **database-state report**, not proof that a local Git process
is making progress or an automated recovery scheduler. The Git source
materializer and lifecycle remain **disabled by default**.

## API and contract

`GET /api/operations/source-mirrors/health`

- Requires a valid JWT, `password_changed=true`, **superadmin** role
  and the dedicated **12/minute, zero-queue** `source-mirror-health`
  rate-limiting policy.
- When `GitHubSourceMirrorLifecycle:Enabled=false`, returns HTTP 404
  with no lifecycle metadata. No public or admin bypass exists.
- When enabled, returns a single JSON summary:
  `observedAtUtc`, `totalMirrors`, `activeLeases`,
  `expiredLeases`, `expiringSoonLeases`, `verifiedMirrors`,
  `recoveryRequiredMirrors`, `oldestExpiredAtUtc`
  (nullable) and `attentionRequired`.
- No mirror IDs, snapshot/workpack IDs, worker IDs, random lease tokens,
  source refs, GitHub credentials, filesystem paths, unfiltered error
  messages or individual row details are selected or returned.
- Read-only: no advisory locks, lease state changes, Git execution,
  remote HTTP calls, automatic retry, write scopes, migration completion
  or permission grants.

### Semantics

A **single PostgreSQL statement** uses a materialized CTE with
`clock_timestamp()` as one observation instant. It aggregates the
persisted lifecycle rows directly in SQL, not in the .NET process:

- `activeLeases`: state = `Leased` and deadline strictly **later**
  than observation instant.
- `expiredLeases`: state = `Leased` and deadline **at or before**
  the observation instant. These rows are not automatically reclaimed.
- `expiringSoonLeases`: active leases with deadlines within **five
  minutes**. This is a subset of `activeLeases`.
- `verifiedMirrors`: state = `Verified`; this proves only the *local
  source mirror* inspection, **not** a GitHub migration.
- `recoveryRequiredMirrors`: quarantined state requiring manual review.
- `oldestExpiredAtUtc`: oldest deadline among expired leases, or
  null if there are none.
- `attentionRequired`: true if any lease has expired or a mirror is
  quarantined. This is a diagnostic flag, **not** an SLA breach or
  automatic release permit.

The core partition invariant is:

`totalMirrors = activeLeases + expiredLeases + verifiedMirrors + recoveryRequiredMirrors`.

The query returns one row of zero counts for an empty table. It is
O(number of lifecycle rows) with one bounded scan; the independent
rate limit reduces cost, but a future telemetry pipeline should export
preaggregated series for high-frequency monitoring. The endpoint does
not page individual worker records.

## Tests and safety

- Real PostgreSQL 17 + local Git integration creates representative
  active, near-expiry, expired, physically verified and quarantined
  records, then asserts the partition invariant, a database-clock
  observation bracket, immutable event history and no sensitive field
  serialization.
- The real ASP.NET/JWT TestServer denies anonymous and non-superadmin
  callers, including users who have not changed their password.
- Feature-off reads fail closed; F-061–F-064 safety and lease fencing
  remain unchanged. No live GitHub push or remote migration is tested.

Run .NET/xUnit, PostgreSQL/real-Git integration, Playwright and
Terraform CI gates. Do **not** interpret green diagnostics as end-to-end
migration success. Recovery remains a deliberate operator action.
