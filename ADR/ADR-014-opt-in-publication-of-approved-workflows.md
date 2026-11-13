# ADR-014: an opt-in, allow-listed, ledgered publisher for approved workflows

Status: Accepted (implemented and exercised against the GitHub simulator only; OFF by default)

Context: ADR-013 plans the pull request and stops there, because the product has no GitHub write path and its write chain (F-040…F-097) is deliberately guarded. A person can
now follow the plan by hand; this ADR adds the one write path the AI-conversion feature needs, built so that every property of that guarded stance is kept, and records what
could not be.

Decision:
- **Off unless the operator turns it on, with a credential of its own.** `WorkflowPublication:*`: a GitHub App (id, installation, signing-key reference) and an allow-list of
  `org/repo`. Missing or partial configuration means disabled. The App must carry exactly Contents, Pull requests and Workflows write; the token minted for each publication is for
  one repository with exactly those permissions, is checked (a broader or different answer is refused), and is discarded. Nothing falls back to the read-only PAT, and the key
  reference may not be the destination connection's own (as in F-043). F-043 itself is untouched: its attestation stays Contents-only.
- **The person confirms the plan by its hash, and the plan is made again at execution.** `planSha256` covers the base commit, so "the base moved" is a different plan. Confirmation,
  the allow-list, a ready plan and a superadmin are all required before the first write.
- **The ledger is written before the write, and the database keeps it honest.** One live attempt per plan (partial unique index), states recorded before and after each write and only
  one step forward at a time, final states final, shape of path and branch constrained, no deletes. A crash or a lost answer leaves a row that says how far it got.
- **A write is never repeated, and uncertainty is never read as "nothing happened".** A definite refusal before any write frees the same plan; anything after a write, a 5xx, a lost answer or a
  read-back that fails marks `writesPossible`, blocks the plan, and leaves the destination to a person. The executor does not believe a writer that denies a write after the ledger recorded the
  branch as created; the database refuses a `Failed` row that claims no write after that point.
- **Read it all back.** The pull request must be open from our branch at our commit into our base, and the file at that commit must hash to the approved text. Never merge.
- **The writer is narrow by construction.** It writes one file named `.github/workflows/azdo-{n}-{name}.yml` on a branch `migrate/azdo-{n}-{hash8}` for text that hashes to the approved
  hash, and refuses anything else before it asks for a token.
- **The simulator models what the writer depends on.** GhSim now grants, asks for and reports `pull_requests` and `workflows` permissions and a `workflow` scope, and refuses a workflow file
  or a pull request without them. The refusal's status code and wording are an assumption.

Consequences:
- **Nothing here has met GitHub.** Not verified online: how github.com answers a workflow-file write without the permission, a write whose answer is lost, App-token permission rules, the
  Contents API for workflow files, and rate limits. Enabling it for a real repository should start with a sandbox repository and a throw-away App, with `credential-check` first.
- Only superadmins can publish; approval already required two people (four eyes), and publication is a third, explicit act on a hash.
- No automatic cleanup: a `writesPossible` failure leaves a branch (and possibly a file) that a person removes. A reconciliation view of what exists versus the ledger would help and is not built.
- The ledger and proposals tables are not in the restricted-runtime grants script (fails closed), as for the earlier AI tables.
- The original Azure triggers are not converted by a conversion: the published workflow is manual-only. They come back only as a separately approved derived proposal ([ADR-015](ADR-015-azure-triggers-as-a-separately-approved-derived-proposal.md)).
- The repository must already exist at the destination (creating it is the missing multi-repository transfer), and the App must be installed on it.

Alternatives considered: publishing with the destination PAT (rejected: far broader than one repository, and it would blur the credential separation the project insists on); reusing F-043
(rejected: wrong permissions by design); a pull request through `git push` of the sandbox runner (rejected for now: its fences are for migration pushes and a workflow-file push needs the same
extra permission anyway); automatic retry of an ambiguous write (rejected: a duplicated branch or pull request is worse than asking a person); merging on approval (rejected: a person reviews
the pull request, that is the point).
