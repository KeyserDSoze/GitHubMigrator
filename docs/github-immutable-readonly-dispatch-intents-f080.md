# F-080 — immutable, write-disabled remote sandbox dispatch intents

## Goal

F-079 exposes a trusted superadmin-only GitHub App GET-only review and
immutable F-078 session evidence. It does not yet have a durable handoff
record for a later *separately authorized* transport implementation.

F-080 adds precisely that **inert handoff**, not an execution outbox.
A successful response MUST NOT mean a push has been scheduled, authorized,
performed or verified. A future remote sender would need a separately
reviewed source/target authorization boundary, a pessimistic durable
pre-send barrier, destination-side non-destructive create-only semantics,
and post-send object readback. F-080 is not its queue.

## API

Under the existing scoped F-079 root
`/api/incremental-preparations/{preparationId}/operation-plans/{planId}/github-app-scopes/{scopeId}/remote-sandbox-review-sessions/{sessionId}`:

- `POST /dispatch-intents`: body-free manual request. The JWT actor must
  have the `superadmin` role and must differ from the F-078 observer,
  F-055 preparer and F-052 original human actors.
- `GET /dispatch-intents/{intentId}`: exact scoped historical inspection
  of the immutable evidence and current F-078 read-only status.

Only a configured test repository is accepted. These routes have their
own eight-requests-per-minute limiter and reject by default with
`SandboxDispatchIntentDisabled`. No GitHub token, signing key, ref,
refspec, repository URL, GitHub API mutation, subprocess, transport
callback, send command or enqueue operation exists in F-080.

## PostgreSQL authority and exactly-once semantics

F-080 adds `github_remote_sandbox_dispatch_intents` and
`github_remote_sandbox_dispatch_intent_events` **after F-078**.

A record contains only: the immutable F-078 session ID and SHA,
the F-055 hazard and F-052 grant IDs, numeric target repository,
independent actor, exact F-074 review digest, F-044 packet and candidate
digest, latest typed F-031 ref digest, DB-clock record time, bounded
expiry, canonical F-080 SHA-256 and permanent `PreparedWriteDisabled`
state. All four flags `can_execute`, `transport_invoked`,
`may_automatically_retry`, `migration_complete` are permanently
false in both the domain hash check and SQL CHECK constraint. No remote
refs or OIDs leave the stage.

The recorder uses one PostgreSQL `READ COMMITTED` transaction. It
acquires numeric-repository advisory locks **732911** then **733050**
before sampling the database clock. It validates the scope and
rehashes the F-078 session + unique Recorded audit, rechecks its
live F-045/F-047/F-050/F-052/F-055 control-plane state,
verifies the same F-055 hazard and reviewed grant, independent human
actors, matching packet, baseline, candidate set and preflight SHA,
and reuses only the exact previous intent for an unexpired, unchanged
session. UNIQUE session/hazard plus lock serialization guarantees one
row + one audit event across replicas. Different actors cannot overwrite
the result. A completed possible-send marker or expired/changed
authority rejects even an otherwise idempotent replay.

PostgreSQL BEFORE UPDATE/DELETE triggers prohibit rewriting or deleting
the intent or event. All inspection validates the SHA and audit first,
and returns `ReadOnlyIntentCurrent` only while the F-078 review is
current and its own expiry has not passed. Otherwise it is
`HistoricalOnly`. Both statuses return
`CanExecute=false`, `CanRetry=false`, `MigrationComplete=false`.

## Verification

- Real PostgreSQL 17 complete F-075/F-077/F-078 chain + F-080 receipt
  and exact idempotent replay.
- Six independent PostgreSQL connections, one row and one audit event.
- Scoped readback denies wrong preparation, plan and scope.
- Changed actor is refused; F-055 `MayHaveTransmitted` demotes to
  historical and blocks replay.
- SQL-level tampering of execute flag, hash, row or event is rejected.
- Competing F-055 transaction marking `MayHaveTransmitted` while
  a recorder waits on the repository lock preempts the handoff.
- Production TestServer JWT role matrix, explicit disabled-default
  conflicts, and zero provider HTTP requests.

No frontend execution control or migration-complete indicator is
introduced. This is a stacked F-080 PR on F-079; `main` remains
unchanged.
