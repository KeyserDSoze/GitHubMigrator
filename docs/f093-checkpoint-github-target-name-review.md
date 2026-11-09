# F-093 — source checkpoint vs verified GitHub ref-name review

F-093 adds a **GET-only historical safety review** to F-092. For one
source Git repository mapped in an F-029 immutable plan, the operator
supplies an existing version-two F-091 source checkpoint and the UUID
plus exact SHA-256 of a previously collected F-031 GitHub target ref
observation. Nothing is fetched from a provider in this request.

Before returning any rows the database re-verifies the F-090/F-091
checkpoint chain, its F-025 per-ref SHA, and the sealed F-031 target ref
observation. The F-031 must still be current and its observation timestamp
must **not predate** when the source checkpoint was committed to staging.
The source/target mapping must match the F-029 plan, and the requested
cursor must exist for this exact snapshot and repository.

A single keyset page returns up to 50 source changes for the chosen
repository and a name-only result from the verified F-031 ref set:
present, or absent **at observation time**. The eight decision states
explicitly distinguish Added/Restored, Changed, and Missing. A missing
source ref never requests GitHub deletion, and even an Added ref absent
at the target remains review-only. A Changed ref cannot be treated as
fast-forward without a physical Git object ancestry check.

**Important boundaries:** a source F-013 SHA-256 fingerprint is a
fingerprint of normalized staging metadata, **not a Git object ID**.
This comparison cannot claim branch tip equality, whole history
transfer, current live GitHub state, actual GitHub permissions,
safe automatic retry or migration completion. No token, live network
call, GitHub write, credential or sender enters F-093.

Full F-091 journal integrity and full bounded F-031 observation integrity
are verified for every page, so end-to-end operation cost is not yet
O(page-size) despite the bounded SQL page. Independent fresh target
observation and full Git history closure remain prerequisites of any
future remote synchronization executor.

## Operator workflow

In **Source sync**, open an attested (version 2) checkpoint, choose
**Inspect source ref changes**, then expand **Review against saved GitHub
observations**. The operator can choose only F-031 observations marked
current, bound to the same operation plan and repository mapping, and
recorded no earlier than the source staging commit. The UI shows
name presence/absence and the source change's safety classification.
The server independently revalidates all these conditions.

The API route is:

```http
GET /api/incremental-preparations/{preparationId}/operation-plans/{planId}/source-sync-checkpoints/{checkpointId}/target-ref-review/{observationId}?sourceRepositoryId={sourceRepositoryId}&expectedTargetEvidenceSha256={sha256}&take=50&after={optionalCanonicalSourceId}
```

It requires a password-changed superadmin and inherits the staging-sync
rate limiter. Its response has `canPush=false`, `canRetry=false`,
`canDispatch=false` and `migrationComplete=false`.

This API does not imply that a previously saved F-031 observation still
describes live GitHub state beyond its short current-observation window.
New observations must be independently acquired through the existing
read-only GitHub target-ref evidence workflow.
