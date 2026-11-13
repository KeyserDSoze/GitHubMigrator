# F-097c.1 — Full-ref source checkpoint-bound offline ancestry v3

Tracks [#97](https://github.com/KeyserDSoze/GitHubMigrator/issues/97) under [#96](https://github.com/KeyserDSoze/GitHubMigrator/issues/96) and parent [#92](https://github.com/KeyserDSoze/GitHubMigrator/issues/92).

The F-094 read-only local Git ancestry request has an **optional**
`SourceCheckpointGitRefProofSha256`. When absent the SHA-256 digest
layout and domain of historical F-094 unbound v1 and F-096 F-060-bound
v2 receipts are unchanged. When supplied, it **requires** both
physical F-060 mirror ID and proof, activates the
`f097c-offline-git-ancestry-checkpoint-mirror-bound-v3` domain,
and includes a separate typed `f097-immutable-full-ref-oid-proof`
row before the canonical Git ref/OID list. This prevents proof swapping
across source checkpoint IDs, ref sets or separately verified physical
source mirrors.

`GitSourceCheckpointPhysicalAncestryIntegrity.Verify` validates an
independently sealed F-091 checkpoint and F-097 full-ref proof
(including its exact F-097 predecessor if the epoch is incremental),
the original F-029 operation mapping, the F-061 physical mirror
identity/verified SHA/object-set/count against **the exact later
checkpoint snapshot**, and the F-094 v3 physical audit receipt.
The audited source OIDs must be byte-for-byte equal to the F-097
manifest OIDs; full Git closure and ancestry are verified by the
existing offline physical auditor. Mirrors must be Verified,
non-executable and fresh for a new audit.

## Trust boundary and remaining work

The verifier consumes **trusted inputs** and is not by itself a proof
of PostgreSQL provenance. Production use must re-fetch and independently
validate the source F-097b receipt, F-091 checkpoint, F-014 source
observations, F-060/F-061 workpack and F-031 target observation
under source-locked PostgreSQL; it must then run the real local
Git auditor against a deployment-owned GUID-addressed bare mirror.
This is the responsibility of still-open **#96**. F-095 remains
baseline-only and **does not yet store v3 receipts**. No new API,
sender, remote read/write, retry, dispatch, live target verification
or migration-complete authority has been added.

The new v3 format is tested against actual SHA-1 and SHA-256 local
source/target bare mirrors, with tamper and unsafe-incomplete cases.
