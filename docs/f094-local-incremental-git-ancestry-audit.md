# F-094 — Offline incremental Git ancestry audit

F-094 is a **read-only local proof** for two deployment-owned bare Git
repositories. It is a prerequisite for future incremental GitHub
synchronization, not a sender or deployment-ready authorization service.

Inputs are GUID handles (not paths/URLs/refspecs), exact sorted branch/tag
ref and Git object ID arrays, the already sealed source F-091 checkpoint
identity/SHA-256 and an F-031 destination observation identity/SHA-256.
The caller MUST source those refs and proofs from independently verified
F-014/F-032/F-091/F-031 storage. This initial internal adapter does not
implement that end-to-end join or expose an unauthenticated public API.

The GitObjects adapter uses fixed deployment-owned paths, an off-by-default
switch, no network protocols and a sanitized process environment. It
reuses F-056's strict bare-repository verification, checks object format,
no shallow/alternates/promisor state, ref/OID correspondence and full fsck,
and seals both reachable-object sets before and after the graph test.
Only up to 128 refs per side are allowed; bigger workloads must be
split into independently verifiable workpacks in a follow-up.

| State | Meaning | Execution |
| --- | --- | --- |
| MatchingObjectId | Same literal full Git object SHA | No remote authority |
| CreateCandidateBlocked | Source-only ref | Must prove target absent and recheck permissions |
| FastForwardCandidateBlocked | Target commit is ancestor of source commit in the local source graph | Still blocked for remote push |
| TargetAheadPreserved | Source tip is proven to be an ancestor of target tip in the sealed source or destination local graph | No rewind |
| DivergedBlocked | Both commits available in the source graph, neither ancestor | No force push |
| HistoryUnavailableBlocked | Neither local mirror provides sufficient cross-tip ancestry proof | No guess or blind retry |
| TagChangeBlocked | Same tag name, different object SHA | No automatic tag replacement |
| TargetOnlyPreserved | Target ref absent from source proof | No deletion |

For a destination-only newer commit, the auditor verifies the source
tip exists as a commit in the target mirror and checks target-side
ancestry before reporting `TargetAheadPreserved`. If both tips are
unavailable in the opposing mirrors, it conservatively reports
`HistoryUnavailableBlocked` rather than guessing divergence. These
checks are covered for SHA-1 and SHA-256 physical Git repositories.

This receipt is a local observation, not a claim that a remote GitHub
repository has identical objects at the time of reading. It does not
verify a live target branch or transfer missing objects. It cannot
authorize GitHub API writes, pushes, retries or migration completion.

## Current implementation and limitations

The F-094 core is `GitIncrementalAncestryIntegrity` in Business;
`LocalGitIncrementalAncestryAuditor` lives in the GitObjects adapter and
reuses the hardened F-056 Git subprocess and complete local mirror checks.
No GitHub adapter, remote token or outbound HTTP path is present in this
component. Auditing is explicitly opt-in through its constructor.

The receipt SHA-256 binds the checkpoint and observation identities,
the supplied proof digests, complete source and destination ref/OID sets,
both reachable object-set digests/counts and every per-ref decision.
The wall-clock time is an observation timestamp, excluded from the
deterministic content digest. The result contains explicit false values
for transport/write/retry/migration completion.

**Not yet done:** fetch the exact F-032/F-091 and F-031 persisted object
IDs and cryptographically validate their mapping into an F-094 request
inside a database transaction, provide a durable result ledger and an
authenticated diagnostic API, and independently compare a fresh live
GitHub target immediately before any authorized remote operation.

## F-096 workpack physical binding

A new optional pair, `SourcePhysicalMirrorId` and
`SourcePhysicalMirrorProofSha256`, makes the local F-094 auditor use
the actual F-060 workpack-scoped bare repository. Providing only one
field, an empty/foreign physical UUID, or an invalid digest fails closed.
Both fields enter a **new version-2** local ancestry SHA; all historical
v1 receipt hashes remain unchanged. The F-095 production recorder now
requires a successful F-035/F-061 verification and this v2 receipt.
This does not authorize GitHub writes.
