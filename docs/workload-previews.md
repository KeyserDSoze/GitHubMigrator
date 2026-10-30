# F-009/F-010 — Pipeline and Boards migration review workbench

Status: **read-only analysis and review previews**, not automated deployment.

## F-009 Azure Pipelines → GitHub Actions

The **Pipelines → Actions** UI takes a saved Azure DevOps source connection, verified GitHub destination connection, Azure project ID, pipeline ID and proposed target repository. The backend reads pipeline/build definition metadata, pipeline and variable-group names, and project-level service endpoint names through Azure DevOps REST. Existing GitHub Actions repository secret and variable *names* are enumerated with bounded pagination. It generates a deliberately **non-executing** workflow candidate at `.github/workflows/azdo-{pipelineId}-{safe-name}.yml`, and a checklist of manual prerequisites.

When the source repository YAML can be read as JSON from Azure DevOps Git Items API (`$format=json&includeContent=true`), a bounded YamlDotNet parser extracts task type identifiers and step kinds. It **does not return scripts, inputs, source YAML text, variable values, task arguments or credentials**. Each identified step appears as a disabled TODO in the suggested GitHub Actions file and a translation warning. Unsupported/classic pipelines, templates, scripts and complex conditions **still require a full manual or future rule-based converter**; this is **not** a functioning conversion.

If an Azure AI Foundry / Azure OpenAI deployment is configured using `Ai:AzureOpenAiEndpoint` (`https://*.openai.azure.com`) and `Ai:Deployment`, the API sends only task identifiers, service connection *types*, counts and a disabled candidate workflow for advisory AI review, using managed identity. The model's output is a checklist, **not code executed or automatically published**. AI is optional and failure is explicitly visible. Untrusted source YAML and credentials never enter the AI prompt.

### Configuration and secret safety

- Azure DevOps metadata may disclose *names*, `isSecret` flags, and some nonsecret values. The adapter retains **names and secret flags only**. This is intentionally conservative: values labeled nonsensitive may still contain credentials.
- GitHub REST APIs permit listing repository secret **names** but do not disclose existing secret values. A name matching an Azure variable does **not** prove value correctness. Consult [GitHub Actions secret API](https://docs.github.com/en/rest/actions/secrets).
- Service endpoint listing is **project scoped**. The checklist treats every returned connection as a **candidate**, never proof the specific pipeline uses it. Credentials and authorization parameters are ignored.
- For cloud deployments, favor GitHub OIDC + trust policy and least-privilege cloud roles. Where credentials must be recreated, the checklist explains GitHub **Settings → Secrets and variables → Actions**; no secret value is copied.
- An explicit, audited approval workflow with write-scoped GitHub credentials, CI validation and publish/rollback is still **not implemented**. No GitHub API PUT/POST/DELETE is called by this workbench.

API: `POST /api/workloads/pipelines/preview`, admin/superadmin only, with `sourceConnectionId`, `destinationConnectionId`, `projectId`, `pipelineId`, `targetRepository`. Returns `PipelineBlueprint` with workflow path, disabled YAML scaffold, setup steps, blockers and AI status.

## F-010 Azure Boards → GitHub Issues

The **Boards → Issues** UI accepts a source connection, project ID and up to 100 Azure work item IDs. `POST /api/workloads/boards/preview` fetches direct work item relations and returns proposed issue bodies with an HTML comment stable marker:

```text
<!-- azdo-workitem:{organization}:{projectId}:{workItemId} -->
```

The Azure ID, original type/state and source URL remain in the body; original IDs must not be claimed as GitHub issue **numbers**, because GitHub allocates them. Work-item hierarchy/relations, source commit SHAs and PR artifact links are preserved as traceability evidence. Missing ID mappings, unresolvable SHAs, unsupported artifacts and unverified PRs are warnings, not assumed successes.

A separate read-only **Check target links** action calls `POST /api/workloads/boards/reconcile` with source/destination connection IDs, project ID, target repository, source work item IDs, and optional mapping dictionaries:

```json
{
  "sourceConnectionId": "11111111-1111-1111-1111-111111111111",
  "destinationConnectionId": "22222222-2222-2222-2222-222222222222",
  "projectId": "project-guid",
  "targetRepository": "repository",
  "workItemIds": [101, 102],
  "knownGitHubIssues": { "102": 55 },
  "knownGitHubCommits": { "aaaabbbbccccddddeeeeffff0000111122223333": "aaaabbbbccccddddeeeeffff0000111122223333" },
  "knownGitHubPullRequests": { "301": 22 }
}
```

GitHub `GET /repos/{org}/{repo}/issues/{number}`, `commits/{sha}`, and `pulls/{number}` are queried using the saved verified destination connection token. A target object is classified as **TargetExists**, **MissingMapping**, **NotFound**, **Unavailable**, **Unsupported** or **NotChecked** (bounded 120 provider probes per call). A GitHub PR is not mistaken for an issue. Coverage reports distinguish missing input mappings from provider errors and unavailable objects.

**Critical distinction:** TargetExists only proves the **object exists**. It does **not** mean a GitHub Issue already references that commit/PR or that Azure Boards hierarchy and all reverse references were reconstructed. Work-item relations are direct-only; commit messages, closed/completed pull request metadata, TFVC changesets, indirect relations and Git history scans are not fully covered. `readyToPublish=false` by design.

## Next safe increments

1. Pipeline: validated rule converter for supported YAML tasks, Azure AI review of redacted structured plan, user edit/approval, create a PR containing workflow proposal, plus explicit secrets/OIDC setup checks.
2. Boards: durable idempotent source-ID → GitHub issue-number map, issue creation after user approval, second-pass patching of issue bodies to link mapped IDs/commits/PRs, and a full reconciliation report.
3. Large jobs: durable queue with paging, backoff/retry, checkpoints, error classification, migration audit and replay safety.
4. Provider permissions: narrow-scoped credentials, documented grants, and real Azure DevOps/GitHub integration smoke tests before making production readiness claims.

API endpoints currently require logged-in admin/superadmin with changed initial password; provider reads use fixed Microsoft/GitHub hostnames, no redirects/cookies and bounded responses. Tests with fake provider responses verify the core behavior. Full production integration remains pending.
