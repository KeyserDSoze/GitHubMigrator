# F-064 — PostgreSQL-authoritative clock for source-mirror leases

## Why this matters

F-061/F-063 used `TimeProvider.GetUtcNow()` from each application
replica when taking a lease, refreshing its deadline and deciding
whether a generation had expired. A fast host could reject a healthy
worker prematurely; a slow host could treat an already-expired
generation as still active. Cross-replica token/generation checks alone
cannot repair disagreements about *when* the token expires.

## Behavior

The PostgreSQL lifecycle journal obtains a fresh UTC
`SELECT clock_timestamp() AS "Value"` **inside the same transaction,
after the per-mirror `pg_advisory_xact_lock` has been acquired**.
This database timestamp is authoritative for:

- admitting a fresh owner or reclaiming an expired lease;
- extending the current owned generation's deadline, including the
  F-063 hard two-hour lifetime limit;
- verifying F-062 physical-source proof freshness and ownership on
  completion;
- quarantining a currently owned generation after physical verification
  fails;
- both mutable status timestamps and append-only lifecycle events.

Using PostgreSQL `now()` / `transaction_timestamp()` would be
**incorrect**: they return the timestamp from the beginning of a
transaction. A worker may spend time blocked behind the advisory lock,
during which the previous owner can expire. `clock_timestamp()`
takes a live post-lock reading instead. It is **not** a promise that
physical time is monotonic across a database restart, a manual clock
jump or an unexpected database clock adjustment.

No application server's `TimeProvider` supplies an ownership or
expiry decision any longer. Database deadlines remain normalized to
milliseconds so EF Core/Npgsql round trips agree with the receipt sent
to workers. PostgreSQL `timestamptz` is the timestamp source, not a
client-provided offset.

## Important limits

- F-060 physical Git verification still timestamps its sealed result
  using the Git worker's clock. F-062 fails closed if that clock
  disagrees with the database by more than the accepted proof window
  (two minutes old, five seconds into the future). **F-064 makes
  database lease fencing consistent; it does not eliminate the need for
  NTP or implement a trusted remote-proof clock.**
- A PostgreSQL clock jump remains an operational risk. Operators should
  monitor database time synchronization and drift. No external time
  service, network privilege or distributed consensus protocol is added.
- This is internal only, **default-disabled**, and adds no GitHub
  write scope, remote push, API endpoint, automatic retry or migration
  completion. An expired owner still cannot commit or renew, even
  with a valid historic source proof.
- No SQL schema or data migration is required for the clock-source
  change. Existing F-061/F-063 status rows retain their immutable
  evidence and the original admission timestamps. Deployment should
  ensure synchronized infrastructure clocks.

## Tests

1. PostgreSQL 17 real-Git test brackets admission, heartbeat and
   verification with independent live database timestamps and asserts
   that the database-owned row and immutable events fall inside them.
2. A held advisory lock lets a source lease expire *while another
   transaction is waiting*. After release, the successor can reclaim
   with a new generation; the old token cannot renew. This guards
   against switching to transaction-start time.
3. The existing F-060–F-063 suites still cover real SHA-1/256 Git
   source object verification, old-deadline rejection, lease-lifetime
   limits, proof freshness, crash recovery and SQL no-write fences.

Run all .NET/xUnit, PostgreSQL/Git integration, React/Playwright and
Terraform validation gates. GitHub.com HTTPS write tests are neither
needed nor authorized by this feature.
