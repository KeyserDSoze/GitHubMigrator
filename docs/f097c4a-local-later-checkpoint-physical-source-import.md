# F-097c.4a — Local source-only materialization from a later F-097 checkpoint

Tracked by [#101](https://github.com/KeyserDSoze/GitHubMigrator/issues/101),
part of [#100](https://github.com/KeyserDSoze/GitHubMigrator/issues/100)
under parent [#96](https://github.com/KeyserDSoze/GitHubMigrator/issues/96).

The bounded, internal `GitSourceCheckpointLocalMaterializationIntegrity.Bind`
takes independently verified F-029 mapping, latest F-097c.2 F-091/F-031
source-target OID evidence, a separately read F-097b source ref/OID
receipt and the existing F-035 bundle metadata. It **requires a later**
source snapshot (not F-029 baseline), a F-097 predecessor, a complete
1–128 canonical SHA-1 or SHA-256 full-ref list and a *single* reviewed
repository mapping. It recomputes the exact F-030 reconciliation SHA from
the F-097 full refs, F-029 manifest and current target observation,
and compares it to the recorded F-035 bundle's reconciliation SHA.

An off-by-default `LocalGitSourceCheckpointMirrorMaterializer`
delegates only to the existing hardened F-060 **local** bundle importer,
which independently rehashes the immutable UUID.bundle bytes, imports
locally into a UUID.git bare mirror, runs physical Git fsck/ref/OID
verification and computes reachable object closure. It then recomputes
the F-060 hash + verification receipt, rejecting anything that differs.
The F-060 mirror UUID is derived from the **later source snapshot**
and the exact workpack UUID; a baseline mirror cannot be substituted.
Existing F-060/F-059 plan types are used solely as a local importer
adapter; no F-059 coordinator or GitHub HTTP/transport is invoked.

### Trust and remaining boundaries

This is **not** yet a runtime PostgreSQL later-epoch source workpack
builder or an F-061 source lease recorder. The contract must be supplied
from the *independent* F-097c.2 and F-097b verified DB readers and the
F-035 bundle; a self-made hash or user-supplied refs never prove origin.
It does not create, claim or complete an F-061 lease, create F-095 v3,
call GitHub or authorize push/retry/dispatch/migration complete.
An actual source-locked later checkpoint workpack/bundle acquisition
and F-061 recovery flow remains in #100; the durable F-095 ancestry v3
writer stays in #96. Parent #92 stays open.
