# ADR-020: a plan may replace a text this product published, while it still stands behind it

Status: Accepted (0.10.0). Extends ADR-016, which allowed only one replaceable text.

Context: ADR-016 let a derived Triggers proposal replace exactly one text on the base branch: the approved text of the conversion it was derived from. A *re-conversion* of the same pipeline (another
approved conversion, with another text, for the same workflow path) therefore met the file its predecessor put there as `WorkflowPathOccupied`, and so did a Triggers proposal after the
file had already been replaced once. The only way forward was by hand.

Decision:
- **What a plan names as replaceable is a small set of texts, decided by the product, never by the destination.** (1) For a Triggers proposal, the approved text of its parent conversion, while that
  approval stands and for the same pipeline (unchanged: it also covers a file a person put there by hand following the plan). (2) A text the **ledger** says this product published to this very path in
  this very repository (state `Published`, organization and repository compared case-insensitively), **only while the approval of the proposal that published it still stands, for the same pipeline
  (source connection, project and pipeline id) and for exactly that text**. At most five such texts are named, the newest first, after the parent's, each once.
- **The reader names which one it found.** `IGitHubPublicationReader.ReadAsync` takes the set. A file is `PriorVersion` only if its text is exactly one of them *and* the blob id GitHub reports is the git id of
  the bytes that were read (unchanged from ADR-016); the target now also carries `WorkflowFileSha256`, the hash of that text. The planner checks the reader's claim against the set again.
  A re-conversion is then `mode: Update` with the blob and the replaced text named, so the writer, the ledger row and `planSha256` are exactly those of ADR-016.
- **A re-conversion is described as one.** Title `Replace the GitHub Actions workflow for Azure pipeline #N (…) with a new conversion`, a commit message that names the replaced hash, and a body that says
  the file is replaced, that whatever the earlier version had and this one does not (automatic triggers, for example) is gone, and that this conversion runs only when started by hand. The plan finding
  `WorkflowWillBeReplaced` and the screen say the same.
- **Everything else is somebody's file.** A file a person wrote, edited after the product published it, or put at the path by hand for a conversion is `WorkflowPathOccupied`, as before. A text whose approval was
  withdrawn is **not** named even if the ledger shows the product published it: the product replaces only what it still stands behind. A text another pipeline (or another project, or another Azure organization)
  published to a path that happens to be the same is not named. The writer, the executor, the ledger constraints and the base branch are untouched: it is still a new branch, one file, one pull request, no merge.

Consequences:
- **A behaviour that looks like a regression and is not:** a revoked conversion can no longer be replaced by the product even after the operator wants it gone; the operator replaces it by hand, as before this record.
  Allowing it would have let the ledger vouch for a text the product no longer vouches for.
- A plan reads one proposal per distinct published text (at most five): the cost of the rule, bounded.
- The ledger is the product's memory of what it wrote; a file the product wrote before the ledger existed (the manual flow of ADR-013) is replaceable only as the parent's text of a Triggers proposal.
- Not verified online: nothing new. The replacement is the `PUT` of ADR-016 (documented answer 200 with the blob `sha`); no run against github.com.

Alternatives considered: replacing any file whose hash matches *any* approved proposal of the pipeline (rejected: nothing says the product put it there, and a person's copy of the same text would be "ours");
requiring the old approval to be withdrawn first (rejected: replacing is the reason to supersede, and nothing forces an operator to revoke); a path-wide "the product owns this file" flag (rejected: it would stay true after a
person edits the file).
