using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain;
using GitHubMigrator.Domain.Tests.WorkflowGates;
using static GitHubMigrator.Domain.Tests.Ai.WorkflowProposalServiceTests;

namespace GitHubMigrator.Domain.Tests.Ai;

internal sealed class FakeAzureYamlSource : IAzureWorkloadReader, IPipelineYamlReader
{
    public PipelineYaml Yaml { get; set; } = new(AzureWithTriggers, false);
    public Exception? Throw { get; set; }
    public int Reads { get; private set; }
    public (string Organization, string Project, int Pipeline, string Token)? Last { get; private set; }

    public const string AzureWithTriggers = "trigger:\n  branches:\n    include: [main, 'release/*']\n  paths:\n    exclude: [docs]\npr:\n  branches:\n    include: [main]\nschedules:\n- cron: '0 3 * * 1-5'\n  always: true\n";

    public Task<PipelineYaml> ReadPipelineYamlAsync(string organization, string projectId, int pipelineId, string token, CancellationToken ct)
    {
        Reads++;
        Last = (organization, projectId, pipelineId, token);
        return Throw is null ? Task.FromResult(Yaml) : Task.FromException<PipelineYaml>(Throw);
    }

    public Task<AzurePipelineSource> GetPipelineAsync(string organization, string projectId, int pipelineId, string token, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<AzureBoardItem>> GetBoardItemsAsync(string organization, string projectId, IReadOnlyList<int> itemIds, string token, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class PipelineTriggerServiceTests
{
    private const string SourceToken = "ado-pat-never-in-a-proposal";

    private sealed record Rig(PipelineTriggerService Triggers, WorkflowProposalService Proposals, InMemoryProposalRepository Repo, FakeAzureYamlSource Source, WorkflowProposal Parent);

    private static MigrationConnection SourceConnection(ConnectionDirection direction = ConnectionDirection.Source, string provider = ProviderKeys.AzureDevOpsServices) =>
        new(SourceId, "Azure", provider, direction, "contoso", "ref-source", Now, Now, "Verified", "bot", "Verified");

    private static async Task<Rig> RigAsync(bool approveParent = true, MigrationConnection? connection = null, string? token = SourceToken, bool analysers = true,
        string? parentYaml = null, string[]? parentWarnings = null)
    {
        var repo = new InMemoryProposalRepository();
        var proposals = Service(repo, analysers: analysers);
        var kept = (await proposals.RecordAsync(Author, SourceId, "project", 42, AiId, await ConvertedAsync(parentYaml ?? Clean, analysers), default))!;
        if (approveParent) await Service(repo).ApproveAsync(Reviewer, kept.Id, kept.WorkflowSha256, parentWarnings ?? [], "Reviewed the manual-only workflow.", default);
        var secrets = new MemorySecrets();
        if (token is not null) secrets.Values["ref-source"] = token;
        var source = new FakeAzureYamlSource();
        var factory = new WorkflowGatePipelineFactory(
            analysers ? new FakeToolRunner("actionlint", new(true, "[]", null, 0)) : null, analysers ? new FakeToolRunner("zizmor", new(true, "[]", null, 0)) : null);
        var triggers = new PipelineTriggerService(proposals, new FakeDestinations(connection ?? SourceConnection()), secrets, source, factory);
        return new(triggers, proposals, repo, source, (await repo.GetAsync(kept.Id, default))!);
    }

    private static async Task<string> RefusedAsync(Rig rig, Guid? id = null)
    {
        var ex = await Assert.ThrowsAsync<TriggerDerivationException>(() => rig.Triggers.DeriveAsync(Author, id ?? rig.Parent.Id, default));
        return ex.Code;
    }

    [Fact]
    public async Task An_approved_conversion_gets_a_derived_proposal_with_the_azure_triggers_and_the_parent_is_left_exactly_as_approved()
    {
        var rig = await RigAsync();
        var derived = await rig.Triggers.DeriveAsync(Author, rig.Parent.Id, default);

        Assert.Equal(ProposalState.Proposed, derived.State);
        Assert.Equal(ProposalKinds.Triggers, derived.Kind);
        Assert.Equal(rig.Parent.Id, derived.ParentProposalId);
        Assert.NotEqual(rig.Parent.Id, derived.Id);
        Assert.NotEqual(rig.Parent.WorkflowSha256, derived.WorkflowSha256);
        Assert.Equal(WorkflowHash.Sha256(derived.WorkflowYaml!), derived.WorkflowSha256);
        Assert.True(derived.IntegrityOk);
        Assert.Null(derived.ApprovedSha256);
        Assert.Equal((rig.Parent.SourceConnectionId, rig.Parent.ProjectId, rig.Parent.PipelineId, rig.Parent.AiConnectionId),
            (derived.SourceConnectionId, derived.ProjectId, derived.PipelineId, derived.AiConnectionId));
        Assert.Equal("author", derived.CreatedBy);

        // the Azure YAML was read with the source credential, for the parent's pipeline
        Assert.Equal(1, rig.Source.Reads);
        Assert.Equal(("contoso", "project", 42, SourceToken), rig.Source.Last);

        // only the on: entry differs from the approved text
        var before = rig.Parent.WorkflowYaml!.Split('\n');
        var after = derived.WorkflowYaml!.Split('\n');
        var onAt = Array.IndexOf(before, "on:");
        Assert.Equal(before.Take(onAt), after.Take(onAt));
        Assert.Equal(before.SkipWhile(l => !l.StartsWith("permissions:")), after.SkipWhile(l => !l.StartsWith("permissions:")));
        Assert.Contains("  push:", after);
        Assert.Contains("  schedule:", after);

        Assert.Equal(["push — branches: main, release/**; paths: ignore docs, ignore docs/**", "pull_request — branches: main", "schedule: 0 3 * * 1-5"], derived.Evidence.Triggers!.Mapped);
        Assert.Contains(derived.Evidence.Findings, f => f.Gate == "triggers" && f.Code == "TRIGGER_PR_AZURE_REPOS_IGNORED" && f.Severity == "Warning");
        Assert.Contains(derived.Evidence.Findings, f => f.Gate == "triggers" && f.Code == "TRIGGER_STARTS_PRIVILEGED_JOBS"); // the sample publishes with a secret
        Assert.Equal(rig.Parent.Evidence.Provenance, derived.Evidence.Provenance);
        Assert.Equal(rig.Parent.Evidence.SourceTasks, derived.Evidence.SourceTasks);

        // the approved conversion is untouched: same state, same text, same approved hash
        var parent = (await rig.Repo.GetAsync(rig.Parent.Id, default))!;
        Assert.Equal((ProposalState.Approved, rig.Parent.WorkflowYaml, rig.Parent.ApprovedSha256), (parent.State, parent.WorkflowYaml, parent.ApprovedSha256));
    }

    [Fact]
    public async Task Approving_the_derived_proposal_needs_every_mapping_difference_named_and_is_judged_by_the_trigger_policy()
    {
        var rig = await RigAsync();
        var derived = await rig.Triggers.DeriveAsync(Author, rig.Parent.Id, default);
        var codes = derived.Evidence.Triggers!.Differences.Select(d => d.Code).Order(StringComparer.Ordinal).ToArray();
        Assert.Contains("TRIGGER_PR_AZURE_REPOS_IGNORED", codes);

        // gates raise no warning for this text (clean analysers), so the only things to accept are the mapping's differences
        var none = await Assert.ThrowsAsync<WorkflowProposalException>(() => rig.Proposals.ApproveAsync(Reviewer, derived.Id, derived.WorkflowSha256, [], "Reviewed.", default));
        Assert.Equal("WarningsNotAcknowledged", none.Code);
        Assert.Equal(codes, none.Details.Order(StringComparer.Ordinal));

        var partial = await Assert.ThrowsAsync<WorkflowProposalException>(() => rig.Proposals.ApproveAsync(Reviewer, derived.Id, derived.WorkflowSha256, codes.Skip(1).ToArray(), "Reviewed.", default));
        Assert.Equal([codes[0]], partial.Details);

        var approved = await rig.Proposals.ApproveAsync(Reviewer, derived.Id, derived.WorkflowSha256, codes, "Reviewed the triggers and what differs from Azure.", default);
        Assert.Equal(ProposalState.Approved, approved.State);
        Assert.Equal(derived.WorkflowSha256, approved.ApprovedSha256);
        var evt = rig.Repo.Log.Last(e => e.ProposalId == derived.Id && e.Type == "Approved");
        Assert.EndsWith("+triggers", evt.Detail!.PolicyVersion);
        Assert.Equal(codes, evt.Detail.AcknowledgedWarnings.Order(StringComparer.Ordinal));

        // and the approval of the derived proposal did not touch the parent's
        Assert.EndsWith("+triggers", WorkflowGatePolicy.Default.WithAutomaticTriggers().Version);
        Assert.DoesNotContain("+triggers", rig.Repo.Log.Last(e => e.ProposalId == rig.Parent.Id && e.Type == "Approved").Detail!.PolicyVersion);
    }

    [Fact]
    public async Task The_same_person_cannot_approve_the_derived_proposal_they_asked_for_when_four_eyes_are_on()
    {
        var rig = await RigAsync();
        var derived = await rig.Triggers.DeriveAsync(Author, rig.Parent.Id, default);
        var ex = await Assert.ThrowsAsync<WorkflowProposalException>(() => rig.Proposals.ApproveAsync(Author, derived.Id, derived.WorkflowSha256,
            derived.Evidence.Triggers!.Differences.Select(d => d.Code).ToArray(), "Reviewed.", default));
        Assert.Equal("SecondApproverRequired", ex.Code);
    }

    [Fact]
    public async Task A_conversion_that_is_not_approved_or_is_not_a_conversion_is_not_a_parent()
    {
        var pending = await RigAsync(approveParent: false);
        Assert.Equal("ProposalNotApproved", (await Assert.ThrowsAsync<WorkflowProposalException>(() => pending.Triggers.DeriveAsync(Author, pending.Parent.Id, default))).Code);
        Assert.Equal(0, pending.Source.Reads);

        var rig = await RigAsync();
        Assert.Equal("ProposalNotFound", (await Assert.ThrowsAsync<WorkflowProposalException>(() => rig.Triggers.DeriveAsync(Author, Guid.NewGuid(), default))).Code);

        var derived = await rig.Triggers.DeriveAsync(Author, rig.Parent.Id, default);
        await rig.Proposals.ApproveAsync(Reviewer, derived.Id, derived.WorkflowSha256, derived.Evidence.Triggers!.Differences.Select(d => d.Code).ToArray(), "Reviewed.", default);
        Assert.Equal("ParentIsNotAConversion", await RefusedAsync(rig, derived.Id)); // triggers are never derived from triggers

        var rejected = await RigAsync();
        await rejected.Proposals.RevokeAsync(Reviewer, rejected.Parent.Id, "Wrong pipeline.", default);
        Assert.Equal("ProposalNotApproved", (await Assert.ThrowsAsync<WorkflowProposalException>(() => rejected.Triggers.DeriveAsync(Author, rejected.Parent.Id, default))).Code);
    }

    [Fact]
    public async Task Nothing_is_kept_when_azure_has_no_automatic_trigger_or_the_yaml_cannot_be_used()
    {
        var rig = await RigAsync();
        var before = rig.Repo.Rows.Count;

        rig.Source.Yaml = new("trigger: none\npr: none\n", false);
        Assert.Equal("NoAutomaticTriggers", await RefusedAsync(rig));
        rig.Source.Yaml = new(null, false);
        Assert.Equal("SourceYamlUnavailable", await RefusedAsync(rig)); // a classic pipeline has no YAML
        rig.Source.Yaml = new(null, true);
        Assert.Equal("SourceTooLarge", await RefusedAsync(rig));
        rig.Source.Yaml = new("trigger: [unclosed\n", false);
        Assert.Equal("SourceYamlUnreadable", await RefusedAsync(rig));
        rig.Source.Yaml = new("   \n", false);
        Assert.Equal("SourceYamlUnavailable", await RefusedAsync(rig));
        rig.Source.Throw = new WorkloadInputException("SourceHostNotAllowed");
        Assert.Equal("SourceHostNotAllowed", await RefusedAsync(rig));

        Assert.Equal(before, rig.Repo.Rows.Count);
    }

    [Fact]
    public async Task The_source_must_be_an_azure_devops_source_with_a_credential()
    {
        Assert.Equal("SourceNotFound", await RefusedAsync(await RigAsync(connection: SourceConnection() with { Id = Guid.NewGuid() })));
        Assert.Equal("UnsupportedSource", await RefusedAsync(await RigAsync(connection: SourceConnection(ConnectionDirection.Destination))));
        Assert.Equal("UnsupportedSource", await RefusedAsync(await RigAsync(connection: SourceConnection(provider: ProviderKeys.GitHubEnterpriseCloud))));
        var noToken = await RigAsync(token: null);
        Assert.Equal("SourceCredentialMissing", await RefusedAsync(noToken));
        Assert.Equal(0, noToken.Source.Reads);
    }

    [Fact]
    public async Task Without_the_analysers_nothing_is_approved_so_nothing_is_a_parent()
    {
        var rig = await RigAsync(approveParent: false, analysers: false);
        // the parent could not even be proposed: it is Blocked, and a blocked proposal is not a parent
        Assert.Equal(ProposalState.Blocked, rig.Parent.State);
        Assert.Equal("ProposalNotApproved", (await Assert.ThrowsAsync<WorkflowProposalException>(() => rig.Triggers.DeriveAsync(Author, rig.Parent.Id, default))).Code);
    }

    [Fact]
    public async Task A_derived_text_the_gates_stop_is_kept_blocked_without_its_text()
    {
        // the mapping cannot produce a trigger the policy refuses; what can still stop the text is an analyser, here an actionlint that rejects the derived workflow
        var rig = await RigAsync();
        var strict = new PipelineTriggerService(rig.Proposals, new FakeDestinations(SourceConnection()), new MemorySecrets { Values = { ["ref-source"] = SourceToken } }, rig.Source,
            new WorkflowGatePipelineFactory(new FakeToolRunner("actionlint", new(true, "[{\"message\":\"bad\",\"line\":1,\"column\":1,\"kind\":\"syntax-check\"}]", null, 1)),
                new FakeToolRunner("zizmor", new(true, "[]", null, 0))));
        var blocked = await strict.DeriveAsync(Author, rig.Parent.Id, default);
        Assert.Equal(ProposalState.Blocked, blocked.State);
        Assert.Equal(ProposalKinds.Triggers, blocked.Kind);
        Assert.Null(blocked.WorkflowYaml);
        Assert.Equal(rig.Parent.Id, blocked.ParentProposalId);
        var ex = await Assert.ThrowsAsync<WorkflowProposalException>(() => rig.Proposals.ApproveAsync(Reviewer, blocked.Id, blocked.WorkflowSha256, [], "Reviewed.", default));
        Assert.NotEqual("", ex.Code);
    }

    [Fact]
    public async Task Each_request_makes_a_new_proposal_and_earlier_ones_are_kept()
    {
        var rig = await RigAsync();
        var first = await rig.Triggers.DeriveAsync(Author, rig.Parent.Id, default);
        var second = await rig.Triggers.DeriveAsync(Reviewer, rig.Parent.Id, default);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.WorkflowSha256, second.WorkflowSha256); // deterministic: the same Azure YAML gives the same text
        var list = await rig.Proposals.ListAsync(SourceId, "project", 42, default);
        Assert.Equal(2, list.Count(p => p.Kind == ProposalKinds.Triggers));
        Assert.Equal(1, list.Count(p => p.Kind == ProposalKinds.Conversion));
    }

    [Fact]
    public async Task The_pull_request_for_a_triggers_proposal_says_it_starts_by_itself_and_lists_the_triggers_and_the_differences_a_person_accepted()
    {
        var rig = await RigAsync();
        var derived = await rig.Triggers.DeriveAsync(Author, rig.Parent.Id, default);
        var codes = derived.Evidence.Triggers!.Differences.Select(d => d.Code).ToArray();
        await rig.Proposals.ApproveAsync(Reviewer, derived.Id, derived.WorkflowSha256, codes, "Reviewed the triggers.", default);

        var secrets = new MemorySecrets { Values = { ["ref-1"] = "destination-pat" } };
        var reader = new FakePublicationReader();
        var service = new WorkflowPublicationService(rig.Proposals, new FakeDestinations(WorkflowPublicationPlanTests.Destination()), secrets, reader, TimeProvider.System);
        var (plan, yaml) = await service.PlanAsync(derived.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);

        Assert.True(plan.Ready);
        Assert.Equal(derived.WorkflowYaml, yaml);
        Assert.Equal(".github/workflows/azdo-42-orion-api-ci-cd.yml", plan.WorkflowPath);
        Assert.Equal("migrate/azdo-42-" + derived.WorkflowSha256![..8], plan.HeadBranch);
        Assert.EndsWith("with automatic triggers", plan.PullRequestTitle);
        Assert.Contains("with automatic triggers", plan.CommitMessage);
        Assert.Contains(derived.WorkflowSha256, plan.CommitMessage);
        Assert.Equal(derived.WorkflowSha256, Assert.Single(reader.Calls).Sha); // the destination is compared with the derived hash, not the parent's

        var body = plan.PullRequestBody;
        Assert.Contains("**starts by itself**", body);
        Assert.Contains("### Automatic triggers", body);
        Assert.Contains("- `push — branches: main, release/**; paths: ignore docs, ignore docs/**`", body);
        Assert.Contains("- `schedule: 0 3 * * 1-5`", body);
        Assert.Contains("Differences from Azure, accepted by a person:", body);
        foreach (var code in codes) Assert.Contains($"`{code}`", body);
        Assert.DoesNotContain("original Azure triggers were **not** converted", body);
        Assert.DoesNotContain("It runs only when started by hand", body);
        Assert.Contains("not** proven equivalent", body);
    }
}
