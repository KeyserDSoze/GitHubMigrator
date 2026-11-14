# Live smoke tests (real Azure DevOps Services and GitHub)

The simulators ([ado-sim](ado-test-environment.md), [gh-sim](github-test-environment.md)) are written from documentation and memory: they are **not an oracle**. `tests/live/LiveSmoke.Tests` runs the product's own adapters against the real services and records, for every behaviour a simulator assumes, whether the real service agrees.

Everything against Azure DevOps and GitHub is **read-only**: all HTTP goes through `ReadOnlyGuardHandler`, which throws on anything but GET/HEAD, and the git fetch only reads. The one exception is the opt-in model check, which must send prompts: its `CompletionOnlyGuardHandler` refuses everything except `POST …/chat/completions`, and the prompts are fixed and secret-free. The project is not part of the solution test run; CI compiles it but never executes it. Tests without credentials are skipped.

## What it checks

| Area | Test | Real service behaviour confirmed |
| --- | --- | --- |
| Azure DevOps Services | validator | a valid PAT is accepted, a bogus one is rejected (the report names the code, e.g. a 203 sign-in page) |
| | discovery | projects, repositories, refs, Graph users/groups/memberships and ACLs are read; no credential appears in the inventory; the share of ACE principals that resolve through Graph `legacyDescriptor` |
| | git fetch | a real repository (smallest non-empty one up to 100 MB, or the one you name) becomes an exact mirror: refs equal the REST refs, `fsck --strict` passes |
| | pipelines | the first pipeline is read with variables, variable groups and service connections; the shape of `variableGroups` in the build definition |
| | contract probes | Graph field names (`legacyDescriptor`), continuation headers, membership edge shape, ACL shape and descriptor kinds |
| GitHub | validator and access | token, organization, membership and role, private-repository policy |
| | missing repository | a missing name is `NotVisibleOrMissing` |
| | repository probes (with `..._GH_REPO`) | an absent commit is "not found" although GitHub answers 422; refs and annotated tag objects; rulesets; Actions secret/variable **names** |
| | publication reader (with `..._GH_REPO`) | the read-only publication plan's reads: default-branch tip, an absent workflow path (contents 404), an absent branch whose name contains `/` (git/ref 404), an empty `head=owner:branch` pull request list, a missing repository / base branch |

| | reconciliation readers (with `..._GH_REPO`) | what the reconciliation view and the plan's earlier-pull-request check read, GET only: an attempt of which nothing exists (no branch with a `/` in its name, an empty pull request list for `head=owner:branch` in any state, no file), and `GET /pulls/{number}` answering 404 for a number that is not there. With `GITHUBMIGRATOR_LIVE_GH_PULL=<number>:<head branch>` (a pull request in the sandbox repository, any state): how a real pull request is read by number and by head, and that another branch name is not taken for it |
| AI model (opt-in, **sends prompts**) | ping | the product client gets an answer from the configured endpoint; which of `max_tokens` / `max_completion_tokens` it accepts |
| | rejections | a bogus key, an unknown model and a ~1.2 MB input are classified `Unauthorized`, `ModelNotFound`, `ContextLengthExceeded` as LlmSim assumes (with the real status and code in the report) |
| | product converter | the product's own converter (prompt, redaction, gates, one repair) on three tiny secret-free sample pipelines: how many reach `ReadyForReview` and what blocks the rest (a measurement, not a pass/fail) |

The fidelity report (`live-smoke-report.json`) has one entry per finding: `confirmed` (the simulator is right), `differs` (**fix the simulator or the product**) or `info`. Scopes the token lacks show up as `info`, not as failures.

## Running it

Create **throw-away read-only tokens** and keep them out of shell history and CI logs.

Azure DevOps Services PAT scopes: Project and Team (Read), Code (Read), Graph (Read), Identity (Read), Build (Read), Variable Groups (Read), Service Connections (Read). The ACL probes also need Security (Manage), a high-privilege scope: leave it out and that probe reports `info`.
GitHub: a classic token with `repo` and `read:org` (or a fine-grained one with read access to the organization, Metadata, Contents and, for rulesets and Actions names, Administration/Secrets/Variables read).

```bash
export GITHUBMIGRATOR_LIVE_ADO_ORG=my-org            # organization name, not a URL
export GITHUBMIGRATOR_LIVE_ADO_PAT=...
export GITHUBMIGRATOR_LIVE_ADO_PROJECT=MyProject     # optional
export GITHUBMIGRATOR_LIVE_ADO_REPO=my-repo          # optional: the repository to mirror (pick one with annotated tags)
export GITHUBMIGRATOR_LIVE_GH_ORG=my-github-org      # optional
export GITHUBMIGRATOR_LIVE_GH_PAT=...
export GITHUBMIGRATOR_LIVE_GH_REPO=some-repo         # optional: enables the repository-level probes
./script/live-smoke.sh
```

The model checks need an endpoint you are happy to send a few thousand tokens of fixed, secret-free prompts to (they go through `CompletionOnlyGuardHandler`: only `POST …/chat/completions`, never anything else):

```bash
export GITHUBMIGRATOR_LIVE_AI_ENDPOINT=https://api.openai.com/v1   # or https://<resource>.openai.azure.com, http://localhost:11434/v1, …
export GITHUBMIGRATOR_LIVE_AI_MODEL=gpt-4.1                          # model id, or the Azure deployment name
export GITHUBMIGRATOR_LIVE_AI_KEY=...                                # not needed with GITHUBMIGRATOR_LIVE_AI_AUTH=None (local servers)
export GITHUBMIGRATOR_LIVE_AI_PROVIDER=OpenAICompatible             # or AzureOpenAI (+ GITHUBMIGRATOR_LIVE_AI_API_VERSION for the deployment route)
```

Set `GITHUBMIGRATOR_ACTIONLINT` / `GITHUBMIGRATOR_ZIZMOR` (or put them on `PATH`) to have the last check run the real analysers.

PowerShell: set the same variables with `$env:NAME = '...'` and run `dotnet test tests/live/LiveSmoke.Tests`. `GITHUBMIGRATOR_LIVE_REPORT` changes the report path.

## What a real run of the publisher would still have to confirm (not built into the smoke test: it writes)

The smoke tests above only read. The publisher (ADR-014/016/019) writes, so it is **not** part of them and has not been run against github.com. It was tested against the GitHub simulator, which is written from
documentation. These are the things its behaviour depends on that nobody has seen GitHub do, in the order to find out. Use a throw-away organization and **one** sandbox repository you can delete, and a throw-away
GitHub App installed on that repository only, with exactly Contents, Pull requests and Workflows (write). Credentials go in the operator's secret store, never in chat, a shell history or a CI log.

| # | Step (in the product, with the sandbox repository allow-listed) | What it would confirm | Today |
| --- | --- | --- | --- |
| 1 | `POST …/publication/credential-check` | the App token can be minted with exactly those three permissions, and nothing broader is accepted | simulator only |
| 2 | Plan, then publish a conversion | a branch, one file under `.github/workflows/` and a pull request are created with **App** permissions (GitHub's refusal of a workflow file without *Workflows: write* is only assumed: status and wording) | simulator only |
| 3 | Reconcile the attempt | `head=owner:branch` finds the pull request when the branch name contains `/`; the pull request list carries `merged_at` for a merged one; the single-pull-request read | the read-only test above, with `..._GH_PULL`, covers the reads |
| 4 | Merge it by hand, derive the Triggers proposal, approve it, plan, publish | a replacement is one `PUT` with the blob `sha` and answers **200** for an App token (documented for tokens; App behaviour not seen) | simulator only |
| 5 | Plan a Triggers proposal while the conversion's pull request is still open | `EarlierPullRequestOpen` against a real open pull request | simulator only |
| 6 | Make a write fail halfway (revoke the App's Workflows permission between branch and file), then reconcile and release | what GitHub answers when a write is refused or its answer is lost, and that the product's ledger, reconciliation and release say the true thing about what is left | simulator only |
| 7 | Delete the leftover branch by hand, then release | how soon a deleted branch disappears from the read (the release is refused while it is visible) | simulator only |

Also never seen on GitHub: the label case-sensitivity for `runs-on`, and the **meaning** of the mapped Azure triggers (the ten Orion goldens were never executed on GitHub: whether they run as an Azure pipeline did
is the first thing a real pilot would measure). A `differs` in any of these is a product or simulator bug to fix with a test, as above.

## Dry run

`./script/live-smoke.sh dry-run` also starts llm-sim for the model checks. It starts ado-sim (`ADO_SIM_FLAVOR=services`, orion estate) and gh-sim and points the very same tests at them through `GITHUBMIGRATOR_LIVE_ADO_SIM_ORIGIN` / `GITHUBMIGRATOR_LIVE_GH_SIM_ORIGIN`. It proves the test logic works (it already found two defects in the tests themselves); the report says `target: SIMULATORS` and **says nothing about the real services**.

## What to do with a `differs`

1. Decide who is wrong: the simulator (fix it and its contract tests) or the product (fix it with a test; the two defects found so far, `422 No commit found for SHA` and `variableGroups` as objects, were product bugs).
2. Re-run the dry run, then the live run.

Not covered here by design: writes of any kind to GitHub (the guarded push is exercised against gh-sim only), Azure Boards, and organization-scale discovery limits.
