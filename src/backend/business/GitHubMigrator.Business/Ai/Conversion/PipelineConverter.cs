using GitHubMigrator.Business.WorkflowGates;

namespace GitHubMigrator.Business;

// Orchestrates one conversion: redact, ask, extract, gate, and (a bounded number of times) ask again with the blocking
// findings. It decides nothing about approval and publishes nothing: its result is a proposal plus the evidence.
public sealed class PipelineConverter(IChatModelClient client, IWorkflowGatePipelineFactory gates, TimeProvider clock, WorkflowGatePolicy? policy = null)
{
    private readonly WorkflowGatePolicy _policy = policy ?? WorkflowGatePolicy.Default;

    public async Task<ConversionProposal> ConvertAsync(AiConnectionUse use, AzurePipelineSource pipeline, string? sourceYaml,
        ConversionOptions? options, CancellationToken ct)
    {
        options ??= new();
        var plan = ConversionPlanBuilder.Build(pipeline, sourceYaml);
        if (pipeline.Kind != "yaml") return ConversionProposal.NotAttempted("ClassicPipelineUnsupported", plan);
        if (string.IsNullOrWhiteSpace(sourceYaml)) return ConversionProposal.NotAttempted("SourceYamlUnavailable", plan);
        // defence in depth: the caller resolved the connection with consent required, and nothing here relies on that alone
        if (!use.Connection.SendSourceContent) return ConversionProposal.NotAttempted("SourceConsentRequired", plan);

        var redaction = SourceRedactor.Redact(sourceYaml, Math.Min(SourceRedactor.DefaultMaxBytes, Math.Max(1_000, use.Endpoint.MaxInputBytes / 2)));
        if (redaction.Blocked) return ConversionProposal.NotAttempted("RedactionBlocked", plan);
        // a cut-off source would silently drop steps: refuse instead of converting part of a pipeline
        if (redaction.Truncated) return ConversionProposal.NotAttempted("SourceTooLarge", plan);

        var sourceTasks = SourceTaskInventory.From(sourceYaml);
        var nonce = ConversionPrompt.NewNonce();
        while (redaction.Text.Contains(nonce, StringComparison.Ordinal)) nonce = ConversionPrompt.NewNonce();
        var (system, user) = ConversionPrompt.Build(plan, redaction.Text, sourceTasks, nonce, _policy);
        var messages = new List<ChatMessage> { ChatMessage.System(system), ChatMessage.User(user) };
        var promptSha = ConversionPrompt.Sha256(messages);
        var redactions = redaction.Findings.GroupBy(f => f.Kind).Select(g => new RedactionSummary(g.Key, g.Count())).OrderBy(r => r.Kind, StringComparer.Ordinal).ToArray();

        var attempts = new List<ConversionAttempt>();
        int promptTokens = 0, completionTokens = 0;
        string? reportedModel = null;
        ConversionProposal? proposal = null;
        var pipelineOfGates = gates.Create(_policy);
        var gateContext = plan.ToGateContext(sourceYaml);

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
            Provenance = Provenance(), SourceTasks = sourceTasks.Order(StringComparer.OrdinalIgnoreCase).ToArray()
        };

        ConversionProvenance Provenance() => new(use.Connection.Id, use.Connection.Provider.ToString(), new Uri(use.Connection.Endpoint).Host,
            use.Connection.Model, reportedModel, promptSha, plan.Sha256, _policy.Version, ActionCatalog.VerifiedOn.ToString("yyyy-MM-dd"),
            redactions, promptTokens, completionTokens, attempts.ToArray(), clock.GetUtcNow());

        static string Cap(string text) => text.Length > 120_000 ? text[..120_000] : text;
    }
}
