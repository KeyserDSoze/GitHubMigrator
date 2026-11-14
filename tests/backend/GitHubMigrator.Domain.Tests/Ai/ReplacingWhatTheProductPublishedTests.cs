using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using static GitHubMigrator.Domain.Tests.Ai.EarlierPullRequestTests;
using static GitHubMigrator.Domain.Tests.Ai.WorkflowProposalServiceTests;

namespace GitHubMigrator.Domain.Tests.Ai;

// A file at the workflow path is replaced only if it is exactly a text this product still vouches for: the approved text of the conversion a Triggers proposal came from, or a text the
// ledger says THIS PRODUCT published to this very path in this very repository, while the approval of the proposal that published it stands, for the same pipeline. A re-conversion of a
// pipeline that was published and merged is the second case. Anything else at the path is somebody's file.
public sealed class ReplacingWhatTheProductPublishedTests
{
    private static Task<(PublicationPlan Plan, string WorkflowYaml)> PlanAsync(Rig rig, WorkflowProposal proposal) =>
        rig.Service.PlanAsync(proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);

    private static string[] Named(Rig rig) => rig.Reader.Replaceable.Last();

    // Another approved conversion of the same pipeline (another text), as an earlier publication would have left it.
    private static async Task<(Guid Id, string Sha256)> ApprovedConversionAsync(Rig rig, string marker, int pipeline = 42, string project = "project", Guid? source = null)
    {
        var kept = (await rig.Proposals.RecordAsync(Author, source ?? SourceId, project, pipeline, AiId, await ConvertedAsync(Clean + $"# {marker}\n"), default))!;
        await rig.Proposals.ApproveAsync(Reviewer, kept.Id, kept.WorkflowSha256, [], "Reviewed.", default);
        return (kept.Id, kept.WorkflowSha256!);
    }

    [Fact]
    public async Task A_re_conversion_replaces_a_text_this_product_published_to_the_same_path_and_says_so_in_its_own_words()
    {
        var rig = await RigAsync(WorkflowFileState.PriorVersion);
        var earlier = await ApprovedConversionAsync(rig, "first conversion");
        rig.Ledger.Rows.Add(Attempt(earlier.Id, 7, workflow: earlier.Sha256)); // published and merged

        var (plan, _) = await PlanAsync(rig, rig.Parent);
        Assert.Equal((PublicationModes.Update, true), (plan.Mode, plan.Ready));
        Assert.Equal((Blob, earlier.Sha256), (plan.ReplacesBlobSha, plan.ReplacesWorkflowSha256));
        Assert.Equal([earlier.Sha256], Named(rig)); // the reader was told which text, and only that one, may be replaced
        var finding = Assert.Single(plan.Findings, f => f.Code == "WorkflowWillBeReplaced");
        Assert.Contains(earlier.Sha256, finding.Message);
        Assert.DoesNotContain(plan.Findings, f => f.Code == "WorkflowPathOccupied");

        // a conversion is not described as triggers
        Assert.StartsWith("Replace the GitHub Actions workflow for Azure pipeline #42", plan.PullRequestTitle);
        Assert.DoesNotContain("trigger", plan.PullRequestTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new conversion", plan.CommitMessage);
        Assert.Contains(earlier.Sha256, plan.CommitMessage);
        Assert.Contains($"| Replaces | the workflow at this path approved earlier, SHA-256 `{earlier.Sha256}` |", plan.PullRequestBody);
        Assert.Contains("- It **replaces** the file already at this path with a new conversion", plan.PullRequestBody);
        Assert.Contains("automatic triggers, for example", plan.PullRequestBody); // what may be lost is said
        Assert.Contains("runs only when started by hand", plan.PullRequestBody);
        Assert.Contains(plan.Steps, s => s.Code == "Replace");

        // the replacement is part of what a person confirms
        var fresh = await RigAsync(WorkflowFileState.Absent);
        var (create, _) = await PlanAsync(fresh, fresh.Parent);
        Assert.NotEqual(create.PlanSha256, plan.PlanSha256);
    }

    [Fact]
    public async Task A_file_nobody_vouches_for_is_somebodys_and_is_not_replaced()
    {
        var rig = await RigAsync(WorkflowFileState.PriorVersion);
        var (plan, _) = await PlanAsync(rig, rig.Parent);
        Assert.Empty(Named(rig)); // the ledger has nothing for this path, and a conversion has no parent
        Assert.Equal((PublicationModes.Create, false), (plan.Mode, plan.Ready));
        Assert.Equal("Blocker", Assert.Single(plan.Findings, f => f.Code == "WorkflowPathOccupied").Severity);
    }

    [Fact]
    public async Task Only_what_was_published_to_this_path_in_this_repository_is_named()
    {
        var rig = await RigAsync(WorkflowFileState.PriorVersion);
        var earlier = await ApprovedConversionAsync(rig, "first conversion");
        rig.Ledger.Rows.AddRange(
        [
            Attempt(earlier.Id, null, PublicationStates.Failed, writes: true, workflow: earlier.Sha256),            // failed after a write: nothing says it is there
            Attempt(earlier.Id, null, PublicationStates.FileCommitted, workflow: earlier.Sha256),                    // never finished
            Attempt(earlier.Id, 7, PublicationStates.PullRequestOpened, workflow: earlier.Sha256),                   // a pull request that is not known to be merged
            Attempt(earlier.Id, 7, workflow: earlier.Sha256, path: ".github/workflows/azdo-99-other.yml"),          // another file
            Attempt(earlier.Id, 7, workflow: earlier.Sha256, repository: "another-repo"),                           // another repository
            Attempt(earlier.Id, 7, workflow: earlier.Sha256, organization: "another-org")                           // another organization
        ]);
        var (plan, _) = await PlanAsync(rig, rig.Parent);
        Assert.Empty(Named(rig));
        Assert.Equal(PublicationModes.Create, plan.Mode);
        Assert.Contains(plan.Findings, f => f.Code == "WorkflowPathOccupied");

        // the same repository under another spelling is the same repository
        rig.Ledger.Rows.Add(Attempt(earlier.Id, 7, workflow: earlier.Sha256, repository: "ORION-API", organization: "Target-Org"));
        await PlanAsync(rig, rig.Parent);
        Assert.Equal([earlier.Sha256], Named(rig));
    }

    [Fact]
    public async Task A_published_text_is_named_only_while_the_approval_of_what_published_it_stands_for_the_same_pipeline_and_that_exact_text()
    {
        var rig = await RigAsync(WorkflowFileState.PriorVersion);
        var withdrawn = await ApprovedConversionAsync(rig, "withdrawn");
        await rig.Proposals.RevokeAsync(Reviewer, withdrawn.Id, "Found a problem.", default);
        var otherPipeline = await ApprovedConversionAsync(rig, "another pipeline", pipeline: 43);
        var otherProject = await ApprovedConversionAsync(rig, "another project", project: "another-project");
        var otherSource = await ApprovedConversionAsync(rig, "another source", source: Guid.NewGuid());
        var edited = await ApprovedConversionAsync(rig, "text differs from what was published");
        rig.Ledger.Rows.AddRange(
        [
            Attempt(withdrawn.Id, 7, workflow: withdrawn.Sha256),            // approval withdrawn since
            Attempt(Guid.NewGuid(), 7, workflow: new string('5', 64)),       // a proposal that does not exist
            Attempt(otherPipeline.Id, 7, workflow: otherPipeline.Sha256),    // another pipeline that happens to publish to this path
            Attempt(otherProject.Id, 7, workflow: otherProject.Sha256),      // pipeline 42 of another project
            Attempt(otherSource.Id, 7, workflow: otherSource.Sha256),        // pipeline 42 of another Azure organization
            Attempt(edited.Id, 7, workflow: new string('6', 64))             // the ledger names a text the proposal was not approved with
        ]);
        var (plan, _) = await PlanAsync(rig, rig.Parent);
        Assert.Empty(Named(rig));
        Assert.Equal((PublicationModes.Create, false), (plan.Mode, plan.Ready));

        // and the one that stands is named
        var standing = await ApprovedConversionAsync(rig, "standing");
        rig.Ledger.Rows.Add(Attempt(standing.Id, 7, workflow: standing.Sha256));
        await PlanAsync(rig, rig.Parent);
        Assert.Equal([standing.Sha256], Named(rig));
    }

    [Fact]
    public async Task The_service_checks_path_repository_and_organization_itself_whatever_the_ledger_returned()
    {
        var rig = await RigAsync(WorkflowFileState.PriorVersion);
        var earlier = await ApprovedConversionAsync(rig, "first conversion");
        var lax = new WorkflowPublicationService(rig.Proposals, rig.Connections, rig.Secrets, rig.Reader, new Clock(), new AllRowsLedger(rig.Ledger), rig.Observer);
        rig.Ledger.Rows.AddRange(
        [
            Attempt(earlier.Id, 7, workflow: earlier.Sha256, path: ".github/workflows/azdo-99-other.yml"),
            Attempt(earlier.Id, 7, workflow: earlier.Sha256, repository: "another-repo"),
            Attempt(earlier.Id, 7, workflow: earlier.Sha256, organization: "another-org")
        ]);
        await lax.PlanAsync(rig.Parent.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        Assert.Empty(Named(rig));
        rig.Ledger.Rows.Add(Attempt(earlier.Id, 7, workflow: earlier.Sha256, repository: "ORION-API", organization: "Target-Org"));
        await lax.PlanAsync(rig.Parent.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        Assert.Equal([earlier.Sha256], Named(rig));
    }

    [Fact]
    public async Task A_reader_that_reports_a_text_nobody_named_does_not_make_it_replaceable()
    {
        var rig = await RigAsync(WorkflowFileState.PriorVersion);
        var earlier = await ApprovedConversionAsync(rig, "first conversion");
        rig.Ledger.Rows.Add(Attempt(earlier.Id, 7, workflow: earlier.Sha256));
        rig.Reader.Result = rig.Reader.Result with { WorkflowFileSha256 = new string('9', 64) }; // a PriorVersion whose text is not the one named
        var (plan, _) = await PlanAsync(rig, rig.Parent);
        Assert.Equal((PublicationModes.Create, false), (plan.Mode, plan.Ready));
        Assert.Contains(plan.Findings, f => f.Code == "WorkflowPathOccupied");

        rig.Reader.Result = rig.Reader.Result with { WorkflowFileSha256 = null, WorkflowFileBlobSha = null }; // and one that cannot say which blob
        Assert.Contains((await PlanAsync(rig, rig.Parent)).Plan.Findings, f => f.Code == "WorkflowPathOccupied");
    }

    [Fact]
    public async Task A_triggers_proposal_may_replace_its_parents_text_and_what_the_product_published_the_parent_first_each_once_and_a_few_at_most()
    {
        var rig = await RigAsync(WorkflowFileState.PriorVersion);
        var parent = rig.Parent.ApprovedSha256!;
        var published = new List<string>();
        for (var i = 1; i <= 7; i++)
        {
            var earlier = await ApprovedConversionAsync(rig, $"conversion {i}");
            published.Add(earlier.Sha256);
            rig.Ledger.Rows.Add(Attempt(earlier.Id, 7, workflow: earlier.Sha256) with { StartedAtUtc = Now.AddMinutes(i) });
        }
        rig.Ledger.Rows.Add(Attempt(rig.Parent.Id, 7, workflow: parent) with { StartedAtUtc = Now.AddMinutes(30) }); // the parent's own text, published: named once

        var (plan, _) = await PlanAsync(rig, rig.Derived);
        var named = Named(rig);
        Assert.Equal(parent, named[0]);
        Assert.Equal(named.Length, named.Distinct().Count());
        Assert.Equal(6, named.Length);                                  // the parent, and at most five more
        Assert.Equal(published.AsEnumerable().Reverse().Take(5), named.Skip(1)); // the newest published first
        // the file is the parent's text (the first named): it is replaced with the triggers' words
        Assert.Equal((PublicationModes.Update, parent), (plan.Mode, plan.ReplacesWorkflowSha256));
        Assert.StartsWith("Add automatic triggers to the GitHub Actions workflow", plan.PullRequestTitle);
    }

    [Fact]
    public async Task A_withdrawn_parent_no_longer_names_its_text_even_though_the_product_published_it()
    {
        var rig = await RigAsync(WorkflowFileState.PriorVersion);
        rig.Ledger.Rows.Add(Attempt(rig.Parent.Id, 7, workflow: rig.Parent.ApprovedSha256!));
        await PlanAsync(rig, rig.Derived);
        Assert.Equal([rig.Parent.ApprovedSha256!], Named(rig));

        await rig.Proposals.RevokeAsync(Reviewer, rig.Parent.Id, "Found a problem in the manual-only workflow.", default);
        var (plan, _) = await PlanAsync(rig, rig.Derived);
        Assert.Empty(Named(rig));
        Assert.Equal((PublicationModes.Create, false), (plan.Mode, plan.Ready));
        Assert.Contains(plan.Findings, f => f.Code == "WorkflowPathOccupied");
    }

    [Fact]
    public async Task Without_a_ledger_only_the_parents_text_is_named_and_a_conversion_names_nothing()
    {
        var rig = await RigAsync(WorkflowFileState.PriorVersion);
        var bare = new WorkflowPublicationService(rig.Proposals, rig.Connections, rig.Secrets, rig.Reader, new Clock());
        var earlier = await ApprovedConversionAsync(rig, "first conversion");
        rig.Ledger.Rows.Add(Attempt(earlier.Id, 7, workflow: earlier.Sha256));
        await bare.PlanAsync(rig.Parent.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        Assert.Empty(Named(rig));
        await bare.PlanAsync(rig.Derived.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        Assert.Equal([rig.Parent.ApprovedSha256!], Named(rig));
    }

    [Fact]
    public async Task The_executor_replaces_exactly_the_blob_and_text_the_plan_named_for_a_conversion_and_records_it()
    {
        var rig = await RigAsync(WorkflowFileState.PriorVersion);
        var earlier = await ApprovedConversionAsync(rig, "first conversion");
        rig.Ledger.Rows.Add(Attempt(earlier.Id, 7, workflow: earlier.Sha256));
        var (plan, _) = await PlanAsync(rig, rig.Parent);

        rig.Secrets.Values["publication-key"] = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----";
        var writer = new FakeWriter();
        var executor = new WorkflowPublicationExecutor(rig.Service, rig.Connections, rig.Secrets, rig.Ledger, writer,
            new WorkflowPublicationOptions(true, 11, 22, "publication-key", ["target-org/orion-api"]), new Clock());
        var record = await executor.PublishAsync(new ProposalActor(Guid.NewGuid(), "publisher"), rig.Parent.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, plan.PlanSha256, default);

        var written = Assert.Single(writer.Published);
        Assert.Equal((PublicationModes.Update, Blob, earlier.Sha256), (written.Mode, written.ReplacesBlobSha, written.ReplacesWorkflowSha256));
        Assert.Equal((PublicationModes.Update, Blob, earlier.Sha256), (record.Mode, record.ReplacedBlobSha, record.ReplacedWorkflowSha256));
        Assert.Equal(PublicationStates.Published, record.State);
    }
}
