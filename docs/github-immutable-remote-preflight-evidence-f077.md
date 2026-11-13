# F-077 — immutable PostgreSQL journal for reviewed F-051 preflights

## Background

The F-076 trusted read-only orchestration path reconstructs the F-044
candidate source and obtains a fresh F-072/F-073 authenticated GitHub
readback, then uses F-075 to verify current PostgreSQL lease/review/hazard
authority under repository-scoped advisory locks.

Its historical `GitHubRemoteAppPreflightReceipt`, however, was still
supplied as an in-memory parameter. A caller could select an arbitrary
receipt body with a plausible-looking SHA-256, and F-075 could only
cross-check it against the F-052 reviewed grant's persisted digest.

F-077 records the authentic historical F-051 receipt **when the existing
F-051 service finishes its authenticated read-only operation**, and
exposes a new internal F-076 path that resolves the exact reviewed
receipt from the database instead of accepting its body from a caller.

## PostgreSQL schema

New, additive `github_remote_app_preflight_evidence` and
`github_remote_app_preflight_events` tables are initialized during
application bootstrap after F-050 and F-052 schemas. Records use
`(request_id, generation, evidence_sha256)` as an immutable composite
primary key, and the event journal has a matching foreign key and a
unique `Recorded` event per evidence SHA.

**Why the SHA belongs in the key:** F-052's proposal and independent
human review each perform a fresh F-051 read. The later review can have a
different token expiry, read timestamp and proof hash for the SAME F-050
fence generation. F-077 preserves **both independently**; it never
silently overwrites the proposal's evidence with the review's evidence.

The schema persists **all original F-051 receipt fields**, plus
`policy_observed_at_utc`, which F-051's original cryptographic digest
incorporated but did not expose in its DTO. It also captures
`recorded_at_utc` from the PostgreSQL server clock. Every SHA field,
state, candidate count, repository ID, token-expiration relation and
permanently false write flag is checked at the database level.

Neither table stores GitHub installation bearer tokens, App JWTs,
private signing keys, Git transport commands, raw ref names or OIDs,
repository URLs, or any executable authorization.

Both tables have PostgreSQL `BEFORE UPDATE OR DELETE` rejection
triggers. All application writes are append-only INSERTs.
The evidence table has a foreign key to the globally scoped F-050
repository fence, and an index for chronological readback by repository.

## Recording and replay

`PostgresGitHubRemoteAppPreflightEvidenceJournal.RecordAsync`:

1. Validates that the input has F-051's exact read-only verified state,
   valid digests, matching typed-target baselines, sane source counters,
   plausible timestamps and permanently false write flags.
2. **Recomputes** the exact SHA-256 with F-051's original domain tag,
   packet/policy/baseline digests, typed-ref and policy timestamps,
   GitHub App token expiry and no-write flags. A mismatch refuses
   recording before opening a transaction.
3. Requires the exact operator-configured numeric repository allowlist,
   starts a PostgreSQL transaction and takes the existing F-050/F-052/F-055
   repository advisory lock `(733050, hashtext(repositoryId))`.
4. Checks whether that **exact** `(request_id, generation, proof SHA)`
   already exists. A genuine retry is idempotent and produces no new
   event. A contradictory payload at the same key is rejected.
5. For new evidence, rereads the current F-050 fence and checks request
   ID, generation, packet hash, candidate set hash, original policy,
   independent human reviewer, deadline, state and permanent no-write
   flags using **PostgreSQL clock time after acquiring the lock**.
   A changed fence prevents any new evidence append.
6. Atomically inserts both the receipt and a single immutable
   `Recorded` audit event and commits. The F-051 API returns the receipt
   **only after** the configured production journal succeeds.

The original F-051 service's constructor retains an optional journal
dependency to keep isolated domain mocks usable; production DI
**always registers the real PostgreSQL journal**.

## Trusted readback into F-076

The new internal
`GitHubRemoteTrustedDispatchReadOnlyOrchestrator.ReviewFromJournalAsync`
accepts **only preparation/plan/scope IDs, the F-055 hazard ID and
independent actor ID**. No historical receipt, SHA, raw refs, token, URL,
or Git command is accepted.

The real journal's `GetForHazardAsync` looks up the F-055 hazard and
its matching F-052 grant, requires their read-only states and lineage,
then retrieves the exact immutable F-051 record whose
`evidence_sha256` equals **F-052's reviewed `preflight_sha256`**.
It rehashes the persisted receipt and verifies its unique recorded
event. Missing, altered or ambiguous evidence fails closed.

F-076 then uses the existing F-044 server-side source regeneration,
F-072/F-073 live authenticated GitHub GET, and F-075 PostgreSQL
two-lock authority verification. A lock acquired by F-075 after these
read-only observations is still necessary: earlier reads never
authorize transport.

## Tests

Real PostgreSQL 17 tests cover:

- Two distinct proposal/review proofs in the same F-050 epoch, each
  individually addressable by evidence SHA and each with one immutable
  audit event.
- Six concurrent replicas replaying one proof: exactly one evidence
  row and one `Recorded` event, no new operation.
- Forged payload with a previously recorded proof SHA and a mutated
  candidate count is rejected.
- F-050 recovery before appending prevents any new receipt.
- Direct SQL UPDATE and DELETE attacks against both tables (including
  trying to set an execution flag) are blocked by database triggers.
- The F-055 hazard selects **only** the F-052 reviewed SHA, not the
  proposal's older preflight. A `MayHaveTransmitted` hazard refuses
  new trusted readback without deleting history.
- Unit tests independently compare the historical SHA rehash against
  the real F-051 integrity sealing implementation, including timestamp
  changes and invalid witnesses.
- F-076 contract tests verify successful hazard-bound retrieval and
  missing/corrupted journal evidence denied **before signing-key access**.

## Limits, deployment and next actions

This journal preserves the **complete F-051 receipt and the policy
timestamp needed to reproduce its SHA-256**, not a replayable raw GitHub
HTTP transcript, complete original policy response or full raw typed
ref set. It provides durable, append-only provenance from the
authenticated F-051 writer; it does NOT independently prove GitHub
still has the same refs or grant a sender new permissions.

It is not an execution record, server-side GitHub ref reservation,
conditional push proof, migration-complete flag, auto-retry authorization,
or transport adapter. F-076 remains **unregistered in production DI**
and the existing F-075 read-only verifier continues to require
independent PostgreSQL lease/fence/hazard checks. A future test-org
sender still needs separate explicit write authorization and
destination-side non-destructive operations, followed by actual Git
object readback and reconciliation.

F-077 is stacked on F-076 PR #77; `main` remains untouched.
CI: .NET10/xUnit, PostgreSQL17/real-Git, React/Playwright and Terraform.
