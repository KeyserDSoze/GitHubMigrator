# What converting Orion by hand showed about the gates

Written while authoring the corpus. "Fixed" means a change to product code with tests in this same change.

## Found and fixed

| finding | where | change |
| --- | --- | --- |
| `publishFeedCredentials: orion-nuget-feed` was masked as a credential, although it is a *service connection name*: the conversion loses exactly what it needs to map to secrets | `SourceRedactor` (`SENSITIVE_LITERAL`) | keys that hold connection names and selector keys (`...Type/Scheme/Method/Mode/Selector`) are not masked; similar keys that hold credentials still are |
| `/d:sonar.token="$SONAR_TOKEN"` was reported `LITERAL_SECRET` in a generated workflow: a shell variable is a reference | `SecretPatterns` | after `=` a pure `$NAME` / `${NAME}` is a reference; after `:` (a YAML value, expanded by nothing) it stays a literal |
| `Password=${DB_PASSWORD}` / `Password=$(Var)` in a connection string and `https://u:$(Token)@host` were masked | `SecretPatterns` | syntactic references (`$(x)`, `${x}`, `$env:x`, `%x%`) are never credentials |
| `Authorization: Basic <base64>` / `Bearer <opaque>` was **not** masked (the scheme word split the value): a real redaction gap | `SecretPatterns` | new `AUTHORIZATION_HEADER` pattern; references after the scheme are kept |
| a credential found under a key was masked only there: `password: x` plus a later `echo x` leaked the key-less copy (found by the end-to-end test against the Azure stub) | `SourceRedactor` | every other occurrence of a value the scanner identified is masked too, with the same placeholder |
| **actionlint rejects custom self-hosted runner labels** (`runs-on: [self-hosted, linux, docker, npm]` → `runner-label`): the source (2003) demands agents with `docker` and `npm`, which could only be dropped | `ProcessWorkflowToolRunner`, `AzureRunnerLabels` (ADR-017) | the product reads the plain names a self-hosted pool demands from the pipeline YAML, stores them in the plan, names them to the model and writes them into an actionlint config of its own; 2003 now keeps `[self-hosted, linux, docker, npm]`. A model cannot add a label. actionlint compares the spelling exactly |
| **`AzureYamlTaskAnalyzer` did not see tasks inside conditional insertions** (3001's `AzureCLI@2`), counted `template` only in `steps`, and ignored `download:`/`publish:` | `AzureYamlTaskAnalyzer` (ADR-017) | insertions are followed, templates counted in jobs/stages/variables/`extends`, shortcuts listed, computed steps reported as unknown; the corpus test now requires the analyser to see every task the text scan sees. The 2001 and 2003 sidecars list the variable templates |
| deeply nested YAML (`steps: [[[[…]]]]`, ~50 KB) **overflows the stack** inside YamlDotNet and ends the process: found by the robustness test written for the analyser; every parse of untrusted YAML was exposed, including a model's answer | `SafeYaml` (ADR-018) | the depth is measured on the parser's event stream and a document nested deeper than 100 is refused before any node is built; `YAML_TOO_DEEP` for a workflow, "unreadable" for a source |

## Found and not fixed (decisions for a human)

(Items 1 and 4 of the first version of this list were fixed in 0.8 and moved up: see the table above and ADR-017.)

2. **`ZIZMOR_SECRETS_OUTSIDE_ENV` (Medium) on eight of ten workflows.** True and useful (repository-level secrets, no
   environment), but the source has the same property because Azure variable groups are not environment-scoped. Accepted per
   workflow with the reason; moving secrets into environments adds approvals the source does not have.
3. **Container images must be pinned by digest** (zizmor `unpinned-images`, High). 2005 used `dotnet/sdk:8.0` and `redis:7`;
   they are pinned now, which freezes them (security update lag). Digests are in `ACTION_PINS.md`.
5. **Approvals, branch checks and environment protections** (Azure environments prod, infra-prod, staging) live outside the YAML; the
   workflows reference GitHub environments of the same names and every sidecar says so. Nothing creates them.
6. **Tool assumptions the gates cannot see**: trivy on the runner (2001), a signing certificate in the store (2005), the
   quality-gate report path (2002), `webapps-deploy` runtime stack (2001). Listed in `notes`.
7. The unaccepted-warning rule counts zizmor Medium as a warning; Low and Informational findings (`concurrency-limits`,
   `anonymous-definition`, `undocumented-permissions`, `use-trusted-publishing`) are information only.

## Verified, and how

- actionlint 1.7.12 and zizmor 1.32.0 (`--offline --no-ignores --no-config --persona auditor`) report no blocker on any workflow.
- Every action input name used was read from the `action.yml` at the pinned commit; every commit SHA and image digest was resolved
  from the upstream repository/registry the day the corpus was written.
- Not verified: behaviour on GitHub, that tags have not moved since, vulnerabilities of the pinned versions.
