# ADR-013: a read-only publication plan before any publication

Status: Accepted (plan implemented; writing to GitHub is not)

Context: ADR-012 ends with an approved workflow that nobody can yet put in a pull request. The product has no GitHub write path at all, and
its whole write chain (F-040…F-097) is deliberately guarded: a separate repository-scoped credential, two people, fences, readbacks. A pull request is a
GitHub write and must not bypass that stance. It also needs facts the simulator does not model.

Decision:
- **Plan first, write later.** `POST …/proposals/{id}/publication-plan` reads the destination (GET only, one repository) and returns exactly what
  would be published and what is in the way. A person does the push and opens the pull request with the steps shown.
- **Only the approved text.** The plan goes through `RequireApprovedAsync` before the destination is read: a proposal that is pending, rejected, revoked,
  blocked or whose stored text no longer matches its hash is not planned, and the destination is not contacted.
- **A hash for the whole plan.** `planSha256` covers the proposal, repository, base branch and *its commit*, branch, path, commit message, title and
  description. A future executor must be asked to confirm this hash, so it can only write what a person saw against a base that has not moved.
- **Findings come from what the destination holds.** Archived or missing repository, missing base, a different file at the path, the branch taken, a pull
  request open from it; an identical file is "nothing to publish", not "ready". An unreadable destination is a blocker, never an empty result.
- **Untrusted text stays data.** Pipeline name, user names and the model's notes cannot open a heading, link or mention in the description; every value in
  the commands is single-quoted.
- **The simulator learned `head` and `base` filters on the pull request list**, because the plan depends on them and GitHub honours them. The reader also
  refuses to trust the filter: it counts only pull requests whose head branch is the planned one.

Consequences:
- Nothing is written to GitHub by this feature. `writesPerformed` is always false in the answer, and an integration test checks that the simulator saw
  GET requests only, and only for that repository.
- **The executor is a separate decision, and it needs a new credential.** Pushing under `.github/workflows/` needs the `workflow` scope (token) or
  *Workflows: write* (GitHub App); the pull request needs *Pull requests: write*. F-043 attests `Contents: write` only, on purpose. A publication credential
  would be its own repository-scoped attestation behind the same kind of guards, and GhSim would have to model `workflows` and `pull_requests`
  permissions first, otherwise a write path would be tested against something more permissive than GitHub.
- The repository must already exist at the destination: creating it is the (still missing) multi-repository transfer.
- Not verified online: GitHub's behaviour for `head=owner:branch` with branch names that contain `/`, the contents API on very large or symlinked files, and
  rate limits. The reader treats every unexpected answer as unreadable or as "something else is there", never as absence.
- The plan is not persisted: it is cheap to make again and its hash says whether it is still the same plan. An audit of plans that were shown is not recorded.
- The original Azure triggers are still not converted; enabling them is a separate approval.

Alternatives considered: writing the branch and pull request now with the destination PAT (rejected: it would bypass the credential guards and a PAT with the
`workflow` scope is far broader than one repository); reusing the F-043 attestation (rejected: wrong permissions by design); a downloadable zip instead of
a plan (rejected: it cannot say what is in the way or bind to a base commit); persisting every plan (rejected for now: no consumer yet).
