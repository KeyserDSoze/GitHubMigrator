# F-056 — Offline Git object-closure verification (SHA-1 / SHA-256)

The product's F-041/F-048 sandbox workflows already run full Git fsck.
F-056 adds an independently reusable, **read-only** audited comparison
of the objects reachable from explicitly bound branch and tag OIDs in
two **local bare** Git repositories. It does not contact GitHub and does
not grant any transport/write permission.

## How it works

1. Caller supplies two opaque server-side UUIDs and the complete canonical
   source and target refs (which may include unrelated target-only refs).
   Deployment supplies two **trusted absolute roots**. The implementation
   derives only `<root>/<uuid:N>.git`; it accepts no URL, user path,
   Git refspec, SSH command, token or repository mutation.
2. Validate the selected source refs appear on the target with **exactly
   the same OIDs**. Read the *complete physical* refs on both local bare
   repositories, validate exact sets against the request and assert the
   Git object format is consistently SHA-1 or SHA-256.
3. Reject shallow, promisor/partial clone, local object alternates,
   replacement/object graft concerns and symlinked files/directories
   (including nested objects and refs). Use an isolated, non-interactive
   Git environment that forbids remote protocols and lazy fetching.
4. Run real `git fsck --full --strict --no-reflogs --no-progress` on
   **both repositories**, not the weaker `--connectivity-only` check.
   The check walks all available commits, parent links, tag objects,
   trees and blobs, rejects unavailable/corrupted objects and checks
   object hash integrity.
5. Enumerate every reachable object using
   `git rev-list --objects --no-object-names --stdin` seeded by the
   source's explicitly selected tip OIDs on both sides. Deduplicate,
   bound the count and hash a **sorted full OID set** using a
   version-separated SHA-256 digest. Target-only refs are preserved,
   but their extra history is not compared against the selected source.
6. Repeat full `fsck`, complete-ref observation and reachable-object-set
   calculations to detect concurrent local modifications before returning.
   Seal a digest of the source/target identity, ref sets, formats, object
   counts and object-set hashes. No paths or individual OIDs are exposed in
   the receipt.

## Constraints

- Entirely offline. **GitHub's remote object store is NOT verified**:
  that requires a separate authenticated post-transfer fetch to a fresh
  trusted, disposable local mirror, followed by the same full verification
  and independent evidence reconciliation. F-053 is only tip-level readback.
- `MaxObjects=200,000`, `MaxRefs=25,000`, a 16 MiB command-output bound
  and 90-second per-command deadline are conservative fail-closed
  resource limits. Large repositories require a paged/disk-backed proof
  implementation before this auditor can be a production acceptance gate.
- Git submodule pointers record commit IDs in the superproject tree;
  the nested submodule repositories are **not included** in the
  superproject's object store. Git LFS external bytes, SHA-1 collision
  policy, Git commit/tag signature trust and GitHub-specific rules
  are separate checks. The audit never claims complete workload migration.
- Read-only Git commands only. The runner is opt-in `enabled:false`
  by default, not registered as API/worker, and never calls
  `git push`, `clone`, `fetch` or a GitHub network endpoint.
- No global coordination against other independently running local
  processes or filesystem adversaries holding write access to the
  trusted bare directories. The pre/post observations guard simple
  drift; a future remote proof requires an isolated immutable snapshot
  and a durable lock/lease across replicas.

## Automated verification

- xUnit domain: exact baseline and target-only refs; mismatch, case
  collisions, object format mixing, counts and fingerprint validation;
  immutable no-send/no-remote-success receipt semantics.
- Real Linux Git integration: SHA-1 and SHA-256 histories, divergent
  branch commits, a merge commit, annotated tag, blobs/trees, preserved
  target-only ref, repeated read-only verification, damaged pack,
  changed target ref, shallow/alternate/promisor directories, symlinks,
  disabled configuration, cancellation and missing repositories.
- CI: .NET 10, PostgreSQL 17, React/Playwright, Terraform.

The feature is intentionally independent of the F-055
`MayHaveTransmitted` journal; it cannot transition any remote send
state, automatically retry or authorize remote GitHub writes.
