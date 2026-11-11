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
