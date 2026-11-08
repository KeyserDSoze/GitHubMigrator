# F-058 — Durable, non-executable independent remote object reconciliation

F-057 introduced an independently fetched, read-only Git smart-HTTPS
mirror audited against a trusted local source by F-056. F-058 binds
**the claimed F-057 observation** to the existing permanent GitHub
transfer safety history and stores a single immutable reconciliation
receipt per attempted transfer in PostgreSQL.

## Provenance graph

`F-047 BlockedAtWriteBarrier → F-050 ReviewedWriteDisabled / RecoveryRequired
→ F-052 reviewed test grant → F-054 typed-tip readback
→ F-055 MayHaveTransmitted → F-057 independent read-only full-object copy
→ F-058 immutable evidence journal`.

- Readback, possible-send marker and grant must share the *same* numeric
  GitHub repository ID and F-052 grant; **composite PostgreSQL foreign
  keys enforce that binding**. No cross-repository or cross-grant
  evidence can be inserted.
- Every new record holds only immutable hashes and IDs: original
  F-051 typed-ref baseline fingerprint, F-044 create-candidate SHA,
  complete target ref-set fingerprint, F-057 provider-sealed
  observation SHA, F-056 full Git-object closure SHA, F-057 receipt SHA,
  source snapshot UUID and versioned F-058 evidence SHA.
- Read-only reconciliation is valid even after the F-052 test intent
  **expires**, provided its historic review and current persisted
  no-write states are still coherent. No expired grant is extended
  or converted into a new execution lease.
- All expected target refs must equal **exactly** original baseline
  refs plus authorized create-only refs. Annotated-tag object types
  are included in the original baseline SHA. No missing,
  unexpected or divergent Git branch/tag is classified as success.
- The F-057 proof must be fresh (<=90 seconds), from the same
  numeric repository, source snapshot, typed-ref observation and
  allowed org/repo. The F-057 versioned SHA is recalculated, and
  no remote push, write authorization, automatic retry or migration
  completion flag may be set. This integrity hash is **not**
  a cryptographic signature by GitHub or a trusted worker.
- Before insert, the journal obtains the **same target-specific
  PostgreSQL advisory transaction lock** as F-050/F-052/F-054/F-055,
  reloads all historic records, checks independent observer identity
  and verifies the original F-054 and F-055 append-only events.
  A second replica with identical evidence returns the same row ID;
  a different observation or actor is rejected and never overwrites
  the original proof.

## Write barriers and database safety

The single state `FullCopyEvidenceOnly` must never be interpreted as
`MigrationComplete`, `TransportSent` or `ReadyToRetry`. SQL CHECKs
permanently require:

- `remote_push_invoked = false`
- `remote_writes_authorized = false`
- `may_automatically_retry = false`
- `migration_complete = false`

Database triggers forbid UPDATE and DELETE on both the receipt and its
append-only audit events. The F-055 `MayHaveTransmitted` marker is
unchanged by F-058 and remains permanently non-retriable.

The F-058 journal is **default-off**, has a separate numeric GitHub
repository allowlist and trusted org/repo target:
`GitHubRemoteObjectReconciliation:Enabled=false`,
`AllowedRepositoryId=0`, `AllowedRepository=""`.
It is registered only as a scoped internal read-only evidence service,
**not an HTTP endpoint, background sender or GitHub transport**.
The schema is initialized at API startup after F-052–F-055.

## Explicit limitations

- The journal is an evidence **storage** boundary, not an acquisition
  worker. A future separately authorized read-only orchestration must
  invoke the real F-057 runner, verify its in-process provenance,
  and pass the receipt directly rather than accepting a user-crafted
  JSON object. Ordinary digest recomputation does not authenticate
  a caller, GitHub, or the worker that performed the fetch. F-058
  deliberately exposes no API accepting these DTOs.
- F-057's `SourceSnapshotId` is persisted and hashed, but its physical
  relationship to the F-047 source bundle/workpack must be proven by
  a future durable source artifact link. No migration success claim is
  made until this binding and all workload/LFS/submodule checks exist.
- A matching remote object closure never proves that a particular
  **send attempt** succeeded: data could have been present already or
  introduced by another actor. F-055 uncertain outcomes cannot be
  automatically retried or released through this record.
- The F-057 GitHub.com HTTPS integration is not run in CI; F-057
  tests cover local real Git fetch. This journal's PostgreSQL tests
  use a domain-sealed *synthetic* F-057 receipt, not a live GitHub
  attestation. An independently approved live read-only test and
  provenance-enforced orchestration remain necessary.
- Existing F-056 limitations remain: bounded 200k local objects,
  no full LFS/submodule verification, no GitHub credential minting,
  no remote-snapshot transaction lock, no remote Git writes.

## Automated verification

- xUnit: SHA-bound typed baselines, exact remote ref classification,
  forged closure/receipt, unexpected refs, staleness and no-send
  flags.
- PostgreSQL 17: six replicas committing one immutable receipt/event,
  idempotent replay, conflicting observer refusal, expired historic
  reviews, changed F-050 fence denial, non-retriable F-055 marker,
  direct SQL denial of writes/retry/completion, and composite FK proof.
- Existing .NET, Playwright, and Terraform checks run on the PR.

F-058 is stacked on F-057, while the default branch is unchanged.
