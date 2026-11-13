# ADR-012: kept proposals and human approval of AI-converted workflows

Status: Accepted (kept proposals and approval implemented; pull-request publication is not)

Context: ADR-011 produces a *proposal*. Nothing a person could approve existed: the answer was returned and forgotten. The requirement
stands that AI never approves or publishes by itself, that a human approval and a pull request are needed, and that nothing claims an
equivalence nobody established.

Decision:
- **Keep what a model produced, and only that.** A proposal is stored when a model answered (`Proposed` or `Blocked`); nothing sent, or a failed
  request, is not. A `Blocked` answer is kept **without its text**: the answer the gates stopped may be exactly the credential they found, and nobody
  approves it. Gate messages name the problem and never carry the answer (the secret gate reports the *kind*, not the value).
- **Approval binds a person to one text.** The approver sends the SHA-256 of what they read; the server compares it with the stored text's hash, and
  the stored text with its own hash. A reviewer approves bytes, not "the latest proposal".
- **Approval does not trust the stored verdict.** The gates run again on the stored text with the current policy and analysers. A policy tightened,
  an analyser missing, or a catalog refreshed since the proposal can stop an old one. The SHA-256 of the fresh report goes into the decision record.
- **Warnings are accepted by name.** Every warning code of the fresh run must be acknowledged; the record lists the ones accepted (the server's, not
  the request's). Warnings are what a reviewer is there to weigh (for example `ACTION_PIN_NOT_VERIFIED`; real zizmor warns about secrets outside an
  environment on most workflows, so acknowledgement is the normal path, not an exception).
- **Four eyes by default.** Whoever asked for a conversion cannot approve it (`WorkflowApproval:RequireSecondApprover`, default true). A single-reviewer
  deployment switches it off explicitly; who decided is recorded either way. Reject and revoke need a reason.
- **The database holds the line too.** `workflow_proposals` rows cannot change text, hash, evidence or author; only `Proposed → Approved|Rejected` and
  `Approved → Revoked` exist; an approved row must carry the hash of its own text; `workflow_proposal_events` is append-only; nothing is deleted. The
  decision is one transaction (state compare-and-swap plus the event), so two simultaneous approvals record one.
- **One door to the approved text.** `RequireApprovedAsync` returns the workflow only for an `Approved` proposal whose approval hash is the text's
  hash. A later publisher must use it; it is the only code that answers "what exactly was approved".
- **Approved is not published, and not equivalent.** Approval creates no branch or pull request and does not re-enable the original triggers (the
  workflow stays `workflow_dispatch` only). Re-enabling them is a separate, later approval.

Consequences:
- Model-written workflow text derived from customer pipelines is now stored (`Proposed` and later states). There is no retention or deletion policy yet;
  the schema forbids deleting proposals on purpose, so a retention rule needs a reviewed migration.
- Four eyes means a lab with a single `superadmin` cannot approve its own conversions: create a second `admin`, or set
  `WORKFLOW_APPROVAL_REQUIRE_SECOND_APPROVER=false` for the sandbox.
- The approval re-runs actionlint/zizmor, so approval needs them on the API host (the image ships them; CI now installs them for the PostgreSQL
  integration job, where the approval tests fail, not skip, when they are missing).
- `workflow_proposals` and `workflow_proposal_events` are not in `script/postgres_runtime_identity_grants.sql`: a deployment that runs the API with the
  restricted runtime login would be denied access (fail closed) until a reviewed grant is added, as for `ai_connections`.
- The schema is created with `CREATE TABLE IF NOT EXISTS` plus constraints and triggers added idempotently, because on an empty database
  `EnsureCreated` builds the tables from the model first and the model carries no constraints. Both paths were exercised.
- No diff against the deterministic scaffold is recorded: the scaffold is a disabled draft and a diff against it says little. The reviewer reads the
  text, the findings, the plan and the provenance instead.
- Pull-request publication (needs the multi-repository executor, F-043/F-083 being single-repository) and the separate approval to re-enable the
  original triggers remain to be built. A read-only publication plan for an approved proposal exists since ADR-013.

Alternatives considered: approving a proposal id (rejected: the text must be what was read); trusting the stored gate report (rejected: policy,
catalog and analysers move); letting the author approve (rejected as default: a second person is the cheapest strong control); storing blocked
answers with their text (rejected: it can hold the very credential the gates stopped); a service-only state machine without database constraints
(rejected: the audit of who accepted which text should not depend on every writer being correct).
