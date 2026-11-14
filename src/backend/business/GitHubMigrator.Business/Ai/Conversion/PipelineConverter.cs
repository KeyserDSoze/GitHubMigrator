using GitHubMigrator.Business.WorkflowGates;

namespace GitHubMigrator.Business;

// Orchestrates one conversion: redact, ask, extract, gate, and (a bounded number of times) ask again with the blocking
// findings. It decides nothing about approval and publishes nothing: its result is a proposal plus the evidence.
// nonce: where the markers' random value comes from (a test can choose it); never a caller of the API.
public sealed class PipelineConverter(IChatModelClient client, IWorkflowGatePipelineFactory gates, TimeProvider clock, WorkflowGatePolicy? policy = null, Func<string>? nonceSource = null)
{
    private readonly WorkflowGatePolicy _policy = policy ?? WorkflowGatePolicy.Default;

    // templates: what the service read of the templates the pipeline includes (already redacted and bounded); null when nobody asked for them.
    // origin: set when the text is the product's rendering of a classic (designer) definition and not a file; without it a pipeline that is not YAML is still refused.
    public async Task<ConversionProposal> ConvertAsync(AiConnectionUse use, AzurePipelineSource pipeline, string? sourceYaml,
        ConversionOptions? options, CancellationToken ct, ResolvedTemplates? templates = null, SourceOrigin? origin = null)
    {
        options ??= new();
        var plan = ConversionPlanBuilder.Build(pipeline, sourceYaml, options.OperatorRunnerLabels, options.RunnerLabels, templates?.Files.Select(f => f.Text).ToArray());
        var classic = pipeline.Kind != "yaml" && origin?.Kind == PipelineSourceOrigins.ClassicDefinition;
        if (pipeline.Kind != "yaml" && !classic) return ConversionProposal.NotAttempted("ClassicPipelineUnsupported", plan);
        if (string.IsNullOrWhiteSpace(sourceYaml)) return ConversionProposal.NotAttempted(classic ? "ClassicPipelineUnsupported" : "SourceYamlUnavailable", plan);
        // defence in depth: the caller resolved the connection with consent required, and nothing here relies on that alone
        if (!use.Connection.SendSourceContent) return ConversionProposal.NotAttempted("SourceConsentRequired", plan);

        var redaction = SourceRedactor.Redact(sourceYaml, Math.Min(SourceRedactor.DefaultMaxBytes, Math.Max(1_000, use.Endpoint.MaxInputBytes / 2)));
        if (redaction.Blocked) return ConversionProposal.NotAttempted("RedactionBlocked", plan);
        // a cut-off source would silently drop steps: refuse instead of converting part of a pipeline
        if (redaction.Truncated) return ConversionProposal.NotAttempted("SourceTooLarge", plan);

        // the tasks of the pipeline AND of the templates that are sent with it: the workflow has to account for every one of them
        var sourceTasks = new HashSet<string>(SourceTaskInventory.From(sourceYaml), StringComparer.OrdinalIgnoreCase);
        foreach (var file in templates?.Files ?? []) sourceTasks.UnionWith(file.Tasks);
        var nonce = ConversionPrompt.NewNonceAvoiding(nonceSource ?? ConversionPrompt.NewNonce, [redaction.Text, .. templates?.Files.Select(f => f.Text) ?? []]);
        var (system, user) = ConversionPrompt.Build(plan, redaction.Text, sourceTasks, nonce, _policy, templates, classic ? origin : null);
        var messages = new List<ChatMessage> { ChatMessage.System(system), ChatMessage.User(user) };
        var promptSha = ConversionPrompt.Sha256(messages);
        var redactions = redaction.Findings.Select(f => f.Kind).Concat(templates?.Files.SelectMany(f => f.RedactionKinds) ?? [])
            .GroupBy(k => k).Select(g => new RedactionSummary(g.Key, g.Count())).OrderBy(r => r.Kind, StringComparer.Ordinal).ToArray();

        var attempts = new List<ConversionAttempt>();
        int promptTokens = 0, completionTokens = 0;
        string? reportedModel = null;
        ConversionProposal? proposal = null;
        var pipelineOfGates = gates.Create(_policy);
        var gateContext = plan.ToGateContextFor(sourceTasks);

        for (var attempt = 1; attempt <= 1 + options.EffectiveRepairs; attempt++)
        {
            ChatResult result;
            try { result = await client.CompleteAsync(use.Endpoint, use.Secret, new(messages.ToArray(), MaxOutputTokens: use.Endpoint.MaxOutputTokens), ct); }
            catch (ChatModelException ex)
            {
                attempts.Add(new(attempt, "ModelFailed:" + ex.Failure, null, null, null, []));
                if (proposal is null) return ConversionProposal.NotAttempted("ModelFailure:" + ex.Failure, plan, Provenance());
                break; // a later attempt failed: keep what the previous one produced
            }
            promptTokens += result.Usage?.PromptTokens ?? 0;
            completionTokens += result.Usage?.CompletionTokens ?? 0;
            reportedModel ??= result.Model;

            if (result.Truncated)
            {
                attempts.Add(new(attempt, "Truncated", result.Usage?.PromptTokens, result.Usage?.CompletionTokens, null, ["OUTPUT_TRUNCATED"]));
                proposal = new(ConversionStatus.Blocked, "OUTPUT_TRUNCATED", null, null, null, null, plan, null);
                break; // asking again with the same token limit would be cut off again
            }

            var extracted = ModelOutputExtractor.Extract(result.Content);
            if (!extracted.Ok)
            {
                attempts.Add(new(attempt, "NoWorkflow", result.Usage?.PromptTokens, result.Usage?.CompletionTokens, null, [extracted.Error!.Code]));
                proposal = new(ConversionStatus.Blocked, extracted.Error.Code, null, null, null, null, plan, null);
                if (attempt <= options.EffectiveRepairs)
                {
                    messages.Add(new("assistant", Cap(result.Content)));
                    messages.Add(ChatMessage.User(ConversionPrompt.Repair([$"[output] {extracted.Error.Code}: {extracted.Error.Message}"], nonce)));
                    continue;
                }
                break;
            }

            var report = await pipelineOfGates.RunAsync(extracted.Yaml!, gateContext, ct);
            var blockers = report.Findings.Where(f => f.Severity == GateSeverity.Blocker).ToArray();
            attempts.Add(new(attempt, report.Approvable ? "GatesPassed" : blockers.Length > 0 ? "Blocked" : "AnalysersNotRun",
                result.Usage?.PromptTokens, result.Usage?.CompletionTokens, report.WorkflowSha256, blockers.Select(b => b.Code).Distinct().ToArray()));
            proposal = new(report.Approvable ? ConversionStatus.ReadyForReview : ConversionStatus.Blocked,
                report.Approvable ? null : blockers.Length > 0 ? "GatesBlocked" : "AnalysersNotRun",
                extracted.Yaml, report.WorkflowSha256, extracted.Notes, report, plan, null);
            if (blockers.Length == 0 || attempt > options.EffectiveRepairs) break;

            messages.Add(new("assistant", Cap(result.Content)));
            messages.Add(ChatMessage.User(ConversionPrompt.Repair(blockers.Select(b => $"[{b.Gate}] {b.Code}{(b.Line is { } line ? " line " + line : "")}: {b.Message}"), nonce)));
        }

        return (proposal ?? ConversionProposal.NotAttempted("ModelFailure:Unknown", plan)) with
        {
            Provenance = Provenance(), SourceTasks = sourceTasks.Order(StringComparer.OrdinalIgnoreCase).ToArray(), PipelineName = pipeline.Name
        };

        ConversionProvenance Provenance() => new(use.Connection.Id, use.Connection.Provider.ToString(), new Uri(use.Connection.Endpoint).Host,
            use.Connection.Model, reportedModel, promptSha, plan.Sha256, _policy.Version, ActionCatalog.VerifiedOn.ToString("yyyy-MM-dd"),
            redactions, promptTokens, completionTokens, attempts.ToArray(), clock.GetUtcNow(),
            templates is null ? null : templates.Files.Select(f => new TemplateProvenance(f.Repository, f.Path, f.Ref, f.Sha256, f.Bytes)).ToArray(),
            templates is null ? null : templates.Unresolved,
            classic ? origin!.Kind : null, classic ? Notes(origin!) : null);

        static string Cap(string text) => text.Length > 120_000 ? text[..120_000] : text;

        // the notes come from the definition's own text: they are redacted like the source before they are kept or shown
        static IReadOnlyList<string> Notes(SourceOrigin origin) => origin.Notes.Take(40).Select(note =>
        {
            var redacted = SourceRedactor.Redact(note, 2_000);
            return redacted.Blocked ? "(withheld: it holds something that must not be kept)" : redacted.Text;
        }).ToArray();
    }
}
