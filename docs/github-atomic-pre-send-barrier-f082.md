# F-082 — atomically consume a reviewed sandbox epoch before any possible send

## Why this feature exists

F-081 records a signed, independently confirmed, still write-disabled
sandbox review. F-055 has a durable irreversible
`MayHaveTransmitted` hazard, but its own standalone API has no
direct binding to that complete F-081/F-080/F-078 chain.

F-082 supplies the missing **transaction boundary**: it consumes
one independently confirmed review and atomically commits an F-055
possible-send marker and a new, immutable F-082 audit record. This is
the one-way **at-most-once safety barrier** for a future, separately
authorized transport. Even an identical caller after a connection loss
must NOT invoke the operation again or resend anything.

**F-082 itself never sends Git data.** Its evidence permanently says
`MayHaveTransmittedWriteDisabled`, which expresses pessimistic
uncertainty, NOT an actual GitHub transmission. It does not claim the
target was updated, or that the migration succeeded. It provides NO
transport credentials, refspec, command, worker or dispatch rights.

## Critical transaction protocol

`PostgresGitHubRemoteSandboxPreSendBarrierJournal.RecordPossibleSendAsync`
is **internal-only**, not registered as an HTTP endpoint or background
worker. The only accepted inputs are authoritative immutable GUIDs
for the confirmation/intent, preparation, plan, scope, and a distinct
operator ID. No caller can submit source refs, OIDs, URL, credentials,
SHA-256 values or transport flags.

The operation:

1. Rejects unless explicitly enabled for a single numeric test repo
   (feature disabled by default).
2. Opens a real PostgreSQL transaction and acquires the existing
   repository advisory locks **732911 then 733050**, in order.
3. Reads PostgreSQL's clock; re-hashes exact F-081/F-080/F-078
   historical evidence and their unique audit records, requiring F-081
   to remain current.
4. Loads and rechecks F-055, F-052, F-050, F-047 and F-045 persisted
   authority under the locks, including actors, plan/scope/repository,
   source packet, candidate hash, original policy, baseline,
   preflight and deadlines.
5. Requires the new marker actor to differ from all preceding humans.
   Rechecks the database clock just before the CAS.
6. **In the same transaction** atomically CAS-updates the F-055 hazard
   from `PreparedWriteDisabled` to `MayHaveTransmitted`, writes its
   F-055 `MayHaveTransmitted` event, and inserts one sealed F-082
   record plus one F-082 `Recorded` audit.
7. Commits all changes together. A failure or transaction rollback
   leaves neither F-055 mark nor F-082 record. After a successful
   commit, every further F-082 call is refused: there is no idempotent
   replay or automatic retry.

Physical PostgreSQL `UNIQUE` constraints on confirmation, intent,
session and hazard prevent duplicate send barriers. Triggers reject
UPDATE/DELETE of F-082 evidence or audit, and SQL CHECK constraints
force `can_execute=false`, `transport_invoked=false`,
`may_automatically_retry=false`, `migration_complete=false`.
F-055 itself permanently keeps its
`transport_authorized=false` and `transport_invoked=false` flags.
This means even a successful F-082 record is never proof that a
transport occurred.

Historical inspection always returns `HistoricalOnly`, verifies
F-082's SHA-256 + unique audit, exact confirmation provenance, the
committed F-055 `MayHaveTransmitted` transition, actor/time and
ordered hazard events. No "ready to send" or "can retry" indicator
exists.

## Tests

- Full real PostgreSQL 17 F-075/F-077/F-078/F-080/F-081/F-082
  preparation: an atomic marker, one F-055 transition and one F-082
  immutable record and event.
- Six concurrent application replicas: exactly one successful marker,
  one F-055 transition event and five permanent refusals.
- Wrong preparation/plan/intent and same person cannot commit.
- An existing F-055 possible-send marker prevents a new F-082 record.
- A competing `MayHaveTransmitted` update winning the repository lock
  prevents the waiting F-082 caller from committing.
- SQL mutation, delete and write-flag tampering are rejected.
- Domain SHA tampering and forged executable/retry flags are rejected.
- Disabled-by-default service refuses before querying PostgreSQL.
- Existing API TestServer startup exercises new schema initialization.

## Next boundary

F-082 is the last durable pessimistic **pre-send** primitive, not a
GitHub transport implementation. A future remote sender must be
independently authorized and, once F-082 commits, attempt at most one
bounded, create-only transport against an isolated test repository.
It must never infer success from the F-082 marker; only a separately
audited authenticated remote readback and object-closure verification
can establish observed target state. A process restart, timeout or
lost acknowledgment must go to manual reconciliation and NEVER
reacquire this barrier.

This is stacked on F-081; `main` remains untouched.
