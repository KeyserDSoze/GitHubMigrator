# F-036 — authenticated Azure DevOps Git source acquisition

F-036 adds an **actual, authenticated, read-only HTTPS Azure DevOps Git
fetch** in front of the existing F-035 immutable bundle export. It creates
a complete local bare mirror from a **server-derived** Azure DevOps
Services repository, verifies its refs and reachable Git object graph,
and then calls F-035, which independently verifies and hashes an
immutable `.bundle` recorded against the F-034 workpack in PostgreSQL.

The target GitHub repository is never contacted or modified by this
component. The workpack remains `Blocked / WriteBlocked` and the DB
constraints still prevent `ready_for_execution=true`.

## Flow

1. JWT admin/superadmin invokes
   `POST /api/incremental-preparations/{preparationId}/operation-plans/{planId}/transfer-workpacks/{workpackId}/source-bundle/fetch-azure`
   with only `{ "expectedReconciliationSha256": "<64 lowercase hex>" }`.
2. The service validates the current F-029 plan, F-032 reconciliation,
   F-034 blocked workpack and the exact F-014 source ref evidence.
3. The project name and repository name are read from the **historical
   F-014 snapshot**, not mutable staging, a caller-provided URL or Git
   configuration. The source connection ID and organization must match
   the existing `Verified` Azure DevOps Services connection.
4. The PAT is fetched only from the existing configured Key Vault or
   development encrypted-secret store, never from a request body or
   query parameter. Git's remote URL is constructed exclusively as
   `https://dev.azure.com/{organization}/{project}/_git/{repository}`
   with validated and URL-escaped segments.
5. The Git process uses fixed `/usr/bin/git`, no shell, a clean
   environment and an in-memory child-only `http.extraHeader` Basic
   authentication value. `http.followRedirects=false`, SSL verification
   on, all non-HTTPS Git protocols blocked, no credential helper, no
   interactive prompt and no inherited global/system Git config.
6. The worker creates a fresh temporary **bare** repository under
   `<SourceRoot>/<sourceConnection GUID>/<SHA256(repository ID)>.git.tmp-...`.
   It runs `git init --bare`, then a *read-only-to-Azure* `git fetch`
   with only `refs/heads/*` and `refs/tags/*` local refspecs; no Git
   remote or credential is persisted in `config`.
7. It enumerates every Git ref, rejects unexpected branches/tags,
   case collisions and mismatched Git object IDs, checks for symbolic
   links and object-store alternates, runs `git fsck --full --strict`,
   caps the mirror at 512 MiB and atomically promotes the mirror
   directory. Interrupted attempts remove only their random staging
   directory; a successfully promoted mirror is **not overwritten**.
8. The existing F-035 service independently checks the bare mirror,
   creates/verifies a real Git bundle, hashes its bytes and rechecks
   the exact DB source/target provenance under the source advisory lock
   before committing the append-only artifact checkpoint.

## Configuration

Both flags must be explicitly enabled; neither has a default:

```text
GitSourceBundles:Enabled=true
AzureGitFetch:Enabled=true
GitSourceBundles:SourceRoot=/trusted/readonly-verified-or-owned/mirrors
GitSourceBundles:BundleRoot=/trusted/persistent/bundles
```

Both mount paths must already exist, be absolute, have no symlink
ancestors, and be writable only as required by the runtime non-root
identity. The runtime Docker image now includes Git and CA certificates.
Deploy mirror/bundle storage with encryption, service-owned
permissions, disk quotas and retention. The process currently runs
under the API service identity: separate-container/UID isolation is a
future hardening requirement, not a capability claimed by F-036.

A local bare mirror already acquired for the *same* source connection
and repo ID is revalidated against exact refs and `git fsck` and
reused on retry. This is safe historical source evidence, **not**
proof that the Azure DevOps repository is unchanged in real time.

If the process crashes after mirror promotion but before the bundle
checkpoint, the next request re-verifies the local mirror and continues.
If the process crashes after the bundle is written but before F-035
commits the DB row, the F-035 no-overwrite protection deliberately
stops and requires operator quarantine/repair of the orphan bundle.

## Tests and honest limitations

CI uses Git for real: a test-only injected read-only transport sources
a local bare Git upstream to verify actual fetching and the complete
commit/tag contents. Tests exercise retry/reuse, ref drift, unexpectedly
present source branches, symlink paths, cancellation, and prove that
the production transport puts the PAT **only** in child environment
configuration, never in process arguments or the remote URL.
A real Azure DevOps organization is not available to CI; the live
authenticated endpoint therefore requires a separate staging smoke
test with scoped credentials. The API+PostgreSQL integration checks
RBAC, feature-disabled behavior, proof mismatch, idempotent F-035
checkpoint reuse and no unapproved GitHub calls.

This is still a **synchronous** bounded Git operation initiated through
the admin API. A durable *in-progress fetch* lease and crash-recoverable
multipart object transfer are not yet implemented; the F-034 job
provides the proof-bound evidence barrier and F-035 provides the
completed export checkpoint. No Azure DevOps Server / on-prem,
TFS or non-HTTPS endpoint is enabled in F-036. Microsoft recommends
Entra tokens for new Azure Git authentication; F-036 initially uses
the existing application's Azure DevOps PAT store, which must have
least-privilege code read access.

No GitHub push, repository creation, permission grant, ref update,
deletion, synchronization execution, or post-import verification exists.
Before enabling destination writes, implement an independently reviewed
authorization system, transfer leases, object rehash on replay and
actual non-destructive GitHub-side verification.

Official Git documentation: https://git-scm.com/docs/git-config
Microsoft Azure Repos cloning:
https://learn.microsoft.com/en-us/azure/devops/repos/git/clone
