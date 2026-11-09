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

## Distinct source-locked F-032v2/F-038v2 write-blocked capture (2026-11-09)

Newly implemented under #104 on sole `main`:

- [871490b](https://github.com/KeyserDSoze/GitHubMigrator/commit/871490b1916af4ea6c2b373e2e996acdf6489473) defines an **independent version-2 SHA-256 workpack receipt**, binding the immutable F-029 baseline mapping, genuine later F-091/F-097b full source OIDs/root, **entire approved F-031 checkpoint receipt digest**, F-032v2 reconciliation ID/SHA, F-038v2 workpack ID, source actor and permanent `Blocked` / `WriteBlocked` / false authority flags.
- [9ef7948](https://github.com/KeyserDSoze/GitHubMigrator/commit/9ef794872021f978044a149295efe01313a67958), [12087a8](https://github.com/KeyserDSoze/GitHubMigrator/commit/12087a8817f6d680b53cd87c8ee614ffc36ccf32): additive **separate** `git_later_checkpoint_reconciliations`, `git_later_checkpoint_workpacks` and `git_later_checkpoint_workpack_events` tables, with F-031 FK to `git_checkpoint_target_observations` (NOT old `incremental_target_ref_evidence`), SQL CHECK/UNIQUE and mutation-blocking triggers. Same relationships exist in EF-created fixtures.
- [996199a](https://github.com/KeyserDSoze/GitHubMigrator/commit/996199ae8acae63cc141fd062503db9790b184af): `PostgresGitIncrementalLaterCheckpointWorkpackJournal`, an **internal synchronous** atomic capture with source-first and plan-second transaction locks; it revalidates latest staging, F-091 attestation, F-097b persisted source root/actor, reviewed F-031 receipt/audit, immutable human review/audit, original F-027 approval, destination validation and five-minute observation freshness **under lock**. It stores F-032v2/F-038v2 + single audit atomically and verifies exact actor/evidence/child semantics on replay.
- [0d55097](https://github.com/KeyserDSoze/GitHubMigrator/commit/0d55097fa049e3b5f22a3c297e9f60676166344f), [19aecd3](https://github.com/KeyserDSoze/GitHubMigrator/commit/19aecd379857761fb23914b8aa05caecfa8c86c8): separately register `GitIncrementalLaterCheckpointWorkpack:Enabled=false` by default; test fixtures explicitly opt in.
- [81c8469](https://github.com/KeyserDSoze/GitHubMigrator/commit/81c84693887f5d543a2427102759af9911e5dcc7): adds real SHA1/SHA256 **two-epoch** PostgreSQL integration for atomic persisted capture, exact replay, actor/digest refusal, SQL mutation rejection and F-027 revocation, with explicit zero rows in legacy F-032/F-038.

**Architecture boundary:** These are **F-032v2 and F-038v2 logically equivalent but isolated durable records**, not rows inserted into legacy `incremental_git_ref_reconciliations` / `git_transfer_workpacks`. The legacy worker, GitHub remote sender and F-035 source-bundle acquirer cannot see the new rows. This is deliberate: the old F-032 FK binds the historical F-031 and could not safely represent a later checkpoint. The new writer exposes **no HTTP mutation endpoint, no worker registration and no destination credentials/provider call**. No F-095 proof, physical ancestry or migration completion is claimed. Subsequent #103 must add independently authenticated/read-only v2 readback and F-035 / F-060 / F-061 safe acquisition without granting remote writes.

CI for latest integration [42661512812](https://github.com/KeyserDSoze/GitHubMigrator/actions/runs/42661512812) must be confirmed 4/4 before considering this scoped slice complete. Keep #104 open until verified.

## F-097c.4c.2 — historical GET-only F-032v2/F-038v2 readback (2026-11-09)

Implemented as a separate, default-**OFF** feature `GitIncrementalLaterCheckpointHistory:Enabled=false`, independent of the F-032v2/F-038v2 writer switch. The only endpoint is an authenticated **superadmin GET**:

`/api/incremental-preparations/{id}/operation-plans/{planId}/source-sync-checkpoints/{checkpointId}/later-workpacks/{workpackId}/history?sourceRepositoryId=...&targetObservationId=...&expectedTargetReceiptSha256=...`

Every handle is an exact, previously issued immutable scope. No caller raw refs, object IDs, actor/reviewer UUIDs, local Git paths, PATs or mutable destination URL are accepted. The endpoint is GET-only and is not attached to any F-035 worker or remote sender.

The reader independently verifies the **stored** F-091/F-097b complete source ref/OID chain, original F-029 reviewed mapping, #109 immutable F-031 checkpoint observation and audit, separately approved historical human review and audit, the original SHA-256 F-032v2 canonical comparison, the full version-2 workpack receipt, one F-038v2 event, F-032v2/F-038v2 record linkage and false execution flags. A mismatched target digest or tampered child row returns a conflict, never a fallback to legacy F-031.

The returned projection contains **only** checkpoint/target/workpack/reconciliation IDs, SHA-256 digests, bounded category counts, record time and `Blocked` / `WriteBlocked` state; `historicalOnly=true`, `currentApprovalVerified=false`, and all other execution flags are false. This is intentionally different from a live admission: historical signed evidence remains inspectable after an F-027 revocation, expiry or superseding source epoch, but a historical receipt is **never** a current permission, a retry token or migration-completion evidence. Any future action must recheck current source, human review and GitHub target under a new source-locked gate. See #111 and parent #103.

The frontend exposes an explicit UUID-driven **read-only inspector** under the saved F-031 target observation. No auto-load, POST or provider GET occurs. The UI requires an exact allowlist of response fields and rejects any forged push, retry, dispatch, non-`WriteBlocked`, raw ref/OID or extra actor information rather than rendering it. Actual two-epoch SHA-1 and SHA-256 PostgreSQL tests check JWT role/default-OFF, positive history, wrong target/receipt, redaction, and the original approval's revocation. Playwright checks that the browser makes exactly one GET and rejects executable server responses. CI must pass **4/4** before closing #111.

## #112 F-035v2 source-only preparation contract (2026-11-09)

The new `GitIncrementalLaterCheckpointBundleIntegrity.Prepare/Verify`
produces a **domain-only, non-authorizing F-035v2 read plan** from an
independently loaded and sealed later F-091/F-097b checkpoint, original
F-029 mapping, exact #109 F-031 receipt, F-032v2 canonical reconciliation
and F-038v2 permanently `Blocked/WriteBlocked` receipt. The versioned
SHA-256 plan binds the full F-038v2 digest, checkpoint, source snapshot,
source manifest root, F-097b full-ref proof, F-031 reviewed observation
and all canonical source branch/tag Git ref names + **real object IDs**.
It supports homogeneous SHA-1 and SHA-256 OIDs and limits bundle inputs
to 128 refs. Changed digests, ref names/OIDs, source scope, target
observation, baseline/current plan, and all authority flags fail closed.

The nested `GitSourceBundleRequest` is **internal only**. No API, caller
bundle path, GitHub credential/URL or local acquirer/worker is exposed;
`CanAcquireBundle=false` and every destination write flag is false.
The existing baseline F-035 contract/storage/hash and legacy sender
remain unchanged. Self-sealed synthetic input is not database
provenance: a later source-first locked PostgreSQL verifier must reload
current F-091/F-097b, F-031, F-032v2, F-038v2, latest snapshot and
human gate before any actual local Git export, then verify source mirror
refs/OIDs with fsck and persist bundle SHA/bytes and lease fencing.
This future physical acquisition/replay is **still open** under #112/#103,
as are F-061 and F-095 under #100/#96. See [issue #112](https://github.com/KeyserDSoze/GitHubMigrator/issues/112).

## #112 internal PostgreSQL F-035v2 live read gate (2026-11-09)

`PostgresGitIncrementalLaterCheckpointBundleReadPreflight` is now a distinct
default-**OFF** internal reader (`GitIncrementalLaterCheckpointBundleRead:Enabled=false`).
The caller supplies **only persisted identifiers** (preparation, plan,
F-091 checkpoint, F-031 observation, F-038v2 workpack, repository source ID
and full expected F-031 receipt digest). It first requires the verified
F-032v2/F-038v2 historical store/readback, then independently rechecks the
**current** F-091/F-097b source chain, latest snapshot, reviewer approval,
F-031 target observation and destination validation via the existing
source-locked v2 audit gate. It reloads and compares the workpack receipt,
F-032v2 SHA, source manifest and exact original OIDs, and produces the
domain F-035v2 preparation only when historical **and** live gates agree.

This is not a capability, not an exposed HTTP operation, and not a physical
Git bundle. It deliberately includes `CanAcquireBundle=false` and all
write flags false. The existing local bare-mirror acquirer is NOT invoked.
A future **physical** F-035v2 writer still needs its own source-first lock
and lease fencing across staging, local mirror fsck/object closure, canonical
ref comparison, restricted `UUID.bundle` path, file SHA-256 and byte length
persistence, fault recovery and idempotent replay. A prior historical receipt
cannot authorize this after reviewer revocation or source supersession.

SHA-1/SHA-256 two-epoch PostgreSQL tests check successful read-only
preparation, wrong F-031 SHA/scope and F-027 revocation. #112 and #103
remain open for the physical source-only acquisition. The closed #111
history readback remains safe and available as *history* even when
the new current F-035v2 gate rejects a bundle.
