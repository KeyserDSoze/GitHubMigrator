# F-097c.4b — Durable F-061 generation lease on a later local source checkpoint

[Issue #102](https://github.com/KeyserDSoze/GitHubMigrator/issues/102)
is a bounded part of [#100](https://github.com/KeyserDSoze/GitHubMigrator/issues/100)
under [#96](https://github.com/KeyserDSoze/GitHubMigrator/issues/96) and [#92](https://github.com/KeyserDSoze/GitHubMigrator/issues/92).

`DurableGitSourceCheckpointMirrorMaterializer` accepts only independently
verified F-029, F-097c.2, F-097b and F-035 **in-process** evidence.
It refuses a baseline F-029 snapshot, mismatched full Git OID/ref evidence,
wrong F-031 target/reconciliation digest, invalid source bundle metadata,
any remote write authority and missing source predecessor. It derives
the F-060 source mirror UUID from the **later F-091 checkpoint snapshot**
and immutable source-only F-035 workpack UUID.

When explicitly enabled, it delegates to the existing
`DurableGitSourceSnapshotMirrorMaterializer` rather than implementing
a second lease protocol. That wrapper acquires the exact F-061 PostgreSQL
mirror lease with generation fencing, periodically renews it, calls the
real local F-060 bundle materializer, verifies the physical ref OIDs and
full reachable Git object closure and ensures the F-060 receipt was
recomputed **after** lease admission. It then writes a F-061 `Verified`
event with the immutable object-set digest and releases ownership.
Failure, orphaned promotion and corrupt physical objects retain the
existing recovery/quarantine behavior. Cross-replica replay must re-fsck.

This adapter is **disabled by default** and never calls a GitHub
provider, a sender, F-059, F-095 or any destination write. It does not
create a F-035/F-038 later-epoch source bundle or revalidate mutable
checkpoint selection under a source-first PostgreSQL lock at admission;
that next persistent ingestion step remains in **#100**. Thus this
component is not yet a complete operational incremental migration flow.

## Real PostgreSQL integration results

The PostgreSQL 17 fixture runs with actual local Git bare repositories
and immutable bundle imports for SHA-1 and SHA-256. A F-097 full-ref
checkpoint **after the baseline** is bound to the source bundle and
the existing F-061 lease generation protocol. Checks include:

- Disabled admission rejects before any F-061 record is created.
- Forged source snapshot is rejected before acquiring a lease.
- Physical F-060 import verifies all real Git refs, merge/tag histories
  and reachable objects; F-061 stores one `Verified` event and releases
  lease ownership, with four execution flags false.
- Re-reading from a new DbContext reacquires the lease as generation 2,
  re-fscks the same immutable physical mirror and appends another
  `Verified` event without rewriting the bare repo.
- A corrupted immutable F-035 bundle re-verification fails and sets
  generation 3 to `RecoveryRequired` with a `Quarantined` event.
- Three separate DbContexts/worker instances serialize the F-061 lease,
  each verifying the same source object set under a distinct generation,
  with one persistent source mirror and no remote writes.

All four jobs passed on [GitHub Actions 42613748548](https://github.com/KeyserDSoze/GitHubMigrator/actions/runs/42613748548).
This integration deliberately starts from separately verified F-097b
and F-035 evidence. **Source-scoped transactional creation** of the
later checkpoint's F-032/F-038/F-035 records remains in #100.
