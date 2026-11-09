# F-097c.2 — Verified source/target checkpoint audit preflight

Tracks [#98](https://github.com/KeyserDSoze/GitHubMigrator/issues/98) under
[#96](https://github.com/KeyserDSoze/GitHubMigrator/issues/96) and [#92](https://github.com/KeyserDSoze/GitHubMigrator/issues/92).

The internal, disabled-by-default
`PostgresGitIncrementalCheckpointAuditPreflight` resolves **only**
UUID handles and an expected target SHA-256. It never takes branch names,
Git OIDs, workpack paths, GitHub URLs, credentials or refspecs from an HTTP
request. It does not have an HTTP route or register any sender.

It verifies an active F-029 reviewed plan, its **exact latest** F-091
checkpoint, an F-097b durable full source branch/tag/OID proof (including
its independently read F-014 source manifests and all predecessors) and
the immutable F-031 destination observation. The target must still be
current, mapped to the same source repository, observed after the source
checkpoint's staging commit, and cryptographically consistent with its
original organization/repository/ref data. Source and target ref arrays
must be canonical, contain 1–128 refs each, and have homogeneous SHA-1
or SHA-256 object IDs. Global F-013 ACL completeness is **not** treated
as Git ref completeness.

`GitIncrementalCheckpointAuditInputs` contains only independently
verified refs, SHA handles and four fixed `false` execution flags. It
is read-only evidence **for a future physical audit**, not a reusable
F-094 request or an F-095 receipt.

The reader purposefully does **not** take an F-060 physical workpack or
run local Git. A future F-095 v3 durable writer must re-read all this
evidence **under the PostgreSQL source advisory lock** and verify the
physical F-060/F-061 mirror, full object closure, F-097c.1 v3 SHA and
target sandbox before storing a record. No success/push/retry/dispatch
permission is introduced. Parent #96/#92 remain open.

## Integration verification

Real PostgreSQL 17 integration tests use the completed F-014 source
discovery and F-090/091 checkpoint fixture, F-097b immutable ledger,
and an independently sealed F-031 observation. SHA-1 and SHA-256
source/target ref OIDs are both covered, including fresh readback after
restarting the DbContext. Negative tests refuse a missing F-097 receipt,
wrong repo, incorrect checkpoint, incorrect target SHA, mixed SHA-1/
SHA-256 repositories, tampered F-014 source observations, and expired
F-029 plans. The F-029 plan may expire **before** an F-031 observation;
the fail-closed rejection then correctly reports plan staleness first.
The suite also asserts that no F-095 ledger row is written.

The preflight is intentionally not registered as an executable worker.
The long-running F-095/F-060/F-061 physical and durable later-epoch
integration remains open as #96.
