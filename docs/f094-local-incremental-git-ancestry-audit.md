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
| TargetAheadPreserved | Source commit is ancestor of target in locally available graph | No rewind |
| DivergedBlocked | Both commits available, neither ancestor | No force push |
| HistoryUnavailableBlocked | The target object is not available in the source mirror | No guess or blind retry |
| TagChangeBlocked | Same tag name, different object SHA | No automatic tag replacement |
| TargetOnlyPreserved | Target ref absent from source proof | No deletion |

This receipt is a local observation, not a claim that a remote GitHub
repository has identical objects at the time of reading. It does not
verify a live target branch or transfer missing objects. It cannot
authorize GitHub API writes, pushes, retries or migration completion.
