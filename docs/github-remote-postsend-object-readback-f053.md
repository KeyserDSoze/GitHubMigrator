# F-053 — GET-only GitHub post-send object tip readback

F-052 classifies the complete remote branch/tag ref set but does not query the
Git objects pointed to by created refs. F-053 adds a separate, bounded
read-only verification step for the future remote reconciliation workflow.

## Readback contract

- A validated GitHub organization, repository and canonical create-only
  branch/tag list (at most the F-046 online candidate limit) are required.
- The adapter uses the existing F-042 full GitHub branch/tag reader. Each
  candidate must already exist at its exact proposed Git object SHA.
- Each created branch or lightweight tag must resolve to a commit returned
  by GET /git/commits/{sha}. Annotated tags are read through
  GET /git/tags/{sha}, up to four nested tag objects, with cycle detection
  and an independently queried final commit. The returned commit SHA and
  reported root tree SHA must be syntactically valid and bound to the proof.
- All candidate-tip object requests use fixed-host api.github.com,
  HTTP GET only, bounded JSON bodies and strict status checks. A hidden
  object (404), timeout, rate limit, invalid JSON, unknown object type,
  mismatched SHA or unexpected pagination is not a verification success.
- The entire branch/tag set is read AGAIN after the Git-object calls.
  Any changed ref, including unrelated branches or annotated-tag types,
  rejects the evidence rather than allowing a blind retry.
- Domain evidence SHA-256 binds the final complete ref observation,
  remote ref fingerprint, all tip and peeled commit OIDs, tree identifiers,
  tag depths and timestamp. A separate assessment independently rebuilds
  the F-052 ref classification and the F-053 object evidence.
- Both readback and assessment explicitly carry
  MayAutomaticallyRetry=false and RemoteWritesAuthorized=false.

## Limitations and security boundary

The adapter is intentionally standalone: it has no API endpoint, worker
registration or GitHub write transport. It cannot create repositories,
push branches or tags, authorize remote writes or bypass the F-047,
F-050 and F-052 write-disabled fences.

This check establishes that GitHub's authenticated Git-data endpoints
returned the selected tip objects. It does NOT verify the complete
ancestry, Git tree/blob closure, LFS objects, signatures, pack transfer
or migration fidelity. The commit endpoint's tree SHA is recorded, but
tree contents are NOT independently fetched. Full source/destination
object-closure verification and a durable pre-send may-have-transmitted
state are required before claiming any real remote migration complete.

The calling workflow must supply a scoped ephemeral installation token
and keep it out of persistence, logs and API responses. No real remote
target token or permission is assumed for F-053.

## Tests

xUnit simulated GitHub HTTP covers GET-only requests, full before/after
ref checks, annotated tags and commit tips, sealed SHA evidence, unknown
or cyclic tags, missing/forged commit objects, moving refs, unexpected
refs and malformed request rejection before any network I/O.
