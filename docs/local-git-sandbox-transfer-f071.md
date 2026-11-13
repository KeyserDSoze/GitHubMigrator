# F-071 — real Git history transport into a local-only sandbox

## Why this exists

F-060 physically materializes a complete immutable, provenance-bound Git
source mirror from a verified F-038 bundle, and F-056 independently audits
the full reachable Git graph on two bare repositories. F-070 proved five
source snapshot / incremental review-only preparation cycles with crash
recovery and no duplicate work.

The next technical risk is whether complete *Git history* (not only
metadata change records) can be transferred to a fresh destination and
verified without dropping branches, annotated tags, merge parents, trees
or blobs. F-071 exercises that real transport using two **isolated
local filesystem Git bare repositories** as a surrogate for a test
destination. This is NOT GitHub Enterprise Cloud, and a passing test
is NOT evidence of successful real GitHub migration or permission scopes.

## New internal, non-API component

`LocalGitDestinationSandboxTransfer` lives inside the GitObjects
infrastructure assembly as an **internal class**. Existing
`InternalsVisibleTo(GitHubMigrator.Integration.Tests)` permits CI-only
invocation. It is **not** registered with dependency injection or an
HTTP endpoint, and has no access to GitHub credentials, remote URLs,
arbitrary Git refspecs, external target connection settings, or the
production operation-plan authorization pipeline.

Constructor requires two operator/test-controlled, fully-qualified,
existing and disjoint local roots:

- F-060 materialized source root, containing
  `{sourceMirrorId:N}.git`.
- A dedicated scratch/destination root for local sandbox bare targets.

`enabled=false` is the constructor default.

The caller supplies a verified F-059 trusted source plan, a F-060
cryptographic physical mirror receipt and a **GUID-only sandbox ID**.
No repository name, path, URL, token or refspec is accepted.

### Actual Git transport and verification sequence

1. Fail closed on disabled mode, missing IDs, overlapping roots or
   symbolic links / reparse points in the trusted directory ancestry.
2. Recompute the entire F-060 source proof and compare it to the
   supplied immutable receipt, including object digest, source bundle
   provenance and independently timestamp-bound verification hash.
3. Acquire a process gate and a POSIX filesystem lock on the sandbox
   root, then **re-verify all reachable source Git objects** using
   F-056 strict fsck and reference equality.
4. If a target with this sandbox ID already exists, **never update or
   delete it**: re-verify exact expected ref OIDs and full object closure.
   Unexpected refs, corrupt objects or a symlink result in refusal.
5. Otherwise initialize a random private bare Git repository of the
   exact SHA-1 or SHA-256 format; execute a **file-protocol-only**
   `git fetch` from the already verified source with fixed branches and
   tags refspecs. No GitHub HTTPS, `git push`, credential helper,
   hooks, global Git configuration or subprocess-supplied shell script.
6. Strictly verify the temporary target with F-056. Atomically promote
   it to the final GUID.git sandbox directory, refusing pre-existing
   targets. Verify it again after promotion; never repair a corrupt
   promoted target automatically.
7. Finally, independently F-056-audit source and sandbox target,
   comparing full reachable commit ancestry, trees, blobs and tags
   and sealing a sandbox-only SHA-256 receipt bound to the F-060 proof,
   destination ID and F-056 closure evidence.

All Git commands are invoked with argument arrays (not shell strings),
a 90-second subprocess deadline, constrained output, empty Git ambient
environment and `GIT_ALLOW_PROTOCOL=file`. Random staging directories
are the only directories eligible for cleanup, never an existing
permanent sandbox.

**Receipt flags:** `SandboxWritesPerformed=true` indicates **local
sandbox writes only**; `RemotePushInvoked=false`,
`RemoteWritesAuthorized=false`, `MigrationComplete=false` always.

## Actual Linux Git integration tests

- SHA-1 and SHA-256 fully materialized sources containing independent
  `main`, `feature`, an annotated `v1` tag, multiple commits and a
  non-fast-forward merge, transferred with real Git into local bare
  destinations. Ref OIDs, object formats and F-056 full graph digests
  must match exactly. Replays do not rewrite the destination.
- Six concurrent sandbox runners converge on **one** complete bare repo
  and stable receipt with no extra Git directories.
- A ref change in the target after completion causes **refusal without
  overwrite, repair or forced update**.
- A forged source receipt or physical source ref mutation refuses all
  target writes.
- Disabled mode and a symlinked root fail closed.
- A different source mirror can never overwrite a sandbox ID already
  occupied by a separate source.

### Limitations and next steps

This is a **proof of full-history local Git transport**, not GitHub
destination migration. It does not create repositories on GitHub, upload
Git objects via network, run multi-tenant production workers, advance a
remote migration checkpoint or assert end-to-end completion.

Production remote write capability still requires **separately reviewed
GitHub App installation authorization, destination creation/ownership,
non-destructive ref rules, per-workpack target fencing, cancellation and
audited retry behavior**, plus an end-to-end isolated GitHub test-org
pilot. F-071 neither relaxes nor bypasses the existing fail-closed
execution authorization barriers.

No new PostgreSQL schema changes. Branch stacks on F-070 PR #71;
`main` stays unchanged. Run .NET/xUnit, PostgreSQL/real Git integration,
Playwright and Terraform gates before review.
