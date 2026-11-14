# ADR-023: reading the templates a pipeline includes

Status: Accepted (0.11.0). Extends ADR-017, which counted `template:` and told the model it could not see the file; and the redaction and per-connection consent of the converter (ADR-011), which it keeps.

Context: a real Azure pipeline is often a few lines that include the work: step, job, stage and variable templates of its own repository, and of a shared repository declared under `resources.repositories`. Four of the ten
pipelines of the Orion estate are like that. Until now the model was given only the root file and the sentence "TEMPLATES you cannot see", so what a template did was marked manual and its tasks were not in the inventory
at all: the conversion could be "complete" and miss most of the pipeline. Reading more files means sending more of the customer's source to a model, so the question is how to do it without widening what leaves the
estate by default or by surprise.

Decision:
- **Opt-in, per conversion.** `includeTemplates` in the `convert` request (default false; a checkbox on the screen, unchecked). Without it nothing about templates changes: no extra read, the same prompt, the same
  proposal. It is a per-request choice because it widens what is sent; the per-connection consent to send source (`SendSourceContent`) is still required first and is checked before any template is read.
- **Only what the pipeline's own text points at, in the same organization.** `PipelineTemplates` finds `template:` values (plain strings, in document order) and the `repositories` resources; it reads (1) local
  templates, relative to the file that includes them, or rooted when the path starts with `/`; (2) `template: path@alias` where the alias is a **`type: git`** repository resource, read in the organization of the
  source connection with the connection's own credential, at the resource's `ref` (a branch or a tag; none means the default branch). `@self` is the pipeline's repository. A repository name is `Repository` (the
  pipeline's project) or `Project/Repository`, plain names only.
- **A path never leaves its repository and is never computed.** A path is plain characters (letters, digits, `. _ - /` and space), is normalized (`.`/`..`), a `..` above the root is refused, it names a `.yml` or
  `.yaml` file, and any `${{ }}`, `$( )` or `$[ ]` makes it unresolved: the product does not evaluate Azure expressions, so it does not guess a path from one. A `github`/`bitbucket`/other repository type, an
  untyped resource (Azure requires a type), an undeclared alias and a ref that is not `refs/heads/…`, `refs/tags/…` or a plain name (for example `refs/pull/…`) are unresolved with a reason, never read.
- **Bounded.** At most 12 files, depth 3 (a template that includes a template that includes one), 100 KB per file, 100 KB in all together with the pipeline (a share of what the model's endpoint may be sent), 30 read
  attempts (failed ones count), each file once however it is spelled. Whatever is left out is listed with a stable reason (`ExpressionPath`, `PathNotSupported`, `NotYaml`, `EscapesRepository`,
  `UnknownRepositoryAlias`, `RepositoryTypeNotSupported`, `RepositoryNameNotSupported`, `RefNotSupported`, `NotReadable`, `TooLarge`, `RedactionBlocked`, `TooMany`, `TooDeep`, `BudgetExceeded`,
  `SourceDoesNotReadFiles`, `RootLocationUnknown`), shown to the person and, for the model, as "TEMPLATES you cannot see (reference (reason))".
- **Each file is redacted on its own, and one that cannot be redacted is not sent.** The same redactor as for the pipeline: a file with something that must never be sent (a private key) is `RedactionBlocked`, a
  truncated one is `TooLarge`; neither is sent in part. The redaction counts of all files are added to the proposal's, the placeholders are the redactor's.
- **Data between markers, after the pipeline.** Each template is a block `[[TEMPLATE-{nonce} repository=".." path=".." ref=".."]] … [[/TEMPLATE-{nonce}]]` after the source block, with the same nonce, which is chosen so
  that it is in none of the texts. The model is told to convert the templates where they are used, to substitute the `parameters:` they are given, to say in the Notes what it assumed about a value it cannot see, and
  to account for their tasks. That is a mitigation, not a guarantee: the answer is gated like any other.
- **The tasks are the union.** The task inventory the workflow must account for (`TaskAccountingGate`) is the pipeline's plus every sent template's, found in the raw text (names, never values), and it is stored with
  the proposal so approval and the derivation of the triggers gate the kept text against the same set. A task only a template uses and the model forgets is `TASK_NOT_ACCOUNTED`. The plan's service-connection scan
  and runner labels also cover the template texts: a connection or a `pool.demands` named only in a template is the pipeline's too.
- **What was sent is on record, without the text.** The proposal's provenance keeps `Templates` (repository, path, ref, SHA-256 of the redacted text, bytes) and `UnresolvedTemplates` (reference, reason); never a
  template's text. The screen lists both, with the reason in words. A proposal from before this version has neither.
- **Read only.** One `GET …/git/repositories/{repository}/items?path=…&includeContent=true[&versionDescriptor…]` per file, by `IRepositoryFileReader`, through the same client and credential as the rest of the
  source reads; nothing is written, and no path is taken from anywhere but the pipeline's text.

Consequences:
- A pipeline with templates can now be converted as a whole, with its tasks accounted for. It is still a model's reading of what the templates do with parameters it cannot evaluate: that is why the Notes and the
  warnings stay, and why a person approves the text.
- More of the source reaches the model, from repositories other than the pipeline's own (a shared templates repository). The connection's PAT is what authorizes the read, so a template the PAT cannot read is
  `NotReadable`, and a person must have turned on both the consent and the checkbox.
- Not resolved by design: templates of GitHub or other external repositories (they need a service connection and another credential), computed paths, `extends` parameter schemas, template expressions
  (`${{ if }}`, `${{ each }}`) that the model has to interpret, a file larger than the limits, a tag or branch the PAT cannot read.
- Not verified online: reading templates against a real Azure DevOps Services organization or a Server collection. Only a stub and `tools/AdoSim` have answered. The live smoke test
  (`Template_reading_asks_the_real_service_what_the_simulator_assumes`, GET only) records what the real service says to: the definition naming the repository and path of its file, a read by repository id, a read
  at a branch, a missing path, a folder, and what a real pipeline's templates resolve to. Until it is run, "works against real Azure DevOps" is not claimed.

Alternatives considered: always reading templates (rejected: widens what is sent without the person's say-so); expanding the templates in the product before sending (rejected: it would mean evaluating Azure's
template language and its expressions, which the product does not do and which is where subtle mistakes live); resolving `type: github` repositories (rejected for now: a different credential and consent, and the
source's own GitHub connection is not the Azure one); one concatenated source text (rejected: one redaction and one limit for different files, and the model could not tell where a file ended); sending a
template's text back into the stored proposal (rejected: only names and hashes are needed to say what was sent, and the text would be a second copy of source to protect).
