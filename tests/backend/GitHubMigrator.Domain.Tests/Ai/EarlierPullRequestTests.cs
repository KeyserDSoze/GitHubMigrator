using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain;
using GitHubMigrator.Domain.Tests.WorkflowGates;
using static GitHubMigrator.Domain.Tests.Ai.WorkflowProposalServiceTests;

namespace GitHubMigrator.Domain.Tests.Ai;

internal sealed class FakePullObserver : IGitHubPublicationObserver
{
    public Dictionary<int, Func<PullObservation?>> Pulls { get; } = [];
    public List<(string Org, string Repo, int Number, string Head, string Token)> Asked { get; } = [];

    public Task<PublicationObservation> ObserveAsync(string organization, string repository, string baseBranch, string headBranch, string workflowPath, string workflowSha256,
        string? replacedWorkflowSha256, int? pullNumber, string token, CancellationToken ct) => throw new NotSupportedException();

    public Task<PullObservation?> ReadPullAsync(string organization, string repository, int number, string headBranch, string token, CancellationToken ct)
    {
        Asked.Add((organization, repository, number, headBranch, token));
        try { return Task.FromResult(Pulls.TryGetValue(number, out var script) ? script() : null); }
        catch (Exception ex) { return Task.FromException<PullObservation?>(ex); }
    }
}

// A file added (or replaced) while an earlier pull request of this product for the same file is still open would make the two conflict. The plan asks the ledger and reads those pull requests.
public sealed class EarlierPullRequestTests
{
    private static readonly string Blob = new('b', 40);
    private static readonly string Path = WorkflowPublicationPlanner.WorkflowPathFor(42, "Orion API CI/CD");

    private sealed record Rig(WorkflowPublicationService Service, FakePublicationReader Reader, FakePullObserver Observer, InMemoryLedger Ledger, WorkflowProposal Parent, WorkflowProposal Derived,
        WorkflowProposalService Proposals, MemorySecrets Secrets, FakeDestinations Connections);

    private static PullObservation Pull(int number, string state = "open", bool merged = false) => new(number, state, merged, new string('e', 40), "main", null);

    // An approved conversion of pipeline 42 and a Triggers proposal derived from it and approved; the ledger holds what the product wrote for the conversion.
    private static async Task<Rig> RigAsync(WorkflowFileState file = WorkflowFileState.Absent)
    {
        var repo = new InMemoryProposalRepository();
        var proposals = Service(repo);
        var parent = (await proposals.RecordAsync(Author, SourceId, "project", 42, AiId, await ConvertedAsync(Clean), default))!;
        await proposals.ApproveAsync(Reviewer, parent.Id, parent.WorkflowSha256, [], "Reviewed.", default);
        var secrets = new MemorySecrets { Values = { ["ref-source"] = "ado-pat", ["ref-1"] = WorkflowPublicationPlanTests.Token } };
        var source = new MigrationConnection(SourceId, "Azure", ProviderKeys.AzureDevOpsServices, ConnectionDirection.Source, "contoso", "ref-source", Now, Now, "Verified", "bot", "Verified");
        var factory = new WorkflowGatePipelineFactory(new FakeToolRunner("actionlint", new(true, "[]", null, 0)), new FakeToolRunner("zizmor", new(true, "[]", null, 0)));
        var derived = await new PipelineTriggerService(proposals, new FakeDestinations(source), secrets, new FakeAzureYamlSource(), factory).DeriveAsync(Author, parent.Id, default);
        await proposals.ApproveAsync(Reviewer, derived.Id, derived.WorkflowSha256, derived.Evidence.Triggers!.Differences.Select(d => d.Code).ToArray(), "Reviewed the triggers.", default);

        var reader = new FakePublicationReader { Result = FakePublicationReader.FreshTarget() with { WorkflowFile = file, WorkflowFileBlobSha = file == WorkflowFileState.PriorVersion ? Blob : null } };
        var connections = new FakeDestinations(WorkflowPublicationPlanTests.Destination());
        var ledger = new InMemoryLedger();
        var observer = new FakePullObserver();
        var service = new WorkflowPublicationService(proposals, connections, secrets, reader, new Clock(), ledger, observer);
        return new(service, reader, observer, ledger, (await repo.GetAsync(parent.Id, default))!, (await repo.GetAsync(derived.Id, default))!, proposals, secrets, connections);
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now.AddHours(2); }

    private static PublicationRecord Attempt(Guid proposal, int? pull = 7, string state = PublicationStates.Published, string repository = "orion-api", string baseBranch = "main",
        string head = "migrate/azdo-42-aaaaaaaa", string organization = "target-org", string path = "", bool writes = false) =>
        new(Guid.NewGuid(), proposal, new string('d', 64), new string('a', 64), organization, repository, baseBranch, new string('c', 40), head, path.Length == 0 ? Path : path, state, new string('e', 40),
            pull, pull is null ? null : $"https://github.example/{organization}/{repository}/pull/{pull}", state == PublicationStates.Failed ? "PublicationFileFailed" : null,
            state == PublicationStates.Failed ? PublicationStates.FileCommitting : null, writes, Guid.NewGuid(), "publisher", Now, Now);

    private static async Task<PublicationPlan> PlanAsync(Rig rig, WorkflowProposal? proposal = null) =>
        (await rig.Service.PlanAsync((proposal ?? rig.Derived).Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default)).Plan;

    [Fact]
    public async Task The_pull_request_of_the_conversion_a_triggers_proposal_came_from_blocks_the_plan_while_it_is_open()
    {
        var rig = await RigAsync();
        rig.Ledger.Rows.Add(Attempt(rig.Parent.Id, 7));
        rig.Observer.Pulls[7] = () => Pull(7);

        var plan = await PlanAsync(rig);
        Assert.False(plan.Ready);
        var finding = Assert.Single(plan.Findings, f => f.Code == "EarlierPullRequestOpen");
        Assert.Equal("Blocker", finding.Severity);
        Assert.Contains("The conversion this proposal was derived from", finding.Message);
        Assert.Contains("#7", finding.Message);
        Assert.Contains("Merge or close #7 first", finding.Message);
        // read with the destination's read-only credential, for the branch the ledger recorded
        Assert.Equal([("target-org", "orion-api", 7, "migrate/azdo-42-aaaaaaaa", WorkflowPublicationPlanTests.Token)], rig.Observer.Asked);
    }

    [Fact]
    public async Task A_merged_or_closed_pull_request_or_one_that_no_longer_exists_blocks_nothing()
    {
        var rig = await RigAsync();
        rig.Ledger.Rows.Add(Attempt(rig.Parent.Id, 7));
        rig.Ledger.Rows.Add(Attempt(rig.Parent.Id, 8, head: "migrate/azdo-42-bbbbbbbb"));
        rig.Ledger.Rows.Add(Attempt(rig.Parent.Id, 9, head: "migrate/azdo-42-cccccccc"));
        rig.Observer.Pulls[7] = () => Pull(7, "closed", merged: true);
        rig.Observer.Pulls[8] = () => Pull(8, "closed");
        // 9: the destination does not show it (null)

        var plan = await PlanAsync(rig);
        Assert.True(plan.Ready);
        Assert.DoesNotContain(plan.Findings, f => f.Code.StartsWith("EarlierPullRequest"));
        Assert.Equal([7, 8, 9], rig.Observer.Asked.Select(a => a.Number).Order());
    }

    [Fact]
    public async Task Only_attempts_for_this_file_in_this_repository_and_base_that_opened_a_pull_request_are_asked_about()
    {
        var rig = await RigAsync();
        rig.Observer.Pulls[1] = () => Pull(1);
        rig.Ledger.Rows.AddRange(
        [
            Attempt(rig.Parent.Id, 1, path: ".github/workflows/azdo-99-other.yml"),                 // another file
            Attempt(rig.Parent.Id, 1, repository: "another-repo"),                                  // another repository
            Attempt(rig.Parent.Id, 1, organization: "another-org"),                                 // another organization
            Attempt(rig.Parent.Id, 1, baseBranch: "release/2026"),                                  // another base: no conflict on this one
            Attempt(rig.Parent.Id, null, PublicationStates.Failed, writes: true),                   // nothing opened
            Attempt(rig.Parent.Id, 1, PublicationStates.Failed, writes: true),                      // a failed attempt that somehow has a number is not "published"
            Attempt(rig.Parent.Id, 1, PublicationStates.FileCommitted),                             // still running: no pull request yet
            Attempt(rig.Parent.Id, 1, head: rig.Derived.WorkflowSha256 is { } sha ? $"migrate/azdo-42-{sha[..8]}" : "") // this plan's own branch is HeadBranchExists's business
        ]);
        var plan = await PlanAsync(rig);
        Assert.Empty(rig.Observer.Asked);
        Assert.True(plan.Ready);
    }

    [Fact]
    public async Task Any_earlier_publication_of_the_file_counts_not_only_the_parent_and_the_repository_name_is_not_case_sensitive()
    {
        var rig = await RigAsync();
        // another conversion of the same pipeline, published earlier and still open, and an earlier Triggers proposal's pull request
        rig.Ledger.Rows.Add(Attempt(Guid.NewGuid(), 5, repository: "Orion-API", head: "migrate/azdo-42-dddddddd"));
        rig.Ledger.Rows.Add(Attempt(rig.Derived.Id, 6, head: "migrate/azdo-42-eeeeeeee"));
        rig.Observer.Pulls[5] = () => Pull(5);
        rig.Observer.Pulls[6] = () => Pull(6);

        var plan = await PlanAsync(rig);
        Assert.False(plan.Ready);
        var open = plan.Findings.Where(f => f.Code == "EarlierPullRequestOpen").Select(f => f.Message).ToArray();
        Assert.Equal(2, open.Length);
        Assert.Contains(open, m => m.StartsWith("An earlier publication of this workflow file") && m.Contains("#5"));
        Assert.Contains(open, m => m.StartsWith("An earlier publication of this proposal") && m.Contains("#6"));
    }

    [Fact]
    public async Task A_pull_request_that_cannot_be_read_is_a_warning_not_assumed_closed_and_does_not_by_itself_block()
    {
        foreach (Exception failure in new Exception[] { new HttpRequestException("TargetUnreadable"), new InvalidOperationException("bad") })
        {
            var rig = await RigAsync();
            rig.Ledger.Rows.Add(Attempt(rig.Parent.Id, 7));
            rig.Observer.Pulls[7] = () => throw failure;
            var plan = await PlanAsync(rig);
            var warning = Assert.Single(plan.Findings, f => f.Code == "EarlierPullRequestUnchecked");
            Assert.Equal("Warning", warning.Severity);
            Assert.Contains("#7", warning.Message);
            Assert.True(plan.Ready);
        }
    }

    [Fact]
    public async Task A_replacement_is_checked_too_and_a_taken_path_or_an_unreadable_destination_costs_no_further_read()
    {
        var update = await RigAsync(WorkflowFileState.PriorVersion);
        update.Ledger.Rows.Add(Attempt(Guid.NewGuid(), 4, head: "migrate/azdo-42-ffffffff"));
        update.Observer.Pulls[4] = () => Pull(4);
        var replacing = await PlanAsync(update);
        Assert.Equal(PublicationModes.Update, replacing.Mode);
        Assert.False(replacing.Ready);
        Assert.Contains(replacing.Findings, f => f.Code == "EarlierPullRequestOpen");

        foreach (var state in new[] { WorkflowFileState.Different, WorkflowFileState.Identical })
        {
            var taken = await RigAsync(state);
            taken.Ledger.Rows.Add(Attempt(taken.Parent.Id, 7));
            taken.Observer.Pulls[7] = () => Pull(7);
            await PlanAsync(taken);
            Assert.Empty(taken.Observer.Asked);
        }
        var unreadable = await RigAsync();
        unreadable.Ledger.Rows.Add(Attempt(unreadable.Parent.Id, 7));
        unreadable.Reader.Throw = new HttpRequestException("down");
        var plan = await PlanAsync(unreadable);
        Assert.Contains(plan.Findings, f => f.Code == "TargetUnreadable");
        Assert.Empty(unreadable.Observer.Asked);
    }

    [Fact]
    public async Task At_most_three_pull_requests_are_read_each_number_once_and_the_newest_attempt_of_a_number_is_the_one_named()
    {
        var rig = await RigAsync();
        for (var n = 1; n <= 5; n++) rig.Ledger.Rows.Add(Attempt(rig.Parent.Id, n, head: $"migrate/azdo-42-0000000{n}"));
        // several attempts opened #5 (a re-publication after the branch was deleted, say): it is one pull request, named by the newest attempt, and it must not use up the cap
        foreach (var day in new[] { 1, 2, 3 }) rig.Ledger.Rows.Add(Attempt(rig.Parent.Id, 5, head: $"migrate/azdo-42-0000009{day}") with { StartedAtUtc = Now.AddDays(day) });
        foreach (var n in Enumerable.Range(1, 5)) rig.Observer.Pulls[n] = () => Pull(n);

        await PlanAsync(rig);
        Assert.Equal(3, rig.Observer.Asked.Count);
        Assert.Equal(3, rig.Observer.Asked.Select(a => a.Number).Distinct().Count());
        Assert.Equal("migrate/azdo-42-00000093", rig.Observer.Asked.Single(a => a.Number == 5).Head);
    }

    // a ledger that answers with everything it has, whatever was asked for: the service does not rely on the ledger having filtered
    private sealed class AllRowsLedger(InMemoryLedger inner) : IPublicationLedger
    {
        public Task<PublicationRecord?> TryStartAsync(PublicationRecord started, CancellationToken ct) => inner.TryStartAsync(started, ct);
        public Task<bool> AdvanceAsync(Guid id, string from, string to, string? commitSha, int? pullNumber, string? pullUrl, DateTimeOffset at, CancellationToken ct) => inner.AdvanceAsync(id, from, to, commitSha, pullNumber, pullUrl, at, ct);
        public Task FailAsync(Guid id, string errorCode, bool writesPossible, DateTimeOffset at, CancellationToken ct) => inner.FailAsync(id, errorCode, writesPossible, at, ct);
        public Task<PublicationRecord?> GetAsync(Guid id, CancellationToken ct) => inner.GetAsync(id, ct);
        public Task<IReadOnlyList<PublicationRecord>> ListAsync(Guid proposalId, CancellationToken ct) => inner.ListAsync(proposalId, ct);
        public Task<IReadOnlyList<PublicationRecord>> ListForPathAsync(string organization, string repository, string workflowPath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PublicationRecord>>(inner.Rows.ToArray());
        public Task<bool> AbandonAsync(Guid id, DateTimeOffset staleBefore, DateTimeOffset at, CancellationToken ct) => inner.AbandonAsync(id, staleBefore, at, ct);
        public Task<bool> ReleaseAsync(Guid id, Guid byId, string by, string note, string evidenceJson, DateTimeOffset at, CancellationToken ct) => inner.ReleaseAsync(id, byId, by, note, evidenceJson, at, ct);
    }

    [Fact]
    public async Task The_service_checks_organization_repository_and_base_itself_whatever_the_ledger_returned()
    {
        var rig = await RigAsync();
        var lax = new WorkflowPublicationService(rig.Proposals, rig.Connections, rig.Secrets, rig.Reader, new Clock(), new AllRowsLedger(rig.Ledger), rig.Observer);
        rig.Observer.Pulls[1] = () => Pull(1);
        rig.Ledger.Rows.AddRange([Attempt(rig.Parent.Id, 1, repository: "another-repo"), Attempt(rig.Parent.Id, 1, organization: "another-org"), Attempt(rig.Parent.Id, 1, baseBranch: "release/2026")]);
        var (plan, _) = await lax.PlanAsync(rig.Derived.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        Assert.Empty(rig.Observer.Asked);
        Assert.True(plan.Ready);

        rig.Ledger.Rows.Add(Attempt(rig.Parent.Id, 1, repository: "ORION-API", organization: "Target-Org"));
        var (blocked, _) = await lax.PlanAsync(rig.Derived.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        Assert.False(blocked.Ready); // the same repository under another spelling is the same repository
    }

    [Fact]
    public async Task A_conversion_is_checked_the_same_way_and_without_a_ledger_or_observer_nothing_is_asked()
    {
        // a second conversion of the pipeline while the first one's pull request is open
        var setup = await WorkflowPublicationPlanTests.ApprovedAsync();
        var ledger = new InMemoryLedger();
        var observer = new FakePullObserver();
        ledger.Rows.Add(Attempt(Guid.NewGuid(), 3, head: "migrate/azdo-42-12345678"));
        observer.Pulls[3] = () => Pull(3);
        var service = new WorkflowPublicationService(setup.Proposals, setup.Connections, setup.Secrets, setup.Reader, new Clock(), ledger, observer);
        var (plan, _) = await service.PlanAsync(setup.Proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        Assert.False(plan.Ready);
        Assert.Contains(plan.Findings, f => f.Code == "EarlierPullRequestOpen" && f.Message.StartsWith("An earlier publication of this workflow file"));

        var bare = new WorkflowPublicationService(setup.Proposals, setup.Connections, setup.Secrets, setup.Reader, new Clock());
        var (unchecked_, _) = await bare.PlanAsync(setup.Proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        Assert.True(unchecked_.Ready);
    }

    [Fact]
    public async Task The_finding_changes_whether_the_plan_is_ready_and_not_its_hash_and_the_executor_refuses_it()
    {
        var rig = await RigAsync();
        var before = await PlanAsync(rig);
        Assert.True(before.Ready);
        rig.Ledger.Rows.Add(Attempt(rig.Parent.Id, 7));
        rig.Observer.Pulls[7] = () => Pull(7);
        var blocked = await PlanAsync(rig);
        Assert.False(blocked.Ready);
        Assert.Equal(before.PlanSha256, blocked.PlanSha256); // what the person confirms is the same plan; only whether it may go ahead changed

        rig.Secrets.Values["publication-key"] = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----";
        var writer = new FakeWriter();
        var publishLedger = new InMemoryLedger();
        var executor = new WorkflowPublicationExecutor(rig.Service, rig.Proposals, rig.Connections, rig.Secrets, publishLedger, writer,
            new WorkflowPublicationOptions(true, 11, 22, "publication-key", ["target-org/orion-api"]), new Clock());
        var refused = await Assert.ThrowsAsync<WorkflowPublicationException>(() =>
            executor.PublishAsync(new ProposalActor(Guid.NewGuid(), "publisher"), rig.Derived.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, before.PlanSha256, default));
        Assert.Equal("PublicationPlanNotReady", refused.Code);
        Assert.Contains("EarlierPullRequestOpen", refused.Details);
        Assert.Empty(writer.Published);
        Assert.Empty(publishLedger.Rows);
    }
}
