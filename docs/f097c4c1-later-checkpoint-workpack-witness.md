# F-097c.4c.1 — Source-locked workpack witness, without target write authority

Tracks [#104](https://github.com/KeyserDSoze/GitHubMigrator/issues/104)
under [#103](https://github.com/KeyserDSoze/GitHubMigrator/issues/103)
and [#100](https://github.com/KeyserDSoze/GitHubMigrator/issues/100).
The verified checkpoint-scoped reviewed-target dependency is
[#105](https://github.com/KeyserDSoze/GitHubMigrator/issues/105).

## Implemented

- `GitIncrementalCheckpointWorkpackIntegrity` produces a **new v1
  SHA-256 domain** from F-029 baseline plan identity, the exact later
  F-091 checkpoint, F-097b full ref/OID evidence SHA and F-014 root,
  the current F-031 target observation, F-032 reconciliation ID/SHA,
  F-038 workpack ID, actor and four permanent `false` execution flags.
- The disabled-by-default internal PostgreSQL journal re-fetches
  independent F-091/F-097b/F-031 proof **before** the write transaction.
  Under the same source advisory lock used by staging/F-029/F-090/F-097,
  it checks the plan, newest checkpoint, persisted immutable source
  proof, latest target observation/actor and exact SHA identity again.
  It atomically creates a F-032 comparison, **WriteBlocked** F-038
  workpack/event and append-only versioned workpack receipt/event.
  Exact same-actor replay compares full stored proof and F-032 item
  semantics; arbitrary ref names/OIDs, local paths and remote GitHub
  addresses are never taken from callers.
- The additive PostgreSQL schema has SHA/false-flag checks and mutation-
  reject triggers, including for any existing EF-created table.
  Historical F-032/F-038 baseline receipts are not changed, and F-035
  source bundle acquisition is explicitly **not implemented** here.

## Crucial current blocker (real two-epoch integration)

A real, second completed F-014 Git ref discovery and F-091/F-097b
source checkpoint can be verified with SHA-1 and SHA-256. But the
**existing F-029 plan must become non-current** when F-025 applies a
later snapshot: `PostgresIncrementalTargetPreflightRepository.GetAsync`
requires its original F-029 snapshot to remain the latest staging sync.
Existing `PostgresGitHubTargetRefEvidenceStore.SaveAsync` therefore
correctly refuses a *new* F-031 target observation under that stale
baseline plan. This invalidates F-097c.2's baseline-scoped preflight
for real later epochs.

It would be unsafe to force `IsCurrent = true`, reuse an older F-031
observation, or infer Git OIDs from the F-091 fingerprint delta.
**Issue #105** requires a separately versioned, source-locked
checkpoint-scoped reviewed plan and read-only target observation;
only then may this journal's **disabled** later-epoch capture be
admitted and positively tested. Existing baseline current/expiry and
write barriers must not be weakened.

This is a non-executable internal building block; it is NOT a working
end-to-end later checkpoint workpack. Subsequent F-035/F-060/F-061
integration remains #103/#100, and F-095 v3 ancestry remains #96.

## Verification

The SHA-1/SHA-256 real PostgreSQL test intentionally verifies that a
later checkpoint **fails closed** under stale F-029 target review and
creates zero F-032/F-038 workpack rows or F-097c4c1 receipts. Separate
v1 digest/unit tests verify deterministic seals, cross-snapshot
refusal, tamper rejection and false write flags. A positive, truly
later-epoch PostgreSQL readback and concurrency test is **not possible
until #105 is completed**. The issue checklist must remain OPEN until
that path is safe and verified.
