# F-097c.3: read-only physical mirror proof join

[Issue #99](https://github.com/KeyserDSoze/GitHubMigrator/issues/99),
child of [#96](https://github.com/KeyserDSoze/GitHubMigrator/issues/96).

An internal, disabled-by-default PostgreSQL reader extends the verified
F-097c.2 source/target SHA-1/SHA-256 checkpoint preflight with exact
F-061 **physical mirror** metadata and full F-032/F-035/F-038 provenance.
Selection is only by checkpoint, target observation and workpack GUIDs.
The mirror's physical UUID is derived from the exact F-097 source snapshot
and workpack UUID, **not** from the original F-029 snapshot.

The implementation independently verifies F-091 and chained F-097b
immutable F-014 source refs, F-031 target observation, F-032 full
reconciliation item digest and ref counters, F-038 workpack's permanent
destination write barrier, F-035 immutable bundle filename / SHA / ref count,
and F-061 lease generation, released lease, latest Verified event, 
Verified state, object closure hash/count and F-060 mirror SHA-256.
It rechecks logical proof identity and refuses mismatched or stale epochs.
The returned `GitIncrementalCheckpointMirrorReadPlan` is version 1 and
has four hard false execution flags.

This component **does not** open the physical mirror, run `git fsck`,
record a new F-095 audit, or send any objects to GitHub. It is a
precondition only, not an authorization. F-095 still requires a separate
source-locked re-read and real physical F-094 v3 audit. Existing F-095
v1/v2 records and SHA hashes remain untouched; #96/#92 stay open.

## Verified integration coverage

The real PostgreSQL 17 integration fixture uses a completed F-014 source
discovery, an F-091 checkpoint, F-097b immutable source ref/OID evidence,
a current F-031 destination observation, an F-032 reconciliation with
exact source F-014 manifest root and source/target SHA, an F-038 durable
WriteBlocked workpack, a source bundle F-035, and an F-061 Verified
physical-mirror receipt plus its latest Verified event.

SHA-1 (40-character OIDs) and SHA-256 (64-character OIDs) pass the
proof join, survive a new DbContext/restart read, and remain
non-executable. Tests reject disabled mode, foreign/missing workpack,
wrong target SHA and F-061 immutable ref-set mutation; no F-095 row
is created. The original F-095 baseline-only writer is unchanged.
Verified by [GitHub Actions run 42607239215](https://github.com/KeyserDSoze/GitHubMigrator/actions/runs/42607239215)
(all four jobs successful).
