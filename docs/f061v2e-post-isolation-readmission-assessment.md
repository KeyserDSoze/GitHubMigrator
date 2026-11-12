# F-061v2e.1: read-only post-isolation re-admission assessment (#126)

**Status: domain-only non-authorizing assessment implemented on `main`; latest CI pending.** Parent [#121](https://github.com/KeyserDSoze/GitHubMigrator/issues/121) remains open. This is a *candidate for independent operator review*, not an authorization to recover a mirror.

## Inputs and safety boundary

`GitIncrementalLaterCheckpointMirrorReadmissionAssessmentIntegrity.Seal` compares four pre-existing, independently constructed pieces of evidence:

1. A bounded F-060v2 **later** source checkpoint staging plan (F-091/F-097b/F-031/F-038v2) plus the matching source-only F-035v2 durable bundle.
2. The immutable terminal F-061v2 `Isolated` receipt produced by the separately gated #123/#124 source-locked physical/SQL coordinator, with its original failed worker/token/generation, exact decision and `Prepared` hashes, deterministic quarantine filename, bundle SHA/length and verified Git closure.
3. A **new** read-only `GitIncrementalLaterCheckpointMirrorAdmissionCandidate`, which must be constructed by the actual source/target/physical-bundle preflight in any future trusted caller. Its read timestamp must be no earlier than the terminal isolation receipt.
4. A UTC assessment time within two minutes of the source read.

The SHA-256 domain-separated assessment binds the source connection's immutable checkpoint identity, source repository, full-ref proof, ref set, F-031 target receipt, workpack, exact F-035v2 bundle, original F-061 worker/token/generation, recorded terminal isolation receipt and quarantine/object closure. It checks that the *suggested* generation is `originalGeneration + 1` within [2,2000].

The assessment is **not** a claim of current database source state, a new double-fsck, an authenticated approval, or a lease. A caller-supplied `sourceRead`, `Isolated` receipt or matching SHA is not sufficient authority: the future protected PostgreSQL coordinator must independently reconstruct and verify all current facts under SOURCE→PLAN advisory locks, recheck quarantine with the shared F-060 POSIX lock, and require an entirely **new, independent human reviewer decision**.

No new worker/token is created. The existing `RecoveryRequired` row is never edited. `DurableReAdmissionApproved=false`, `CanReAdmit=false`, `CanMaterialize=false`, `CanPush=false`, `CanRetry=false`, `CanDispatch=false` and `MigrationComplete=false` are fixed by the versioned digest and verified on re-read.

**No HTTP endpoint, background worker, importer, GitHub network request, PostgreSQL migration or write is registered by #126.** Existing feature flags for the source/physical/SQL paths remain disabled by default.

## Regression coverage

Real SHA-1 and SHA-256 Git integration fixtures exercise seal/verify after a physically isolated original mirror and separately constructed domain terminal receipt. Negative checks reject changed proof hashes, forbidden permissions, a mismatching bundle, generation overflow, bad review/intent scope, stale source read and an expired assessment interval.

This domain contract is *not* sufficient to close #121. The next slices require a new reviewed, authenticated re-admission decision, additive immutable generation history, source-first lock/CAS, POSIX-locked quarantine verification and separate physical import/Verified lifecycle evidence. F-095v3 ancestry and remote GitHub writes remain unrelated and unauthorized.

## #127: additive PostgreSQL assessment history (still not an F-061 lease)

A separately scoped and **non-authorizing** database foundation is now
bootstrapped after the immutable #123/#124 isolation receipt schema:
`git_later_checkpoint_mirror_generation_assessments` and its
`git_later_checkpoint_mirror_generation_assessment_events` journal.

The new audit-only tables are FK-linked to the exact terminal
`Isolated` receipt and F-035v2 workpack. The PostgreSQL insertion
trigger checks the **original** F-061v2 `RecoveryRequired` lease,
original worker/token/generation, checkpoint/snapshot, full source ref
proof, F-031 target receipt, bundle SHA/bytes and recorded isolation
receipt SHA. The bounded suggested next generation must equal the
original generation plus one. SQL CHECK constraints permanently keep
`durable_readmission_approved` and **all operation permissions FALSE**.
Both the assessment and single `Assessed` event refuse
UPDATE/DELETE, duplicate replay and foreign event SHA/timestamps.
There is no second reviewer, new lease row, new worker/token, physical
restoration or destination GitHub write path.

The schema is additive and initialized idempotently; it does not relax
the existing F-061v2 terminal lease trigger. Actual PostgreSQL17
SHA1/SHA256 tests insert a truthful audit-only assessment after the
trusted source-first physical isolation, verify exactly one immutable
event and explicitly reject UPDATE/DELETE while the original lease
remains `RecoveryRequired`. Separate bootstrap checks refuse forged
raw SQL before a real isolation receipt exists.

This is **NOT the authorizing append-only generation ledger**. Future
#121 work must require a new independent authenticated human decision
and a source-first, POSIX-root-locked single-winning CAS before a
new distinct generation/worker/token may be created. No such writer
is implemented. See [#127](https://github.com/KeyserDSoze/GitHubMigrator/issues/127).

Close #126/#127 only after all four CI jobs, including PostgreSQL 17
integration, succeed on the final `main` commit. The concurrent
#123/#124 isolation work must also pass its own acceptance checks.


## #129: second independent review candidate (domain-only first slice)

The next recovery review is **separate** from #122's original
\`ApprovedForIsolationReview\`. The versioned
\`GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity\` candidate
binds the original reviewer, a **different** proposed second reviewer,
the original failed worker/token/generation, exact immutable F-061
\`Isolated\` receipt, #126 fresh source assessment, source refs/target
receipt, bundle and quarantine object closure. Both
\`ApprovedForIndependentReview\` and \`RejectedForIndependentReview\`
are **non-authorizing** judgments; all durable-review/admission/import/
push/retry/dispatch/migration flags remain \`false\`.
SHA1/SHA256 PostgreSQL-backed real Git fixture checks that another
reviewer cannot reuse the first reviewer's identity or the original
bundle actor/worker, and refuses stale, tampered or permission-elevated
evidence.

**No authentication, SQL persistence, POSIX re-audit, generation lease,
worker, new importer, HTTP endpoint, GitHub push or executable approval
was introduced by this first slice.** The future #129 default-OFF
writer must authenticate the actor and recheck immutable source,
isolation and current physical evidence under SQL+POSIX locks; a
caller-provided review digest or GUID is never sufficient authority.
#121 still owns any separately approved new-generation CAS.


## #129: PostgreSQL second-review schema foundation (no writer)

The API bootstraps two additive tables after #127 and before the F-061 regex repair: `git_later_checkpoint_mirror_second_reviews` and `git_later_checkpoint_mirror_second_review_events`. They have immutable F-061 FK lineage, a strict distinct-second-reviewer constraint, source/target/isolation/assessment SHA-256 checks, bounded next-generation hint and persistent **FALSE** write and completion flags. **Both INSERT and UPDATE/DELETE are rejected unconditionally by PostgreSQL triggers**, including for arbitrary SQL with guessed reviewer identities or self-sealed evidence. This is deliberately more restrictive than a read-only candidate; there is **no authenticated writer yet**. The SHA1/SHA256 PostgreSQL integration fixture verifies the schema, trigger installation and direct-SQL denial. A future independent JWT-authenticated, source-first SQL and POSIX-audited implementation must explicitly replace this deny-all fence only after satisfying #129. Never treat an existing schema or review candidate as an approved F-061 lease or generation.


### #129: source-first post-Isolated read-only preflight

`PostgresGitIncrementalLaterCheckpointMirrorPreparedReconciliationReader.ReadCompletedAsync` now shares the existing live F-091/F-097b/F-031/F-035v2, original lease and immutable Reviewed+Prepared SQL checks, but requires **exactly one** recorded terminal `Isolated` receipt joined to its immutable event and the original failed worker/token/generation. The original `ReadAsync` still refuses a completed receipt; a retry cannot convert review into a second filesystem move. The SHA1/SHA256 physical-quarantine integration fixture combines this NEW source-first read with the EXISTING POSIX-locked double-fsck `InspectPreparedPathsAsync` and rejects foreign repository scope. **This is an internal read-only component, not an authenticated review endpoint; the source read and POSIX audit are not one authorizing transaction.** #129 still needs fresh simultaneous fencing, reviewer JWT, review SQL writer, CAS/replay/three-replica tests. No remote sends, new lease, generation, or successful migration claim.

### #129 post-Isolated read-only SQL/POSIX bridge (no authorization)

The separately default-OFF `PostgresGitIncrementalLaterCheckpointMirrorPostIsolationInspector`
requires `GitIncrementalLaterCheckpointMirrorPostIsolation:Enabled=true`
and a separately enabled **physical** adapter at
`GitIncrementalLaterCheckpointMirrorPostIsolation:PhysicalEnabled=true`.
It reconstructs the immutable SQL `Isolated` receipt SHA and first
review decision from trusted rows, checks complete source/target history,
and surrounds the real POSIX-root-locked double Git fsck / Prepared
marker / bundle proof with TWO source-first SQL reads. A changed receipt,
source refs, previous reviewer, lease event chain or missing quarantine
refuses the diagnostic. The local physical interface exposes ONLY
`InspectPreparedPathsAsync`, never rename or SQL mutations.

Real SHA1 and SHA256 Git/PostgreSQL fixture tests cover positive
post-isolation read, disabled option, foreign repository and deliberate
tampering with the committed Prepared marker, followed by restoration.
The original `RecoveryRequired` lease remains terminal; the original
source and quarantine paths are not restored or adopted.

**Important limitation:** the physical lock is released before the
second SQL read. This is only a non-authorizing observation that can
become stale; it is NOT a simultaneous SQL/POSIX CAS and does NOT prove
that the actor is an independently authenticated superadmin reviewer.
No route, JWT review decision writer, new generation, push, retry or
dispatch is present. These remain separately OPEN in #129 and #121.


### #129 authenticated second-review candidate (read-only HTTP)

The `GET .../mirror-recovery/second-review/candidate` endpoint shares the existing
`PasswordChanged` + `superadmin` JWT authorization and rate limiting. It
calls only the separately default-OFF post-Isolated SQL/POSIX inspector,
then refuses a reviewer matching the original recovery reviewer, original
F-035 bundle actor or failed worker using **server-read identities**, not
a client-supplied reviewer GUID. It returns a diagnostic subset without
the opaque original lease token, any local filesystem path or a grant to
readmit, retry, push, dispatch or mark migration complete. There is **no
POST** counterpart or SQL second-review writer, and the audit itself
remains disabled unless both server-side inspection switches are enabled.
Integration HTTP tests cover anonymous/user/admin denials, default-OFF
superadmin denial, GET-only semantics, and no provider transport. A
successful authenticated candidate remains non-authorizing and can go
stale immediately; completing #129 still requires SQL+POSIX-fenced
persisted independent human decision, replay/race guards, and real
Git/PostgreSQL security acceptance.


### #129: durable #127 Assessed prerequisite for second-review GET

The authenticated `GET .../mirror-recovery/second-review/candidate`
now calls `PostIsolationInspector.ReadAssessedAsync`, not the less
restrictive general physical diagnostic. After live F-091/F-097/F-031
source-first PostgreSQL reads and the double-fsck POSIX quarantine
inspection, it requires a **durable #127 generation-assessment row and its
matching immutable `Assessed` event**. Their original F-061 failed
worker/token/generation, F-035 bundle, original source refs, target
receipt, Prepared/Isolated SHA, object closure and bounded future-generation
hint must all match the current source. The domain-specific #126 digest is
reconstructed from the trusted SQL timestamps and full current historical
source/physical receipts. An absent, mismatched or expired (two-minute)
assessment is refused. The non-authorizing candidate response includes
`assessmentEvidenceSha256`, `suggestedNextGeneration` (hint only) and
review times, **never** a new lease/permission.

The SHA1/SHA256 real-Git PostgreSQL fixture verifies that a fully isolated
mirror without a completed `Assessed` audit event is refused, then accepts
an exact row/event whose digest re-seals against real evidence. The
source+POSIX+SQL observations are STILL NOT one atomic CAS and do not
prove bearer anti-replay or create an authenticated second-review event.
The two #129 deny-all SQL review triggers remain installed. A future
writer must independently lock and recheck the source, latest assessment,
JWT/reviewer identity and POSIX path **within its own fenced transaction**.


### #129: live identity revocation on second-review candidate

In addition to the JWT `PasswordChanged` and `superadmin` middleware,
the **read-only** second-review candidate checks its actor against the
*current ASP.NET Identity account* **before and after** the SQL/POSIX
inspection: current superadmin role, no mandatory password change or
lockout, unchanged security stamp and at most five minutes since the
login that minted its review session ID. Tokens minted before this
feature are deliberately unsuitable for this sensitive candidate.
Revoking the role or rotating the security stamp immediately invalidates
an otherwise unexpired token for this route. TestServer tests cover
fresh reviewer token, rotated-stamp rejection and revoked-role rejection,
without contacting any provider. This is **NOT one-shot JWT replay
protection**: the per-login session ID can be reused for read-only GET.
A future durable second-review writer must atomically consume a unique
session/decision nonce under its own source+POSIX+PostgreSQL lock and
revalidate the actor in the same decision path; none of that authority
is enabled here.


### #129: POSIX lock remains held through the final SQL assessment read

The second-review candidate now calls the new read-only
`WithVerifiedQuarantineAsync` physical interface instead of relying on
a POSIX audit that releases its lock before the final SQL check. The
source-first SQL history is read **before** acquiring the filesystem
lock; under the shared F-060 POSIX process/root lock the physical layer
validates exact quarantine-only paths and a matching durable Prepared
marker, SHA1/SHA256 object/ref closure with two complete Git fsck passes,
and immutable bundle bytes. Only then is the callback invoked to
**re-read source-first PostgreSQL history, validate the immutable Isolated
receipt and #127 Assessed row/event, and re-seal the original domain SHA
while the POSIX lock remains held**. No SQL transaction spans a Git
subprocess, no original path is renamed, no lease/worker/generation is
claimed, and the callback performs no mutation. Direct physical tests
verify that a forged marker prevents callback execution altogether.

This closes a cooperating-process TOCTOU gap for read-only assessment,
but **is not yet a durable second-review decision/CAS** and does not
prevent arbitrary hostile filesystem writers from bypassing our locks.
#129 still needs independent one-time human review authorization and
append-only guarded SQL, with three-replica race/nonce tests. The original
F-061 `RecoveryRequired` lease is terminal and never modified here.

### #129 live reviewer-scoped approve/reject SHA preview

The separately default-OFF superadmin review-candidate GET can now
return two distinct, ephemeral approved/rejected evidence SHA-256
digests and their reviewed-at timestamp. These use the existing
versioned SecondReviewIntegrity.Seal, with the live authenticated
reviewer rather than a user-supplied actor GUID. The source-first
complete F-091/F-097/F-031/F-035v2 history, original F-061
RecoveryRequired lease/decision, #124 quarantine receipts and
#127 Assessed row/event are reconstructed while the shared F-060
POSIX root lock remains held after two actual Git fsck/OID audits.

The proofs are NOT one-time bearer capabilities, NOT SQL-persisted
human decisions, NOT authorization for a generation, import or push.
The #129 review/event SQL tables remain deny-all. A future writer
must independently enforce a one-shot authenticated JWT session and
source-first SQL+POSIX CAS at write time, with three-replica tests.
Existing real SHA1/SHA256 PostgreSQL/Git fixtures assert distinct
SHA digests while all permission/persistence flags remain false.

### #129 no-wait PostgreSQL locks inside the POSIX read-only audit

The post-Isolated candidate checks source history before the F-060
physical lock using its normal source-first advisory lock order.
After dual Git fsck under the F-060 POSIX lock, its final PostgreSQL
revalidation switches to a dedicated `failIfSourceBusy` path.
That path uses `pg_try_advisory_xact_lock` for source then plan in
`AdmissionPreflight` and `PreparedReconciliationReader`. A lock held
by a concurrent source-first writer is refused immediately with
`LaterCheckpointMirrorPostIsolationSourceBusy`; any partially
acquired SQL advisory locks are released by transaction rollback.

This is critical because waiting for source/plan SQL advisory locks
**while already holding POSIX root** would invert the writers'
source-first -> POSIX lock order and could deadlock. Existing writers
and pre-POSIX reads retain their original blocking source-first
semantics. The new alternate locking behavior is accessible only
via an explicit read-only call flag and must NEVER be used as
authorization for a new worker/lease or second-review SQL writer.
Real PostgreSQL17 tests acquire the source lock on a separate
connection and assert immediate fail-closed behavior with a
bounded deadline. The full Git review remains non-authorizing.

### #129 compound event-to-review foreign key

The PostgreSQL second-review `events` table previously had a FK on
`mirror_id` alone. A future writer could, without other safeguards,
associate an event with the correct mirror but a **foreign evidence
digest, reviewer, approval/rejection decision or timestamp**. The
bootstrap now installs an idempotent composite UNIQUE index on the
`second_reviews` row and an FK on FIVE event fields:
`(mirror_id, evidence_sha256, second_reviewer_id, decision,
occurred_at_utc)` matching `(mirror_id, evidence_sha256,
second_reviewer_id, decision, reviewed_at_utc)` in the reviewed
row. The FK prevents detached or mismatched audit events even if
the future protected writer is subsequently activated. Existing
deny-all mutation triggers are **not altered or relaxed**, and
the extra constraint never grants a lease, generation or push.

Real PostgreSQL17 integration checks the database-installed exact
FK definition and unique index, in addition to the existing two
DENY-ALL triggers, event immutability and rejected inserts.

### #129 FK-bound second reviewer to the exact immutable generation assessment

The second-review row now has a composite 13-column FK to the
**exact #127 generation assessment**, not just the same `mirror_id`.
It binds the workpack/checkpoint/source snapshot, Assessed digest,
original terminal F-061 worker/lease token/generation, proposed
generation hint, source staging SHA, F-031 target receipt SHA,
original reviewed recovery decision SHA and actual #124 isolation
receipt SHA. The parent assessment gets a corresponding idempotent
unique index. An otherwise well-formed review with a foreign
assessment digest or swapped failed worker/token/source/target
cannot satisfy this database relationship. The audit **event** also
keeps its separate exact five-field FK to the review row.

This is additive, idempotent and **non-authorizing**. The two
second-review SQL triggers still unconditionally deny INSERT,
UPDATE and DELETE; there is still NO authenticated second review
writer, one-use nonce, new generation, import or push.
Real SHA1/SHA256 Git/PostgreSQL17 tests check the installed
13-column FK, its parent relation, column mappings and valid
unique index in addition to the existing deny-all guards.

### #129 future one-use reviewer session consumption: DENY-ALL foundation

A distinct login minted by `JwtIssuer` carries the signed
`f061_review_session_id`, `f061_review_issued_at` and current
security-stamp witness. The preflight now returns an internal
`GitIncrementalLaterCheckpointMirrorReviewerSession` with
actor ID, session ID and issuance time after checking the
**current ASP.NET Identity account**; the GET revalidates the exact
same session after physical inspection. Neither session ID nor
stamp is exposed in the GET's JSON response.

The additive `git_later_checkpoint_mirror_second_review_session_consumptions`
table establishes a GLOBAL session-ID primary key (a login cannot
commit two reviews), UNIQUE mirror ID (one second decision per mirror),
and UNIQUE server-side decision nonce. A five-column FK binds
mirror, review SHA, reviewer, decision and review timestamp to
the exact immutable second-review row. SQL CHECKs require
issued <= reviewed <= consumed < issued + 5 minutes, reviewed
within two minutes of consumption, and permanently false
re-admit/materialize/push/retry/dispatch/completion flags.

The table's new trigger **unconditionally denies ALL INSERT,
UPDATE and DELETE**, reusing the existing review deny function.
The original review and event tables remain DENY-ALL too. No
nonce is consumed, no decision recorded and no writer, lease or
remote action is enabled in this slice. A future POST writer must
atomically write review + immutable event + one-use session
consumption under the current source/target and POSIX locks,
then prove three-replica single-winner and restart/replay behavior.
The real SHA1/SHA256 PG17/Git test checks installed PK/UNIQUE
and exact FK, rejects raw SQL session INSERT, and verifies all
three tables remain empty. No additional EF raw SQL regexes
were introduced in this bootstrap.

### #129 read-only POST /second-review/preflight (no durable approval)

The authenticated superadmin can submit a proposed decision for
**read-only verification**, using the actor-bound digest/timestamp
returned by the separate default-OFF GET candidate, the exact
`Assessed` SHA, decision enum and a nonzero fresh nonce.
The new `POST .../mirror-recovery/second-review/preflight` route
revalidates the CURRENT Identity-checked login session, then reruns
all source-first PostgreSQL / F-060 dual-fsck POSIX quarantine checks.
The model re-seals approve and reject SHA over the requested
**original UTC millisecond timestamp**, subject to a 2-minute
freshness window and original assessed event provenance.
The server compares submitted digest and assessment SHA with
the newly sealed SHA in constant time, rejecting changed scope.

While POSIX is held, it reads the three-field consumption keys
from PostgreSQL and refuses if a previous review session, nonce
or mirror has been consumed. This is a READ-ONLY snapshot,
**not an atomic replay guarantee**. A future writer must enforce
the same checks while atomically inserting review+event+session
under the correct source-first SQL/POSIX locking protocol.
Even on successful preflight, response explicitly states
`durableSecondReviewPersisted=false`, `sessionConsumed=false`,
and every operational authorization flag false. No F-061
lease/generation/remote authority or SQL mutation is enabled.
The ordinary POST /decision remains the separate FIRST review.

Real SHA1/SHA256 Git/PostgreSQL17 fixtures assert same signed digest
when a fixed GET timestamp is replayed into this read-only
verification, reject expired timestamp, and preserve all flags
false. JWT TestServer tests cover 401/403/409 default-disabled
preflight and verify no provider transport is called.

### #129: complete source-ref and physical object-closure FK on second review

Second-review rows already had a 13-field FK to the immutable
generation assessment for failed worker/token/generation, source
snapshot, F-031 target and first human decision. That link alone
did **not** bind every independent source/Git provenance column
present on both tables. An additive, idempotent **8-field FK**
now additionally binds the same mirror to the exact F-097
full-ref proof SHA, source ref-set SHA, F-035 bundle SHA, Prepared
intent SHA, physical Git object-set SHA and count, and original
source-reviewed UTC timestamp of the #127 Assessed row.

PostgreSQL does not allow a second-review row that merely
names the same mirror and correct lease but substitutes another
valid-looking source refs, bundle, Prepared marker or object graph.
A matching parent UNIQUE index is installed idempotently.
The real SHA1/SHA256 Git/PostgreSQL17 fixture verifies the
8-column FK references the parent assessment, is validated,
and that its unique index is valid; it also checks required
source/physical mapping column names.

All three F-061v2e independent-review SQL tables remain
unconditionally DENY-ALL on INSERT/UPDATE/DELETE. Neither
FK nor index is authorization for a decision, review session
consumption, new generation, lease, filesystem write or push.

### #128 PostgreSQL17 installed CHECK false-positive correction (2026-11-11)

From the canceled [43058337245](https://github.com/KeyserDSoze/GitHubMigrator/actions/runs/43058337245)
PG17 job, the real test `F061v2e_generation_assessment_schema_is_immutable_and_requires_isolation`
failed with `Expected: 0, Actual: 12` while counting malformed
installed constraints. Root cause: `SqlQueryRaw<long>` applies
.NET composite formatting to the literal test SQL:
`position('{{64}}' in pg_get_constraintdef(...))` was rendered to
PostgreSQL as `position('{64}' ...)` and thus incorrectly matched
**correct** 64-character hex CHECK constraints. The C# SQL must use
`position('{{{{64}}}}' ...)`, which EF renders as
`position('{{64}}' ...)`, matching only the **malformed double-brace**
quantifier. This fixes the detector, not the stored schema.

The fail-fast Python SQL guard now verifies that this precise
check is present once and simulates the rendered SQL search.
No lease, receipt, immutable SQL trigger, permission, worker,
Git object, or destination operation is changed.


### #130: authenticated read-only session/nonce intent integrated into preflight

After successful `POST .../second-review/preflight` source-first PostgreSQL and
POSIX-locked two-pass Git inspection, the endpoint now rechecks the **same**
current superadmin Identity session, seals the exact decision preview digest
to the server-side login session ID and request nonce, and rejects a proposed
timestamp outside the five-minute JWT review window. The pure
`GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity`
contract also binds the reviewer and mirror, the canonical UTC issuance,
review and post-audit timestamps and the exact decision (approve or reject).
The result supplies only a `readOnlySessionIntentSha256` and
`readOnlySessionIntentExpiresAtUtc`, with
`durableSessionIntentPersisted=false`, `sessionConsumed=false`,
`durableSecondReviewPersisted=false`, and **all authority flags false**.
The JWT session ID, nonce-derived secret material, original lease token and
local quarantine path are not returned.

The digest is **not a bearer capability, not proof that SQL consumed a nonce,
not PostgreSQL clock authority, not proof of lasting source freshness, and
not permission to perform a write**. The three F-061 second-review tables
retain their unconditional SQL DENY-ALL triggers. #130 still needs a
separately guarded source-first / POSIX / PostgreSQL-clock atomic transaction
for actual review+event+session consumption, direct-SQL forgery rejection,
real SHA-1/SHA-256 integration, restart/replay and three-replica races. The
original terminal `RecoveryRequired` lease, mirror and GitHub destination
are not modified.


### #130 SQL parity: exact session/nonce scope CHECK (non-authorizing)

The second-review session-consumption schema now installs an additive,
validated and idempotent `ck_f061v2e_second_review_session_scope_v2`
CHECK. It rejects an empty mirror UUID, a reused login-session GUID as
the decision nonce, and a malformed or uppercase 64-character SHA-256
review evidence. These restrictions align the future trusted writer
boundary with the standalone read-only #130 domain intent; they are
not a substitute for a fresh server-verified JWT, source-first SQL
and shared F-060 POSIX-locked two-pass Git audit.

The existing three review/event/session SQL tables **still DENY ALL
INSERT/UPDATE/DELETE**; this schema does not enable a writer,
consume a JWT session, re-admit a terminal F-061 lease or perform
GitHub writes. The real PostgreSQL17 SHA-1/SHA-256 two-epoch fixture
checks the installed validated CHECK and its exact SHA quantifier.
The fail-fast EF composite-format manifest now covers **64**
SHA-256 CHECK regexes instead of 63. Actual acceptance continues to
require a final 4/4 main run and a separately protected atomic
decision+event+session transaction with three-replica race testing.


### #130: reject invalid one-use review input before physical inspection

The default-OFF authenticated `POST .../second-review/preflight`
now rejects (HTTP 400) a nonce identical to the current signed
review-session UUID, any noncanonical sub-millisecond UTC review
timestamp, a review in the future, and reviews older than two minutes
or outside the five-minute login-session window **before** invoking
the heavy F-060 POSIX-locked two-pass Git audit. This early check
matches the immutable SQL nonce CHECK and the independent
non-authorizing `SessionIntentIntegrity` bound; successful requests
still recheck all source, target, physical and current Identity
evidence, including freshness, after the expensive audit.

The real API/JWT integration test extracts the session UUID from a
signed test login and verifies same-session nonce, sub-millisecond
and future timestamp attempts each return 400 (not the default-off
physical inspector's 409), with zero provider transport calls.
Nothing persists a review/event/consumption, disables SQL DENY ALL,
changes a `RecoveryRequired` lease or authorizes a GitHub write.
The actual atomic PostgreSQL-clock writer, three-replica races,
SHA-1/SHA-256 physical verification and final four-gate acceptance
are still required.


### #130: audit journal binds future session-intent SHA provenance

The global one-use second-review session-consumption journal now
has `session_intent_evidence_sha256 varchar(64) NOT NULL`, matching
the existing non-authorizing, domain-separated `SessionIntentIntegrity`
SHA. An idempotent additive ALTER installs the column on prior
default-DENY databases, and a separately versioned validated
`ck_f061v2e_second_review_session_intent_sha_v3` CHECK requires
exactly 64 lowercase hex characters. The real PostgreSQL17
SHA-1/SHA-256 fixture asserts the column's NOT NULL contract and
installed CHECK; the early EF composite-format guard covers **65**
SQL SHA regexes rather than 64.

This provides a future audit field, **not** a session consumption or
authorization. A SHA's syntax alone cannot establish authenticated
second-human consent; the eventual writer MUST verify current Identity
and domain digest under the source-first PostgreSQL DB-clock and F-060
POSIX locks and commit review+event+session in one atomic transaction.
All review/event/session tables retain unconditional DENY-ALL triggers.
No lease, generation, physical import or GitHub write is enabled.


### #130: canonical PostgreSQL session timestamp precision

The second-review consumption journal installs an **idempotent, validated**
`ck_f061v2e_second_review_session_precision_v4` CHECK. PostgreSQL now
rejects microsecond-level JWT issuance or expiry timestamps, and
submillisecond review or consumption timestamps: issued/expiry must be
whole seconds, reviewed/consumed whole milliseconds. This matches the
standalone C# `SessionIntentIntegrity` contract and prevents a future
writer from persisting a timestamp that cannot reproduce the
domain-separated session-intent SHA exactly. PostgreSQL's
`timestamptz` normalizes to an instant, so this is **not** an original
timezone-offset attestation and **not** PostgreSQL clock authority.
The real SHA1/SHA256 PG17 integration fixture inspects the installed,
validated CHECK; the 65-expression EF SQL guard remains unchanged.

All three second-review SQL tables retain **unconditional DENY ALL**
mutation triggers. This addition does not enable the second decision
writer, consume any session, adopt a new F-061 generation, modify
terminal leases, import a physical mirror or contact GitHub.
The atomic review+event+session writer and 4/4 main CI remain open.


### #130: real PostgreSQL17 negative CHECK execution without unlocking journal

A separate API/PostgreSQL integration test now clones the INSTALLED
second-review session journal CHECK and NOT NULL constraints into a
transaction-scoped temporary probe table (with its immutable false
defaults), **without** the production foreign keys or mutation triggers.
It verifies one valid canonical session tuple is accepted and that
PostgreSQL itself rejects session-as-nonce replay, zero mirror UUID,
SHA-256 lengths 63/65, uppercase review and intent SHA, subsecond
JWT issuance/expiry and submillisecond review/consumption with
SQLSTATE CHECK violation. SAVEPOINT rollback isolates each invalid
attempt and the entire probe transaction is rolled back afterward.

The same test separately asserts a raw INSERT into the REAL production
session-consumption table still raises the unconditional DENY-ALL trigger.
No real session, second review or event is persisted, and no new
generation/lease or GitHub destination action is enabled. The probe
tests SQL CHECK enforcement, **not** an authenticated atomic writer or
the complete FK/physical replay chain; #130 remains open until the
full writer and its SHA1/SHA256/three-replica race acceptance.


### #130: execute global session/nonce/mirror UNIQUE replay fences in PG17

The real PostgreSQL17 negative-consumption test now clones the **actual
three unique indexes** as well as installed CHECKs/defaults into its
transactional temporary probe. It verifies all three unique indexes are
installed and valid, then commits one canonical tuple to the probe
transaction. Independent attempted replays of the same JWT login
session with a different nonce, the same decision nonce with a
different session, and the same quarantined mirror with both IDs
changed each must fail with PostgreSQL SQLSTATE `23505`; every
constraint failure uses a SAVEPOINT and leaves exactly the original
tuple. Malformed SHA, timestamps and forbidden duplicate IDs continue
to reject with SQLSTATE `23514` as distinct CHECK violations.

The test separately attempts a raw write to the real review-session
journal and requires the unconditional DENY-ALL trigger. This proves
database one-use indexes are operative, **not** that a real authenticated
writer or a three-replica physical Git race exists. No recovery approval,
lease re-admission or GitHub push has been enabled.


### #130: PostgreSQL17 actual varchar-first SQLSTATE correction

The completed [CI 43074185648](https://github.com/KeyserDSoze/GitHubMigrator/actions/runs/43074185648)
exposed a genuine test expectation error: overlong 65-character input
to a `varchar(64)` column is rejected with PostgreSQL `22001`
**before** the 64-character SHA CHECK can run. The read-only probe
test now requires `22001` for overlength while preserving `23514`
for 63-character and uppercase input. This is a *stricter*, actual
database rejection, not a skipped test or a relaxed CHECK.
The same run also revealed two independent five-minute long-lived
SHA1/SHA256 `failAfterImport=true` scenarios with source-provenance
expiry / unexpected error codes; those are tracked separately and
not counted as passing. Final-main 4/4 acceptance remains mandatory.


### #131: deterministic time only in long physical Git fixture

Completed [PG17 CI 43080214985](https://github.com/KeyserDSoze/GitHubMigrator/actions/runs/43080214985)
failed **only two** `F097c4c0c` real SHA-1/SHA-256
`failAfterImport=true` cases, at `PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight`,
where current source-first F-031 target observation/destination validation
correctly expires after five minutes of real fixture execution.
Other three gates and the new #130 SQL CHECK/UNIQUE probe passed.

To maintain the **same** source-first production checks, the test
harness offers a nullable, opt-in `IntegrationClockOverride`; default
is still `TimeProvider.System` and production code is unchanged.
Only `failAfterImport=true` injects a `LongPhysicalFixtureClock`,
which tracks actual UTC until the original durable F-031 target
observation is committed, then clamps the host's observation time to
no later than its **fourth minute** (leaving a margin for older
destination validation). No persisted F-031/F-035/F-061 evidence
is rewritten; there is no new target observation, synthetic review
SHA or change to actual PostgreSQL transaction time. Real Git
SHA1/SHA256 import, post-rename POSIX lock, fsck/ref/OID audits,
immutable SQL and the three-replica race remain active. All
other theory cases, default hosts and explicit stale-source
negative tests still use actual wall time.

This is a **test-only, non-authorizing** clock fixture, not a
production lease extension or a way to allow stale decisions.
It needs full final-main PostgreSQL17 execution before #131
can be accepted.


### #130: authoritative PostgreSQL clock and read-only replay preflight

An additional *read-only* `PostgresGitIncrementalLaterCheckpointMirrorSecondReviewSessionReadiness`
now checks the **actual PostgreSQL server** `date_trunc('milliseconds', clock_timestamp())`
against the signed reviewer login's canonical whole-second issuance,
five-minute exclusive expiry and two-minute review freshness.
It also reads the existing global session-ID, nonce and mirror-ID
consumption keys (which are presently always empty because all review
journals retain unconditional DENY-ALL triggers).

The authenticated `POST .../second-review/preflight` invokes this
read **before** costly source-first/POSIX-locked physical fsck, then
again **after** fresh immutable source and physical verification,
with the server-derived physical mirror ID. The non-authorizing
session-intent preview now uses that second PostgreSQL clock instant
instead of trusting only the API's `TimeProvider`. A real PG17
integration test checks timestamp canonicality, replay scope, expiry,
future and malformed timestamp refusals and zero journal rows.

**This is explicitly not an atomic CAS or a DB writer.** A read can
race against a subsequent write in any multi-replica deployment;
the future approved implementation must authenticate the second
human again, lock the source, recheck full proof/Identity, and record
the second review, matching event and session/nonce/mirror consumption
in ONE DB-clock-governed transaction. No `RecoveryRequired` lease,
new generation, physical import or GitHub write is authorized. The
three mutation triggers remain DENY ALL.


### #128/#131: isolate negative PG17 assertions by forged mirror UUID

Completed [CI 43093654395](https://github.com/KeyserDSoze/GitHubMigrator/actions/runs/43093654395)
revealed a further independent PostgreSQL fixture assertion failure:
`F061v2e_generation_assessment_schema_is_immutable_and_requires_isolation`
expected a globally empty `git_later_checkpoint_mirror_generation_assessments`
table, but the **shared real database** already contained a legitimate
assessment from the real SHA1/SHA256 test. The fake SQL INSERT itself
correctly raised PostgreSQL exception; the unscoped post-assertion
was incorrect. The negative test now uses one fresh
`forgedMirrorId` in both attempted INSERT and post-condition:
**zero rows for exactly that forged mirror**. It does not remove valid
rows, filter or skip a test, alter production SQL, or weaken the
isolation/event uniqueness guards. This fix is independent of
[#131](https://github.com/KeyserDSoze/GitHubMigrator/issues/131):
the two long SHA1/SHA256 `failAfterImport` branches still encounter
real PostgreSQL clock-side 5-minute F-031/lease expiry even when
the TestServer wall-clock was bounded; test fixture segmentation
is still required, with strict production freshness untouched.


### #130: bind database-clock/readiness preflight to explicit PostgreSQL schemas

The read-only session readiness guard queries
`public.git_later_checkpoint_mirror_second_review_session_consumptions`
and calls `pg_catalog.clock_timestamp()` through
`pg_catalog.date_trunc(...)`. Neither the real journal lookup nor
the database clock depends on a connection's mutable `search_path`.
A real PostgreSQL17 integration test starts a transaction, sets
`SET LOCAL search_path TO pg_catalog` (excluding `public`), and
checks that the exact session/nonce/mirror readiness read still
completes using the database clock. The temporary transaction is
rolled back; it does not create, consume or authorize a session.

The strict non-authorizing boundary is unchanged: all three review,
event and one-use consumption tables still DENY ALL mutation;
the future source-first verified, authenticated atomic writer
is not implemented. #131's long SHA1/SHA256 failAfterImport
fixture still needs to complete within the original F-031 real
PostgreSQL-clock freshness interval without weakening runtime checks.


### #131: separate real F-031 epochs for pre-move denial and physical post-isolation

The PG17 regression `F097c4c0c_authenticated_http_review_of_real_later_checkpoint_is_immutable`
previously combined several independent adversarial SQL/restart checks
with the SHA-1 post-rename crash case or SHA-256 three-replica POSIX
isolation race **inside the same five-minute immutable F-031 source
observation window**. Real PostgreSQL correctly rejected the late
`RequireLiveLockedAsync` source re-check; changing only the API test
clock could not affect the database time.

Commit `1bfe472` adds two parameterized **pre-move-only** variants
(one real SHA-1 and one real SHA-256, each provisioning its own real
F-014/F-091/F-097/F-031/F-035 provenance and import-worker failure).
They still execute the original verified Prepared journal, forgery
refusals, restart readback, immutable mutation denials and first-human
replay checks. They stop after proving that no `Isolated` receipt
exists. The original two `failAfterImport=true` variants retain
their independent source observation, actual Git object and marker
checks, source-first SQL lease checks, POSIX-locked isolation,
SHA-1 crash-after-rename recovery, SHA-256 real three-context
one-winner race, post-isolation immutability and second-human
preflight; they no longer rerun the independent pre-move attack
probe sequence in the *same* 5-minute window.

The test suite has **15 parameter rows instead of 13**, preserving
the original 13 scenarios and all negative assertions. Both halves
still use a genuine DB timestamp; the production five-minute TTL,
source-freshness check, F-061 terminal lease and three DENY-ALL
second-review journals are unchanged. This reduces the length of the
source-freshness-critical path but is **not yet proof of a passing
PostgreSQL17 integration run**. Review #131 and the latest main CI
before treating the regression as resolved.


### #131: fail-closed source-busy reader budget includes nested F-031 preflights

The detailed canceled PG17 log showed the SHA-1 test at
`F031-real-age=148.1s` after the pre-move negative checks,
`181.6s` after real isolation and `216.2s` after post-isolation
source audit, before a further **30-second blocked advisory lock**
in an upstream F-031 preflight. The expected source-busy code
was replaced by caller-token cancellation. This is separate from
the finite five-minute database freshness limit.

For the **POSIX-root-held `failIfSourceBusy=true` path only**,
`ReadCompletedAsync` now applies a 12-second linked cancellation
budget to *all* nested source, bundle and admission reads, not just
its own already-nonblocking SQL transaction. Only internal budget
expiry is translated to `LaterCheckpointMirrorPostIsolationSourceBusy`;
caller-requested cancellation is preserved. The real PostgreSQL17
advisory-lock contention test additionally asserts its **30-second
caller timeout has not expired** when the fail-closed refusal returns.
Under contention this may refuse an otherwise legitimate request,
but cannot admit an untrusted mirror, extend F-031 TTL, or allow
a stale second-review writer. The normal completed-reader path is
unchanged. This is a bounded fail-closed behavior, not a claim of
instantaneous nonblocking across independent readers.


### #131: early nonblocking source/plan lock probe under held POSIX root

The prior 12-second linked read budget is a **fail-closed fallback**, not
an efficient way to discover a source lock already held when the caller
holds the F-060 shared POSIX root. `ReadCompletedAsync(failIfSourceBusy:true)`
now looks up the trusted, persisted preparation+plan's
`source_connection_id`, acquires **only** a short PostgreSQL
`READ COMMITTED` transaction, and attempts both source-first and
plan-second `pg_try_advisory_xact_lock` using the existing lease-store
lock keys. Busy source/plan refuses immediately with the existing
`LaterCheckpointMirrorPostIsolationSourceBusy` code. Both locks are
explicitly rolled back before returning to normal preflight.

This preliminary check is **not** a reusable successful proof or a
TOCTOU authorization. The reader still repeats all F-091/F-097b/
F-031/F-035v2 immutable source checks, then its final source-first
locked verification and reread after probe release. If a writer acquires
a lock in that gap, the 12-second source-read budget still guarantees
failure closed (the caller's cancellation remains distinct).
The real PostgreSQL SHA-1/256 adversarial test retains the independent
connection holding the source advisory lock and now also asserts
the refusal completes **under ten seconds**, before the caller's
30-second timeout. No production five-minute provenance TTL, actual
Git fsck, review journal mutation trigger or re-admission lease
permission was modified.


### #131: verify independently reconstructed first-review history, not its new timestamp

Canceled [PG17 CI 43122040258](https://github.com/KeyserDSoze/GitHubMigrator/actions/runs/43122040258)
has archived logs from an actual failing SHA1 `failAfterImport=true`
case. The scenario reached real POSIX isolation with an F-031 proof
age of 145 seconds and successful post-isolation source audit at
178 seconds, then failed inside
`GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity.Seal`
while verifying the *old* first-human review against the **later
restart-reconstructed** `ExpectedInspection`. The old call to
`GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Verify`
re-sealed the original human decision as if its reviewed-at timestamp
belonged to the new physical inspection, and therefore rejected
perfectly matching immutable historical evidence.

The second-review candidate now uses the existing domain
`VerifyForPhysicalIsolation`, which preserves **strict original
inspection verification** when the inspection was the one the
human originally reviewed, and calls
`VerifyHistoricalForIsolationIntent` when the source-locked reader
has a later timestamp with the **same** immutable first reviewer,
signed decision digest, lease token/generation, staging and Git object
graph/first-review candidate SHA. This does not forgive a changed
source, forged first reviewer, new graph or quarantine marker.
The SHA1/SHA256 PG17 scenario now checks that the original and
restart-reconstructed inspection produce the *same* non-authorizing
second review digest, while an impersonated historical reviewer is
rejected. No persisted second decision, lease, new generation,
GitHub push or re-admission is enabled. The SQL immutable journals
still DENY ALL writes.


### #131: separate real advisory-lock contention from slow healthy F-031 source verification

The archived **canceled** [CI 43125230806](https://github.com/KeyserDSoze/GitHubMigrator/actions/runs/43125230806)
had .NET/React/Terraform green, with SHA-1 physical isolation and
successful current-source checks at F-031 proof age 220.7 seconds;
a subsequent expected `LaterCheckpointMirrorSecondReviewAssessmentMissing`
negative assertion was instead rejected with
`LaterCheckpointMirrorPostIsolationSourceBusy`. This is consistent
with the old **12-second internal linked read budget expiring even
when no PostgreSQL source/plan advisory lock is held**: full live
source and physical-proof checks cost much more than a single scalar
SQL lookup.

The `failIfSourceBusy=true` path now retains its **early nonblocking
source+plan advisory-lock probe** (held-lock integration test still
requires a fail-closed response in under ten seconds) but provides a
**40-second total read budget** for legitimate nested F-031/F-035v2
source history, rather than aborting healthy complex reads at 12s.
The budget is still bounded and maps only *internal expiry* to a
fail-closed source-busy error; external caller cancellation remains
distinct. This change cannot bypass a busy source, change real
PostgreSQL F-031 five-minute freshness, open mutation triggers,
create or renew an F-061 lease, or authorize second-human review.
It must be verified with actual latest-main PG17 integration; an
archived canceled run is diagnostic, NOT a green quality gate.


### #131: post-isolation real SHA1/SHA256 checks split into five fresh provenance epochs (2026-11-12)

The completed, **failed** main [CI 43128396672](https://github.com/KeyserDSoze/GitHubMigrator/actions/runs/43128396672) ran 491 real PostgreSQL17 tests: 489 passed, two post-import SHA1/SHA256 restart variants failed after about five minutes. They passed real kernel/POSIX isolation and source checks at F-031 ages of approximately 224s (SHA1) and 246s (SHA256); a subsequent trusted source-first read encountered the original PostgreSQL-clock expiration. This is a **real failed run**, not a canceled validation. The saved F-031 target observation *and* independently validated destination connection both have separate strict five-minute database-clock checks in `RequireLiveLockedAsync`. Freezing an application `TimeProvider` does not move the database clock.

The original 15 theory rows (13 standard plus 2 distinct pre-move adversarial SHA1/SHA256 cases) are retained. The two formerly monolithic post-import rows are expanded into **five SHA1 and five SHA256 fresh independent F-014/F-091/F-097/F-031/F-035 provenance/real-Git epochs**, giving 23 rows total without changing the production five-minute TTL or source/target trust policy:

1. Full post-Isolated source replay, physical double-fsck and marker-forgery refusal, followed by restoration and a genuine fresh inspection.
2. Historical first-human review binding, exact immutable second-review FK/CHECK schema and refusal when a persisted Assessed row has no matching audit event.
3. Real source+POSIX reviewed Assessed positive, independent second reviewer and exact fixed-time preview/session+nonce replay without persistence.
4. Fresh Assessed positive plus stale three-minute review timestamp refusal.
5. Separate physical callback denial on marker forgery, PostgreSQL advisory-lock fail-fast contention, immutable assessment/isolation SQL denials and refusal to reacquire the original `RecoveryRequired` generation.

Each case builds a *new* real source proof and latest target observation, not an altered/extended existing row. All previously asserted real Git SHA1/SHA256 fsck, quarantined source graph, three-replica SHA256 isolation race, historical review SHA, original terminal lease/false authority flags, foreign SQL mutation and replay remain exercised. An early return ends only its designated independent regression slice, after its assertions; it does not mock PostgreSQL time, skip the integration job, skip real Git, or enable any production reviewer writer. The expected additional PG17 test runtime is intentional to avoid letting one test scenario outlive its authoritative five-minute observation window. **A fresh complete 4/4 green run on the resulting main commit, including actual PostgreSQL17, is still required before closing #131/#128/#125 and dependent issues.**
