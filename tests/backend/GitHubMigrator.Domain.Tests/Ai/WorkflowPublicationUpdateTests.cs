using System.Security.Cryptography;
using System.Text;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain;
using GitHubMigrator.Domain.Tests.WorkflowGates;
using static GitHubMigrator.Domain.Tests.Ai.WorkflowProposalServiceTests;

namespace GitHubMigrator.Domain.Tests.Ai;

// Replacing a file this product published earlier: a derived (Triggers) proposal may replace the approved text of the conversion it came from, and only that.
public sealed class WorkflowPublicationUpdateTests
{
    private const string Key = "publication-key";
    private static readonly string Blob = new('b', 40);
    private static readonly ProposalActor Publisher = new(Guid.Parse("cccccccc-0000-0000-0000-000000000003"), "publisher");

    private sealed record Rig(WorkflowPublicationService Service, FakePublicationReader Reader, WorkflowProposal Parent, WorkflowProposal Derived, WorkflowProposalService Proposals,
        InMemoryProposalRepository Repo, MemorySecrets Secrets, FakeDestinations Connections);

    private static WorkflowGatePipelineFactory Analysers() => new(new FakeToolRunner("actionlint", new(true, "[]", null, 0)), new FakeToolRunner("zizmor", new(true, "[]", null, 0)));

    // An approved conversion of pipeline 42 and, derived from it, an approved Triggers proposal; the destination "shows" whatever the reader is told to.
    private static async Task<Rig> RigAsync(WorkflowFileState file = WorkflowFileState.PriorVersion, string? blob = null)
    {
        var repo = new InMemoryProposalRepository();
        var proposals = Service(repo);
        var parent = (await proposals.RecordAsync(Author, SourceId, "project", 42, AiId, await ConvertedAsync(Clean), default))!;
        await proposals.ApproveAsync(Reviewer, parent.Id, parent.WorkflowSha256, [], "Reviewed the manual-only workflow.", default);

        var secrets = new MemorySecrets { Values = { ["ref-source"] = "ado-pat", ["ref-1"] = WorkflowPublicationPlanTests.Token } };
        var source = new MigrationConnection(SourceId, "Azure", ProviderKeys.AzureDevOpsServices, ConnectionDirection.Source, "contoso", "ref-source", Now, Now, "Verified", "bot", "Verified");
        var triggers = new PipelineTriggerService(proposals, new FakeDestinations(source), secrets, new FakeAzureYamlSource(), Analysers());
        var derived = await triggers.DeriveAsync(Author, parent.Id, default);
        await proposals.ApproveAsync(Reviewer, derived.Id, derived.WorkflowSha256, derived.Evidence.Triggers!.Differences.Select(d => d.Code).ToArray(), "Reviewed the triggers.", default);

        var reader = new FakePublicationReader { Result = FakePublicationReader.FreshTarget() with { WorkflowFile = file, WorkflowFileBlobSha = file == WorkflowFileState.PriorVersion ? blob ?? Blob : null } };
        var connections = new FakeDestinations(WorkflowPublicationPlanTests.Destination());
        var service = new WorkflowPublicationService(proposals, connections, secrets, reader, new Clock());
        return new(service, reader, (await repo.GetAsync(parent.Id, default))!, (await repo.GetAsync(derived.Id, default))!, proposals, repo, secrets, connections);
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now.AddHours(2); }

    private static async Task<PublicationPlan> PlanAsync(Rig rig, WorkflowProposal? proposal = null) =>
        (await rig.Service.PlanAsync((proposal ?? rig.Derived).Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default)).Plan;

    [Fact]
    public async Task A_derived_proposal_whose_parent_text_is_at_the_path_is_planned_as_a_replacement_of_exactly_that_blob()
    {
        var rig = await RigAsync();
        var plan = await PlanAsync(rig);

        // the reader was asked about the parent's approved text, and nothing else
        Assert.Equal([rig.Parent.ApprovedSha256], rig.Reader.Priors);
        Assert.Equal((PublicationModes.Update, Blob, rig.Parent.ApprovedSha256), (plan.Mode, plan.ReplacesBlobSha, plan.ReplacesWorkflowSha256));
        Assert.True(plan.Ready);
        Assert.DoesNotContain(plan.Findings, f => f.Severity == "Blocker");
        var info = Assert.Single(plan.Findings, f => f.Code == "WorkflowWillBeReplaced");
        Assert.Equal("Info", info.Severity);
        Assert.Contains(rig.Parent.ApprovedSha256!, info.Message);

        // same file, the branch of the NEW text
        Assert.Equal(WorkflowPublicationPlanner.WorkflowPathFor(42, "Orion API CI/CD"), plan.WorkflowPath);
        Assert.Equal("migrate/azdo-42-" + rig.Derived.WorkflowSha256![..8], plan.HeadBranch);
        Assert.NotEqual("migrate/azdo-42-" + rig.Parent.WorkflowSha256![..8], plan.HeadBranch);

        // the words say it replaces, not adds
        Assert.StartsWith("Add automatic triggers to the GitHub Actions workflow for Azure pipeline #42", plan.PullRequestTitle);
        Assert.DoesNotContain("Add GitHub Actions workflow", plan.PullRequestTitle);
        Assert.StartsWith("ci: add automatic triggers to the GitHub Actions workflow of Azure pipeline 42", plan.CommitMessage);
        Assert.Contains(rig.Parent.ApprovedSha256!, plan.CommitMessage);
        Assert.Contains(rig.Derived.WorkflowSha256!, plan.CommitMessage);
        Assert.Contains($"| Replaces | the workflow at this path approved earlier, SHA-256 `{rig.Parent.ApprovedSha256}` |", plan.PullRequestBody);
        Assert.Contains("**replaces**", plan.PullRequestBody);
        Assert.Contains("**starts by itself**", plan.PullRequestBody);
        Assert.Contains(plan.Steps, s => s.Code == "Replace" && s.Command!.StartsWith("cat > ") && !s.Command.Contains("mkdir"));
        Assert.DoesNotContain(plan.Steps, s => s.Code == "Save");
    }

    [Fact]
    public async Task The_plan_hash_names_the_replacement_so_confirming_a_plan_confirms_which_file_is_replaced()
    {
        var rig = await RigAsync();
        var first = await PlanAsync(rig);
        Assert.Equal(first.PlanSha256, (await PlanAsync(rig)).PlanSha256); // the same reading is the same plan

        rig.Reader.Result = rig.Reader.Result with { WorkflowFileBlobSha = new string('c', 40) };
        var otherBlob = await PlanAsync(rig);
        Assert.NotEqual(first.PlanSha256, otherBlob.PlanSha256); // the file changed under the plan: another plan

        rig.Reader.Result = FakePublicationReader.FreshTarget();
        var create = await PlanAsync(rig);
        Assert.Equal(PublicationModes.Create, create.Mode);
        Assert.Null(create.ReplacesBlobSha);
        Assert.NotEqual(first.PlanSha256, create.PlanSha256);
        Assert.DoesNotContain(create.Findings, f => f.Code == "WorkflowWillBeReplaced");
        Assert.Contains(create.Steps, s => s.Code == "Save");
        Assert.StartsWith("Add GitHub Actions workflow for Azure pipeline #42", create.PullRequestTitle); // a derived proposal on a free path is a new file, as before
    }

    [Fact]
    public async Task A_plan_that_creates_a_file_hashes_exactly_what_it_always_did()
    {
        var setup = await WorkflowPublicationPlanTests.ApprovedAsync();
        var (plan, _) = await setup.Service.PlanAsync(setup.Proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', "publication-plan-v1", plan.ProposalId, plan.WorkflowSha256,
            $"{plan.Organization}/{plan.Repository}", plan.BaseBranch, plan.BaseSha ?? "", plan.HeadBranch, plan.WorkflowPath, plan.CommitMessage, plan.PullRequestTitle, plan.PullRequestBody))));
        Assert.Equal(expected, plan.PlanSha256);
        Assert.Equal((PublicationModes.Create, (string?)null, (string?)null), (plan.Mode, plan.ReplacesBlobSha, plan.ReplacesWorkflowSha256));
    }

    [Fact]
    public async Task Nothing_is_replaceable_once_the_parent_approval_is_withdrawn_and_a_file_there_is_then_somebody_elses()
    {
        var rig = await RigAsync();
        await rig.Proposals.RevokeAsync(Reviewer, rig.Parent.Id, "Found a problem in the manual-only workflow.", default);

        var plan = await PlanAsync(rig);
        Assert.Equal(new string?[] { null }, rig.Reader.Priors); // nothing was named as replaceable
        // even if a reader insisted the file is a prior version, a plan that named nothing does not replace anything
        Assert.False(plan.Ready);
        Assert.Contains(plan.Findings, f => f.Code == "WorkflowPathOccupied" && f.Severity == "Blocker");
        Assert.Equal((PublicationModes.Create, (string?)null), (plan.Mode, plan.ReplacesBlobSha));
    }

    [Fact]
    public async Task A_conversion_never_replaces_anything_and_neither_does_a_parent_of_another_pipeline_or_a_reading_without_a_blob()
    {
        var conversion = await WorkflowPublicationPlanTests.ApprovedAsync();
        conversion.Reader.Result = FakePublicationReader.FreshTarget() with { WorkflowFile = WorkflowFileState.PriorVersion, WorkflowFileBlobSha = Blob };
        var (plan, _) = await conversion.Service.PlanAsync(conversion.Proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        Assert.Equal(new string?[] { null }, conversion.Reader.Priors);
        Assert.False(plan.Ready);
        Assert.Contains(plan.Findings, f => f.Code == "WorkflowPathOccupied");

        // a conversion row that somehow carries a parent id (the database forbids it; the service does not rely on that) is still not a derived proposal
        var lonely = await WorkflowPublicationPlanTests.ApprovedAsync();
        var parentless = (await lonely.Proposals.RecordAsync(Author, SourceId, "project", 42, AiId, await ConvertedAsync(Clean), default))!;
        await lonely.Proposals.ApproveAsync(Reviewer, parentless.Id, parentless.WorkflowSha256, [], "Reviewed.", default);
        lonely.Repo.Rows[lonely.Proposal.Id] = lonely.Repo.Rows[lonely.Proposal.Id] with { ParentProposalId = parentless.Id };
        lonely.Reader.Result = FakePublicationReader.FreshTarget() with { WorkflowFile = WorkflowFileState.PriorVersion, WorkflowFileBlobSha = Blob };
        var (oddly, _) = await lonely.Service.PlanAsync(lonely.Proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        Assert.Equal(new string?[] { null }, lonely.Reader.Priors);
        Assert.False(oddly.Ready);

        // a derived proposal whose recorded parent is a conversion of ANOTHER pipeline
        var rig = await RigAsync();
        var other = (await rig.Proposals.RecordAsync(Author, SourceId, "project", 43, AiId, await ConvertedAsync(Clean), default))!;
        await rig.Proposals.ApproveAsync(Reviewer, other.Id, other.WorkflowSha256, [], "Reviewed.", default);
        rig.Repo.Rows[rig.Derived.Id] = rig.Derived with { ParentProposalId = other.Id };
        await PlanAsync(rig);
        Assert.Equal(new string?[] { null }, rig.Reader.Priors);

        // a destination that says "prior version" without a blob id cannot be replaced: nothing names the file to replace
        var noBlob = await RigAsync();
        noBlob.Reader.Result = noBlob.Reader.Result with { WorkflowFileBlobSha = null };
        var unnamed = await PlanAsync(noBlob);
        Assert.False(unnamed.Ready);
        Assert.Equal(PublicationModes.Create, unnamed.Mode);
        Assert.Contains(unnamed.Findings, f => f.Code == "WorkflowPathOccupied");
    }

    [Fact]
    public async Task Another_file_or_the_same_text_at_the_path_is_what_it_was_before()
    {
        var different = await RigAsync(WorkflowFileState.Different);
        var occupied = await PlanAsync(different);
        Assert.False(occupied.Ready);
        Assert.Contains(occupied.Findings, f => f.Code == "WorkflowPathOccupied");

        var identical = await RigAsync(WorkflowFileState.Identical);
        var done = await PlanAsync(identical);
        Assert.False(done.Ready);
        Assert.Contains(done.Findings, f => f.Code == "AlreadyPublished");
    }

    // ---- the executor: what the person confirmed is what the writer is told and what the ledger keeps ----------------------------------------

    private static WorkflowPublicationExecutor Executor(Rig rig, FakeWriter writer, InMemoryLedger ledger) =>
        new(rig.Service, rig.Proposals, rig.Connections, rig.Secrets, ledger, writer,
            new WorkflowPublicationOptions(true, 11, 22, Key, ["target-org/orion-api"]), new Clock());

    [Fact]
    public async Task A_confirmed_replacement_reaches_the_writer_and_the_ledger_with_the_blob_and_the_text_it_replaces()
    {
        var rig = await RigAsync();
        rig.Secrets.Values[Key] = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----";
        var plan = await PlanAsync(rig);
        var writer = new FakeWriter();
        var ledger = new InMemoryLedger();

        var record = await Executor(rig, writer, ledger).PublishAsync(Publisher, rig.Derived.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, plan.PlanSha256, default);

        var sent = Assert.Single(writer.Published);
        Assert.Equal((PublicationModes.Update, Blob, rig.Parent.ApprovedSha256), (sent.Mode, sent.ReplacesBlobSha, sent.ReplacesWorkflowSha256));
        Assert.Equal(rig.Derived.WorkflowYaml, sent.WorkflowYaml);
        Assert.Equal(rig.Derived.WorkflowSha256, sent.WorkflowSha256);
        Assert.Equal((PublicationModes.Update, Blob, rig.Parent.ApprovedSha256, PublicationStates.Published), (record.Mode, record.ReplacedBlobSha, record.ReplacedWorkflowSha256, record.State));
        Assert.Equal(rig.Derived.WorkflowSha256, record.WorkflowSha256); // the ledger names the NEW text; the replaced one is a separate fact
    }

    [Fact]
    public async Task If_the_file_changed_after_the_plan_was_shown_the_confirmation_no_longer_matches_and_nothing_is_written()
    {
        var rig = await RigAsync();
        rig.Secrets.Values[Key] = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----";
        var plan = await PlanAsync(rig);
        rig.Reader.Result = rig.Reader.Result with { WorkflowFileBlobSha = new string('d', 40) }; // somebody edited the workflow after the person looked
        var writer = new FakeWriter();
        var ledger = new InMemoryLedger();

        var refused = await Assert.ThrowsAsync<WorkflowPublicationException>(() =>
            Executor(rig, writer, ledger).PublishAsync(Publisher, rig.Derived.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, plan.PlanSha256, default));
        Assert.Equal("PublicationPlanChanged", refused.Code);
        Assert.Empty(writer.Published);
        Assert.Empty(ledger.Rows);

        // and a file that is now somebody else's is not replaced at all
        rig.Reader.Result = FakePublicationReader.FreshTarget() with { WorkflowFile = WorkflowFileState.Different };
        var other = await PlanAsync(rig);
        var blocked = await Assert.ThrowsAsync<WorkflowPublicationException>(() =>
            Executor(rig, writer, ledger).PublishAsync(Publisher, rig.Derived.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, other.PlanSha256, default));
        Assert.Equal("PublicationPlanNotReady", blocked.Code);
        Assert.Contains("WorkflowPathOccupied", blocked.Details);
        Assert.Empty(writer.Published);
    }

    [Fact]
    public async Task A_new_file_is_recorded_as_a_creation_with_nothing_replaced()
    {
        var setup = await WorkflowPublicationPlanTests.ApprovedAsync();
        setup.Secrets.Values[Key] = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----";
        var (plan, _) = await setup.Service.PlanAsync(setup.Proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        var writer = new FakeWriter();
        var executor = new WorkflowPublicationExecutor(setup.Service, setup.Proposals, setup.Connections, setup.Secrets, new InMemoryLedger(), writer,
            new WorkflowPublicationOptions(true, 11, 22, Key, ["target-org/orion-api"]), new Clock());

        var record = await executor.PublishAsync(Publisher, setup.Proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, plan.PlanSha256, default);
        Assert.Equal((PublicationModes.Create, (string?)null, (string?)null), (record.Mode, record.ReplacedBlobSha, record.ReplacedWorkflowSha256));
        var sent = Assert.Single(writer.Published);
        Assert.Equal((PublicationModes.Create, (string?)null, (string?)null), (sent.Mode, sent.ReplacesBlobSha, sent.ReplacesWorkflowSha256));
    }
}
