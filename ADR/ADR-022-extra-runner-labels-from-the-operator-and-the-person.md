# ADR-022: extra runner labels, from the operator and from the person asking

Status: Accepted (0.10.0). Extends ADR-017, which took runner labels only from the pipeline's own `pool: demands`.

Context: a pool's demands are Azure capability names (`docker`, `npm`). The labels the destination's runners really carry, in GitHub, are the destination's business and are often not in the pipeline at all
(`gpu`, `linux-x64`, an organization's own naming). actionlint accepts only GitHub's labels plus those configured, so a workflow written for such a runner was refused as `ACTIONLINT_RUNNER_LABEL` however
right it was, and the only way round was to edit the text by hand after the conversion.

Decision:
- **Two more sources of labels, both plain names validated by the same rule as demands** (`AzureRunnerLabels.Parse`: a plain name of up to 50 characters, letters, digits, dot, dash, underscore; labels GitHub or every
  self-hosted runner already has are not needed and not an error; at most 16 in all). (1) The **operator**: `WorkflowConversion:RunnerLabels` (`WORKFLOW_CONVERSION_RUNNER_LABELS` in Compose), allowed for every
  conversion; an entry that is not a plain name is ignored and **logged by name at start**, so a typo narrows what conversions may use, visibly, and never widens it. (2) The **person asking**: `runnerLabels` in the
  `convert` request, for that conversion only; a label that is not a plain name, or more than the limit together with the operator's, is refused (`400 InvalidRunnerLabel`) **before anything is read or sent**: nothing
  is dropped silently.
- **They are stored and told apart.** The plan has `ExtraRunnerLabels` beside `RunnerLabels` (what the pipeline demanded): the model is told in a separate line ("ADDITIONAL RUNNER LABELS the destination's runners
  are said to have … not demanded by the pipeline"), the plan notes say who allowed or gave them, and the plan hash covers them (a plan without them hashes as before). The gates take the union: actionlint
  is configured with both, at conversion, at approval and when the Azure triggers are derived, from the stored plan, so what was approved is judged as it was proposed.
- **A label is a claim, not a runner.** Nothing creates a runner or checks that one exists. The `SELF_HOSTED_RUNNER` warning is unchanged and is still a person's to accept; the screen says the extra labels are "said"
  to exist. Labels beyond GitHub's are still refused by actionlint unless they are in the plan: a model cannot add one.
- **The screen.** A field on the conversion for the labels of this conversion, an explanation when one is refused, and the extra labels shown with the plan apart from the demanded ones.

Consequences:
- A person with the right to convert (admin or superadmin) can allow a label for a conversion. The only thing a label does is pass one lint check, and an approver still sees it in the plan and the warning; if
  that is too permissive for a deployment, the operator list is the place to be strict, and the field can be removed from the request path later without changing the plan format.
- Case matters: actionlint compares spelling exactly, so `GPU` and `gpu` are the same label in the plan (deduplicated case-insensitively, the first spelling kept) and the workflow must use that spelling.
- Not verified online: whether GitHub itself treats runner labels case-insensitively when it picks a runner (documented behaviour not checked; the plan keeps one spelling).

Alternatives considered: one global list only (rejected: the labels differ by pipeline and by destination); per-plan only (rejected: an organization's standard labels would be typed every time); a free-text label with
spaces or operators (rejected: Azure demand expressions are not labels); letting the model propose labels (rejected: a model must not widen what a gate accepts); ignoring actionlint's runner-label kind (rejected
in ADR-017 and still: it would accept any label).
