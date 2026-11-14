# ADR-021: translate Azure's `batch` and `autoCancel` into a GitHub `concurrency` setting, as a choice

Status: Accepted (0.10.0). Replaces the "not translated" part of ADR-015.

Context: ADR-015 named `batch`, `autoCancel` and `drafts` of the Azure triggers as differences and translated none of them, on the grounds that each is a behaviour change a person should choose. Two things
turned out to be wrong with leaving it there.
1. **A default was being dropped without a word.** The Azure documentation (checked online on the pages for `trigger` and `pr`, not by running anything) says `autoCancel` defaults to **true** ("whether to cancel
   running PR builds when a new commit lands in the branch"), `drafts` to true and `batch` to false. The reader treated an absent `autoCancel` as false, so a pull request trigger that cancelled superseded builds
   in Azure produced no difference at all.
2. **GitHub can express two of the three.** `concurrency` (documented by GitHub) allows at most one running run per group, keeps one waiting run and by default replaces it with the newest, and lets
   `cancel-in-progress` be an expression: that is what `batch` is, and what `autoCancel` is. `drafts: false` cannot be a trigger option: the pull request event also fires for drafts, so skipping them needs a
   condition on every job, which the trigger step (it rewrites the `on:` entry and nothing else of an approved workflow) must not invent.

Decision:
- **The reader uses Azure's documented defaults** (batch false, autoCancel true, drafts true). An option that is not a plain `true`/`false` (a variable, an expression, anything else) is not guessed: the default
  is assumed and `TRIGGER_OPTION_UNREADABLE` says so (for `drafts`, nothing is said about drafts).
- **`batch: true` and `autoCancel` (explicit or default) become ONE top-level `concurrency` setting**, only for the triggers that exist (batch needs a CI trigger; autoCancel needs the pull request trigger) and
  scoped so that only the events they are about are ever queued or cancelled: pushes group by ref and wait for the run in progress; pull request runs group by pull request and cancel the run in progress;
  every other run (manual, scheduled) gets a group of its own (`run-<id>`) and is never queued or cancelled. The group is `${{ github.workflow }}-` followed by the expression for the events asked for;
  `cancel-in-progress` is `${{ github.event_name == 'pull_request' }}`, present only when pull requests cancel. The real actionlint and zizmor accept the generated text without findings.
- **They are approximations and say so.** `TRIGGER_BATCH_APPROXIMATED` (the run that is replaced while waiting is shown as cancelled; the run in progress is not cancelled) and
  `TRIGGER_AUTOCANCEL_APPROXIMATED` are warnings accepted by name at approval like every other difference. `TRIGGER_DRAFTS_NOT_MAPPED` stays, now with its reason and the condition to add by hand.
- **It is a choice, on by default.** `derive-triggers` takes an optional `{ translateConcurrency }` (default true). With false, nothing is added and batch and autoCancel are named as before
  (`TRIGGER_BATCH_NOT_MAPPED`, `TRIGGER_AUTOCANCEL_NOT_MAPPED`, now including the default autoCancel). The screen has the checkbox. A reviewer cannot remove the block from a proposal (approval binds to the text),
  so the choice is made when the proposal is derived.
- **The workflow's own concurrency setting always wins.** If the approved workflow already has a top-level `concurrency`, it is left byte for byte, nothing is added, and `TRIGGER_CONCURRENCY_EXISTS` says the Azure options
  were not added. The editor changes the `on:` entry and, only when asked and free, inserts the `concurrency` block right after it; every other line is returned byte for byte, and the result is parsed again to prove
  the block says exactly what was generated.

Consequences:
- A derived proposal with a pull request trigger now has a `concurrency` block by default (Azure's default autoCancel). Existing proposals are unchanged; deriving again gives another text and another hash.
- Not verified online: nothing here was run on GitHub. The documentation says what concurrency does; that a `batch` pipeline behaves identically (Azure collects "all changes not yet built" into one run; GitHub runs the
  newest commit, which contains them) and how the cancelled runs show up in checks is not observed.
- The concurrency group is per workflow name (`github.workflow`): two workflows in a repository with the same `name:` would share groups.

Alternatives considered: translating nothing (rejected: Azure's default autoCancel was being dropped silently); always translating (rejected for the reason in ADR-015, and a reviewer cannot take the block out of an
approved text); a workflow-level `if` for `drafts` (not an option on GitHub: `if` exists on jobs and steps); adding the job conditions to the approved workflow (rejected: the trigger step rewrites `on:` and nothing else);
one concurrency group for every event of the workflow (rejected: a scheduled or manual run would queue, or cancel, pushes that have nothing to do with it).
