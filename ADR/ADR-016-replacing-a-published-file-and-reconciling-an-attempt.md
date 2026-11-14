# ADR-016: replacing a file the product published earlier, and reconciling an attempt with the destination

Status: Accepted (exercised against the GitHub simulator and scripted answers; only GitHub's documented contract for replacing a file was checked online)

Context: ADR-015 brings the Azure triggers back as a separately approved proposal, but a derived proposal could be published only on a free path: once the manual-only conversion had been
published and merged, the plan said `WorkflowPathOccupied` and a person had to carry the change by hand. ADR-014 also promised that the writer "never updates". And the ledger could say that an
attempt "may have written", but nothing let a person see what was actually left at the destination.

Decision, replacing a file:
- **Only one text may be replaced: the approved text of the parent conversion.** A derived (Triggers) proposal names it; the reader is told its hash and reports `PriorVersion` when the file at
  the planned base commit is exactly that text (line endings ignored) and the destination's blob id is the git id of the bytes that were read. The parent must still be approved and be a
  conversion of the same pipeline: once it is revoked, or when anything else is at the path, the file is somebody else's and the plan says `WorkflowPathOccupied` as before. A conversion never
  replaces anything.
- **The plan says so and the person confirms it.** `mode: Update`, `replacesBlobSha`, `replacesWorkflowSha256`, a `WorkflowWillBeReplaced` Info finding, a title, commit message and pull request
  description that say "replaces" (the pull request shows the diff), a `Replace` step for the manual path, and a confirmation sentence that names the replacement. `planSha256` covers the mode, the
  blob and the replaced text, so a file edited after the plan was shown makes the confirmation stale (`PublicationPlanChanged`). A plan that creates a file hashes exactly what it always did
  (a test recomputes it).
- **The writer replaces on the new branch only.** It re-reads, with its own credential, the file at the planned base commit and requires the planned blob id and the planned text
  (`PublicationWorkflowPathChanged`, nothing written, otherwise); creates the branch; sends the one `PUT` with `sha` = the blob being replaced and `branch` = the new branch; expects **200**
  (GitHub's documented answer for a replacement; 201 is for a created file) and treats any other success as "written in a way that was not planned", which blocks the plan like any write whose
  outcome is uncertain; then opens the pull request and reads everything back as before. The base branch is never written. It still never merges, never deletes, never force-pushes and never
  repeats a write. A `Validate` step refuses a plan that does not name both the blob and the text it replaces (or names them for a creation), or that "replaces" a file with itself.
- **The ledger keeps what was replaced.** `workflow_publications.mode`, `replaced_blob_sha`, `replaced_workflow_sha256`: immutable, constrained (an update names both, a creation neither, the
  replaced text is another text), `Create` by default so earlier rows are creations (a 0.6.0 schema was upgraded twice in a scratch database: the existing row became a creation, both constraints existed, the guard refused a change of mode).

Decision, reconciliation:
- **`POST …/proposals/{id}/publications/{publicationId}/reconcile`** (admin or superadmin, rate limited) reads the destination for one recorded attempt, with the destination connection's
  read-only credential (never the publishing App's), **GET requests only**, inside the one repository. The connection must be a verified GitHub destination for the organization the attempt wrote to
  (`DestinationMismatch` otherwise). It changes nothing at the destination and nothing in the ledger, and persists nothing: it is a view.
- **What it reads:** the repository (and whether it is archived), the base branch tip now, the attempt's branch, every pull request from that branch in any state plus the number the ledger
  recorded (read directly when the list does not show it), and the file at the workflow path on the branch and on the base, classified as the approved text, the text that was replaced, something
  else, or nothing. File texts are compared inside the reader and never returned.
- **What it says:** one verdict — `OpenPullRequest`, `OpenPullRequestChanged`, `Merged`, `MergedBaseDiffers`, `ClosedWithoutMerge`, `BranchWithoutPullRequest`, `AlreadyOnBase`, `NothingFound`,
  `TargetUnreadable`, `RepositoryNotFound` — and findings that set it beside the ledger: `BranchMoved`, `BaseMoved`, `PullRequestMissing`, `MultiplePullRequests`, `OtherPullRequests`,
  `AttemptNotFinished`, `UnexpectedObjects` (the ledger says nothing was written but something with this attempt's name exists), `ProductWillNotRetry`, `LedgerStillBlocksPlan`.
  An unreadable destination is a verdict, never a clean result; a merged pull request is `Merged` only if the base has the approved text.
- **No action.** The product does not clean up, retry, close, delete or release anything on the strength of a reconciliation: a person does, and the screen says what the product will not do.

Consequences:
- **Verified online (GitHub's documentation, not a live call):** `PUT /repos/{owner}/{repo}/contents/{path}` answers 201 for a created file and 200 for a replaced one, requires the blob SHA of the
  file being replaced for an update, answers 409 on a conflict, and needs the `workflow` scope for a token under `.github/workflows` (that page says nothing about a GitHub App's permission: that part stays the ADR-014 assumption). **Not verified:** any of it against github.com
  (nothing was run there), GitHub's wording and status for a refused workflow write, `head=owner:branch` with `/` in the branch name (the plan reader already assumes it), that the list of pull
  requests carries `merged_at` for a merged one (the single-pull-request answer carries `merged`; both are read), and App-token behaviour for an update.
- **A dead end that was only described (resolved in [ADR-019](ADR-019-earlier-pull-requests-and-releasing-a-blocked-plan.md)).** An attempt that failed with `writesPossible` keeps that exact plan
  blocked in the ledger even after a person removes what it left. Since 0.9.0 a superadmin can release it after the product has read the destination again; the plan says `AttemptBlocksPlan` meanwhile.
- **A pull request still open for the parent (resolved in ADR-019).** A derived proposal planned while the pull request of an earlier publication of the same file is open is now blocked
  (`EarlierPullRequestOpen`) until that pull request is merged or closed.
- Updating is for the one case "triggers over the file this product put there". Replacing a file after a *re*-conversion (another approved conversion of the same pipeline) is not covered and stays
  `WorkflowPathOccupied`.

Alternatives considered: replacing whatever file is at the path when a person confirms (rejected: the confirmation would be for a file the product cannot describe, and a person's edit would be
lost); committing to the base branch (rejected: the pull request is the review); remembering reconciliations in a table (rejected for now: a view of "now" is what a person needs, and a history of
reads would suggest they are decisions); letting a reconciliation release a blocked plan (deferred: it needs a new, audited transition on a state that is final today).
