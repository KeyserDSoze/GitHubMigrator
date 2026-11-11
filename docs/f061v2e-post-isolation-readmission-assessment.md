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
