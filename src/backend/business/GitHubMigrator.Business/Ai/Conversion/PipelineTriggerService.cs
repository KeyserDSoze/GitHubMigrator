using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

public sealed class TriggerDerivationException(string code) : Exception("Trigger derivation refused")
{
    public string Code { get; } = code;
}

// Turns the Azure triggers of an APPROVED conversion into GitHub triggers: the pipeline's own YAML is read (locally, never sent to a model), mapped by
// fixed rules, written into a copy of the approved workflow, judged by the trigger policy and kept as a proposal of its own. It approves nothing and
// publishes nothing: the derived workflow starts automatically, so a person decides about it separately, by its own hash.
public sealed class PipelineTriggerService(WorkflowProposalService proposals, IConnectionRepository connections, IConnectionSecretStore secrets,
    IAzureWorkloadReader source, IWorkflowGatePipelineFactory gates)
{
    public async Task<WorkflowProposal> DeriveAsync(ProposalActor actor, Guid parentId, CancellationToken ct)
    {
        // only an approved, intact conversion is a parent: what a person approved is what the triggers are added to
        var (approved, parent, _) = await proposals.RequireApprovedWithRecordAsync(parentId, ct);
        if (parent.Kind != ProposalKinds.Conversion) throw new TriggerDerivationException("ParentIsNotAConversion");

        var connection = await connections.GetAsync(parent.SourceConnectionId, ct) ?? throw new TriggerDerivationException("SourceNotFound");
        if (connection.Direction != ConnectionDirection.Source || !ProviderKeys.IsAzureDevOpsSource(connection.ProviderKey)) throw new TriggerDerivationException("UnsupportedSource");
        var token = await secrets.ReadAsync(connection.SecretReference, ct);
        if (string.IsNullOrEmpty(token)) throw new TriggerDerivationException("SourceCredentialMissing");

        string? yaml;
        try
        {
            var reader = WorkloadSources.For(source, connection) as IPipelineYamlReader ?? throw new TriggerDerivationException("UnsupportedSource");
            var read = await reader.ReadPipelineYamlAsync(connection.Organization, parent.ProjectId, parent.PipelineId, token, ct);
            if (read.TooLarge) throw new TriggerDerivationException("SourceTooLarge");
            yaml = read.Content;
        }
        catch (WorkloadInputException ex) { throw new TriggerDerivationException(ex.Code); }
        if (string.IsNullOrWhiteSpace(yaml)) throw new TriggerDerivationException("SourceYamlUnavailable");

        var azure = AzureTriggerReader.Read(yaml) ?? throw new TriggerDerivationException("SourceYamlUnreadable");
        var derived = WorkflowTriggerEditor.Derive(approved.WorkflowYaml, azure);
        if (!derived.Ok) throw new TriggerDerivationException(derived.Error!);

        // the same gates as the conversion, under the policy that allows exactly push, pull_request and schedule
        var report = await gates.Create(proposals.PolicyForKind(ProposalKinds.Triggers)).RunAsync(derived.Yaml!, parent.Evidence.Plan.ToGateContextFor(parent.Evidence.SourceTasks), ct);
        var blockers = report.Findings.Any(f => f.Severity == GateSeverity.Blocker);
        var candidate = new ConversionProposal(report.Approvable ? ConversionStatus.ReadyForReview : ConversionStatus.Blocked,
            report.Approvable ? null : blockers ? "GatesBlocked" : "AnalysersNotRun", derived.Yaml, report.WorkflowSha256, null, report, parent.Evidence.Plan,
            // the model that wrote the manual-only workflow is still the provenance of this text: the mapping only added `on:` entries
            parent.Evidence.Provenance) { SourceTasks = parent.Evidence.SourceTasks, PipelineName = parent.Evidence.PipelineName };

        var evidence = new TriggerEvidence(derived.Differences, derived.Triggers!.Summary());
        var kept = await proposals.RecordAsync(actor, parent.SourceConnectionId, parent.ProjectId, parent.PipelineId, parent.AiConnectionId, candidate, ct,
            ProposalKinds.Triggers, parent.Id, evidence);
        return kept ?? throw new TriggerDerivationException("ProposalNotKept");
    }
}
