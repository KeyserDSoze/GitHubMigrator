# F-078 — immutable, write-disabled GitHub sandbox review sessions

## Motivation

F-076 reconstructs trusted F-044 source evidence, obtains a fresh F-072/F-073
GitHub App **GET-only** observation, reconstructs F-044 a second time, and
delegates to F-075 to validate five PostgreSQL authority chains under shared
advisory locks. F-077 stores the complete **F-051 preflight receipt** plus
its policy timestamp as immutable historical evidence, selected by F-052's
exact reviewed SHA via the F-055 hazard.

F-078 creates an append-only audit trail for a **single read-only sandbox
review session** per F-055 possible-send hazard. It answers what was
reviewed, when it was reviewed, and whether the former review has since
become historical; it cannot schedule a sender or authorize GitHub writes.

## Trusted entrypoint

The existing internal
`GitHubRemoteTrustedDispatchReadOnlyOrchestrator` now has
`ReviewAndRecordSessionFromJournalAsync(preparationId, planId, scopeId,
hazardId, independentActor, ct)`. It accepts **only five UUIDs** and no
ref names/OIDs, GitHub URLs, bearer credentials, packet or prior preflight.

The method:
1. Retrieves the exact historical F-051 receipt from the immutable F-077
   journal via F-055 hazard and the F-052 reviewed proof SHA.
2. Rebuilds F-044 from PostgreSQL source/workpack/ref journal and rechecks
   the Git bundle on disk.
3. Uses the F-072/F-073 authenticated one-repository GitHub App reader to
   GET entire typed heads/tags (including annotated tag-object OIDs) and
   candidate protections with final repository identity reread.
4. Reconstructs F-044 **again after the network calls**, rejecting source
   changes, then calls F-075's PostgreSQL-locked authority verifier.
5. Only on success, passes the F-074 **ReviewedWriteDisabled** digest to
   F-078's PostgreSQL journal. The journal independently re-acquires
   both repository lock families and reevaluates database authority
   immediately before its INSERT. A state change between F-075 review
   and F-078 recording is refused.

The existing in-memory F-074 review is not an execution token or an
independently authenticated remote observation. F-078 is an audit
*downstream* of the trusted F-076 entrypoint. Its `RecordAsync` checks
persisted lineage but does **not** independently rederive the entire
F-074 digest from an arbitrary untrusted DTO. It is not exposed via API
or production DI, and its stored receipts never become write-capability
tokens.

## PostgreSQL transactions and immutable schema

New additive tables are initialized after F-055 and F-077:

- `github_remote_sandbox_review_sessions` keyed by unpredictable UUID
  and uniquely constrained by **F-055 hazard_id** (one session per
  possible-send epoch), with grant/fence/repository/actor identifiers,
  F-044 packet/candidate hashes, exact reviewed F-051 SHA, target baseline
  and last ref observation SHA, F-074 review SHA, DB record time,
  bounded expiry and versioned F-078 SHA-256 digest.
- `github_remote_sandbox_review_session_events` keyed by session UUID
  with a **single** immutable `Recorded` audit event per session.

PostgreSQL rejects `UPDATE` or `DELETE` on either table via triggers
and enforces hash format, positive epoch, recognized state and
**four permanent false flags** in CHECK constraints:
`transport_invoked`, `remote_writes_authorized`,
`may_automatically_retry`, `migration_complete`.

The recorder takes F-045's **732911** and F-050/F-052/F-055's **733050**
numeric-repository PostgreSQL transaction advisory locks, in that order.
It samples database `clock_timestamp()` **after** both locks, then
verifies the real F-045 lease, F-047 blocked attempt, F-050 reviewed
fence, F-052 reviewed grant, F-055 unmarked hazard, F-052/F-055
append-only events and the exact historical F-077 preflight row and
its Recorded audit. Source, repository, fence generation, policy,
packet, candidate, baseline, actor independence and time windows must
match the F-074 receipt. The session expires no later than the earliest
F-074 deadline, F-045 lease, F-050 fence, F-052 grant or F-055 hazard.

Six concurrent replicas creating the same session serialize under these
locks: the **first writes one row + event**; exact replay returns that
immutable row; a different actor or ref/dispatch proof for the same
hazard refuses with conflict. A previous `MayHaveTransmitted`,
`OperatorReviewRequired`, a cancelled grant, superseded lease, or
recovered fence always forbids a new session.

## Historical-only inspection

`InspectAsync(sessionId, numericRepositoryId)` is read-only and
verifies the stored F-078 SHA-256 and exactly one immutable audit event,
then checks live F-045/F-047/F-050/F-052/F-055 control-plane state.

It displays `ReadOnlyReviewCurrent` only while all the read-only
control-plane states remain consistent and the very short deadline
has not expired. Otherwise the immutable session remains visible as
`HistoricalOnly`, even when F-055 was marked `MayHaveTransmitted`.
**Both** statuses always return `CanExecute=false`,
`CanRetry=false` and `MigrationComplete=false`.

Historical-only inspection is a **display status**, not a lock or an
authorization. Another PostgreSQL actor or GitHub user can change
target state immediately afterwards. No credentials, refspec, raw
target refs, GitHub API write, Git push, destination repo creation,
migration-complete flag or automatic retry mechanism is introduced.

## Automated tests

- Real PostgreSQL 17 full F-075 read-only validation followed by
  F-077 immutable receipt recording and F-078 session append and
  audit inspection.
- Six concurrent PostgreSQL connections racing to record the same
  session result in exactly one immutable row and event.
- Reused hazard ID with different actor, review SHA or target-ref SHA
  is rejected; no alternate session or event can be written.
- F-055 marking `MayHaveTransmitted` makes an earlier session
  `HistoricalOnly`, blocks all new recording and **never** allows retry.
- Missing F-077 preflight evidence blocks session creation.
- SQL-level tamper attempts (writing `remote_writes_authorized=true`,
  changing review SHA, deleting the row or deleting its event) are
  rejected by PostgreSQL immutable triggers.
- A second PostgreSQL worker holds the F-055 repository lock, commits a
  possible-send marker, then lets a waiting session recorder proceed:
  the recorder sees the committed marker and refuses.
- Domain orchestrator tests prove F-078 is called **only after**
  the full F-076 trusted review, and missing journal dependency
  fails closed before GitHub network or secret access.

## Deliberate remaining boundaries

F-078 does **not** implement a GitHub remote sender, compare-and-create,
write-enabled lease, execution outbox, target-side atomic ref operation,
remote object readback proof or migration-complete state. F-071 already
demonstrated a separate, opt-in **local Git bare repository** transfer,
but that evidence cannot be reclassified as a successful remote GitHub
migration.

F-078 is internal and unregistered in production DI/API. A future actual
sandbox sender will require a separately reviewed, narrowly scoped,
explicitly enabled authorization protocol and destination-side
non-destructive semantics, with F-055's pessimistic
no-auto-retry-after-uncertain-send rule preserved.

No existing `main` commits are changed: F-078 is stacked on F-077 PR #78.
