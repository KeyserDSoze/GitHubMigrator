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
