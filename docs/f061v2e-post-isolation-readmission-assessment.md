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
