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

## V2 checkpoint F-031 read-only join (2026-11-09)

After [#109](https://github.com/KeyserDSoze/GitHubMigrator/issues/109)
closed with CI 4/4 on main, [29bcfb6](https://github.com/KeyserDSoze/GitHubMigrator/commit/29bcfb687f858f650ffbd3ab4a5dc00709d59938)
adds `GitIncrementalCheckpointTargetJoin.ForReadOnlyComparison`.
It accepts the immutable historical F-029 mapping **with IsCurrent=false**,
a verified-later-epoch F-091 receipt, F-097b source ref/OID proof,
and the separately versioned immutable checkpoint F-031 observation. The
method refuses mixed SHA1/SHA256 OIDs, changed target approval seals,
foreign checkpoint/snapshot, invalid ref collisions, baseline epochs,
and changed or executable planning authority. It returns a **version 2**
`GitIncrementalCheckpointAuditInputs` whose target evidence digest is the
**full checkpoint F-031 receipt digest**, not merely the target-ref hash.

**Security boundary:** this is a *pure, non-authorizing* domain join,
not a verified PostgreSQL latest-epoch reader. The existing
`PostgresGitIncrementalCheckpointAuditPreflight` and
`PostgresGitIncrementalCheckpointWorkpackJournal` still require the
historical **version 1/current F-029** gate and remain fail-closed for
real second epochs. No F-032/F-038 row is produced; all execution flags
stay false. Source-locking, F-031 audit revalidation, safe append-only
F-032/F-038 storage, replay and real PostgreSQL positive tests are the
next #104 work; #103/#100/#96/#92 remain open.

## Independently verified PostgreSQL v2 reader (2026-11-09)

Implemented on main in [b68a21c](https://github.com/KeyserDSoze/GitHubMigrator/commit/b68a21c6a6797edbff6a9a2c40a708e2635bbeb3)
and [693749c](https://github.com/KeyserDSoze/GitHubMigrator/commit/693749c95f0f7d6da332c8337d724b1cc3ffa3be).
The new internal `PostgresGitIncrementalLaterCheckpointAuditPreflight`
requires an **independent, default-OFF** setting
`GitIncrementalLaterCheckpointAudit:Enabled=true`.
The separate #109 checkpoint-target observation and read-preflight
switches and current source checkpoint/ledger switches remain required.
It is deliberately **not** exposed as an HTTP mutation or worker.

Given only scoped immutable IDs and an expected **full F-031 receipt**
SHA-256, the reader independently loads verified F-091/F-097b and
checkpoint F-031 data from PostgreSQL. It revalidates the existing
independent human reviewer and original F-027 mapping through #109's
source-locked gate, requires a latest later source epoch, bounds the
saved target observation age to under five minutes and enforces matching
SHA-1/SHA-256 object formats. Finally it takes the source lock before
the plan lock, re-reads current mutable source/target/review/validation
state and returns only non-authorizing version-2 comparison inputs.

A new gate witness is **not** compared to the historical F-031 receipt's
original time-sensitive preflight digest: that digest intentionally
changes on a fresh validation. Instead the saved observation's original
SHA-256 seal/audit and its candidate/reviewer identities are verified,
and the independent newly checked live gate must match those identities.
A failed, revoked, superseded, foreign, stale or tampered read closes
without F-032/F-038 rows. The legacy F-029/current-v1 reader and writer
are untouched. **No F-032/F-038/F-035 writer is enabled by this feature.**

The new actual SHA-1/SHA-256 two-epoch PostgreSQL/JWT integration checks
positive v2 comparison, wrong target UUID/digest/repository, later
review revocation and continued absence of F-032/F-038 rows. A future
separately authorized writer must reverify all evidence while holding
its own source-first advisory lock, then use distinct, compatible v2
foreign keys and append-only target provenance: it MUST NOT point a
checkpoint F-031 ID at historical `incremental_target_ref_evidence`.
