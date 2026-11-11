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

Close #126 only after all four CI jobs, including PostgreSQL 17 integration, succeed on the final `main` commit. The concurrent #123/#124 isolation work must also pass its own acceptance checks.
