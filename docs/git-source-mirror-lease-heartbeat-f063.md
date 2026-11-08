# F-063 — fenced heartbeat for long-running local source Git mirrors

F-061 introduced a fixed 15-minute PostgreSQL lease to serialize F-060
local Git bundle verification across replicas. A large but healthy `git fsck`
or bundle import can exceed that deadline, allowing a second worker to reclaim
the slot before the original process finishes. F-063 extends **only the owned
lease's deadline**, without relaxing F-062's fresh-proof or the existing
no-remote-write boundaries.

## Contract and operational behavior

- `IGitSourceMirrorLifecycleJournal.RenewAsync` runs under the existing
  per-mirror PostgreSQL transaction advisory lock and **reloads the current
  row** (no reliance on a cached EF entity).
- Renewal requires exact mirror/workpack/plan provenance, the random lease
  token, worker ID, generation, **previous** deadline, `Leased` state and an
  unexpired lease. An expired, superseded, quarantined or completed owner
  cannot renew. The returned receipt has the **same** worker, token and
  generation with an advanced, millisecond-normalized deadline.
- `UpdatedAtUtc` intentionally remains the original lease admission time
  while `Leased`; F-062 verifies a physical proof's timestamp against that
  time. Renewal must never move the proof-floor timestamp. Successful
  renewals append immutable `LeaseRenewed` audit events. The schema
  initializer upgrades the F-061 event-type CHECK, including on existing
  installations (versioned production migration still required).
- The internal durable materializer starts a periodic heartbeat after claim,
  once every **5 minutes** by default (one-third of the 15-minute lease).
  Each heartbeat has to finish before the wrapper can complete or quarantine.
  The wrapper hands the **latest deadline receipt** to completion, so the
  prior receipt cannot be used by a late owner.
- If renewal fails, the wrapper cancels the in-flight physical task and
  propagates the failure; it cannot mark a mirror verified. On cancellation
  or process crash, the last deadline expires normally and a new generation
  must fully re-hash and re-verify the physical mirror. No automatic retry
  of a quarantined or uncertain source is introduced.
- Each generation has an **absolute 2-hour upper bound** from admission.
  Heartbeats cannot keep a wedged process's slot alive indefinitely. The
  final renewal is truncated to this bound; subsequent renewals fail
  closed with `SourceMirrorLifecycleTimeLimit`. Operational orchestration
  must explicitly handle work that legitimately takes longer.

## Safety boundaries

This is solely a *local source mirror* liveness/fencing change, not an
authorization for sending data to GitHub. It does **not** add a remote
`push`, GitHub write token, GitHub API route, automatic retry, migration
completion, scheduling or enable a feature flag. F-061/F-059 remain
default-disabled, and F-055 `MayHaveTransmitted` semantics remain unchanged.

A heartbeat proves ownership of a **database record**, not that a Git
subprocess is making progress or that a hostile same-process component has
performed `git fsck`. Physical F-060 POSIX shared-volume locking and
F-062 per-verification timestamp sealing are still required.

The existing journal uses the application's `TimeProvider`; replicas must
maintain trustworthy synchronized UTC clocks. Cross-host clock authority and
operational metrics/alerts are separate follow-up work. The 15-minute TTL
and 2-hour absolute bound are fixed conservative defaults, not dynamically
estimated from a repository's size.

## Tests

- xUnit: a gated slow materializer cannot complete until a renewal succeeds;
  renewal failure cancels physical work and cannot commit.
- PostgreSQL 17/real Git: renewal retains exact ownership, rejects the old
  deadline, and accepts an F-062 physical proof obtained after admission but
  before renewal. Immutable `LeaseRenewed` event ordering is asserted.
- PostgreSQL: expired and reclaimed owners cannot renew, the next generation
  remains independent, and the 2-hour cap cannot be bypassed.
- Existing tests continue to exercise stale-worker fencing, crash recovery,
  proof tampering, the permanent no-write SQL constraints and local Git SHA-1
  and SHA-256 closure.

Run the .NET, PostgreSQL/Git integration, React/Playwright and Terraform CI
gates before treating the branch as validated. No live GitHub.com remote push
or migration execution is part of these tests.
