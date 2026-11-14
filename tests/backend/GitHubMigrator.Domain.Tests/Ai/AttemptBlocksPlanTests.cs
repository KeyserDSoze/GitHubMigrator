using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using static GitHubMigrator.Domain.Tests.Ai.WorkflowProposalServiceTests;

namespace GitHubMigrator.Domain.Tests.Ai;

// The destination can look free while the ledger still keeps THIS exact plan blocked. The plan says so before anybody confirms it (the executor would refuse it at the last moment anyway),
// only when nothing else already explains it, and without changing what the person confirms: the finding changes whether the plan is ready, never its hash.
public sealed class AttemptBlocksPlanTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now.AddHours(2); }

    private sealed record Rig(WorkflowPublicationService Service, WorkflowPublicationPlanTests.Setup Setup, InMemoryLedger Ledger, PublicationPlan Bare);

    private static async Task<Rig> RigAsync(WorkflowFileState file = WorkflowFileState.Absent)
    {
        var setup = await WorkflowPublicationPlanTests.ApprovedAsync();
        setup.Reader.Result = FakePublicationReader.FreshTarget() with { WorkflowFile = file };
        var ledger = new InMemoryLedger();
        var service = new WorkflowPublicationService(setup.Proposals, setup.Connections, setup.Secrets, setup.Reader, new Clock(), ledger, new FakePullObserver());
        var (bare, _) = await service.PlanAsync(setup.Proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default);
        return new(service, setup, ledger, bare);
    }

    private static PublicationRecord Attempt(Rig rig, string state = PublicationStates.Failed, bool writes = true, DateTimeOffset? updated = null, string? plan = null, Guid? proposal = null) =>
        new(Guid.NewGuid(), proposal ?? rig.Setup.Proposal.Id, plan ?? rig.Bare.PlanSha256, rig.Bare.WorkflowSha256, rig.Bare.Organization, rig.Bare.Repository, rig.Bare.BaseBranch, rig.Bare.BaseSha!,
            rig.Bare.HeadBranch, rig.Bare.WorkflowPath, state, state is PublicationStates.Published ? new string('e', 40) : null, state is PublicationStates.Published ? 7 : null,
            state is PublicationStates.Published ? "https://github.example/target-org/orion-api/pull/7" : null, state == PublicationStates.Failed ? "PublicationFileFailed" : null,
            state == PublicationStates.Failed ? PublicationStates.FileCommitting : null, writes, Guid.NewGuid(), "publisher", Now, updated ?? Now);

    private static async Task<PublicationPlan> PlanAsync(Rig rig) =>
        (await rig.Service.PlanAsync(rig.Setup.Proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default)).Plan;

    [Fact]
    public async Task An_attempt_of_this_exact_plan_that_failed_after_a_write_may_have_happened_blocks_the_plan_without_changing_its_hash()
    {
        var rig = await RigAsync();
        Assert.True(rig.Bare.Ready);
        rig.Ledger.Rows.Add(Attempt(rig));

        var plan = await PlanAsync(rig);
        Assert.False(plan.Ready);
        Assert.Equal(rig.Bare.PlanSha256, plan.PlanSha256);
        var finding = Assert.Single(plan.Findings, f => f.Code == "AttemptBlocksPlan");
        Assert.Equal("Blocker", finding.Severity);
        Assert.Contains("failed (PublicationFileFailed)", finding.Message);
        Assert.Contains("A superadmin can release the attempt", finding.Message);
        Assert.Contains("the base branch can move", finding.Message);
        Assert.Equal(rig.Bare.Findings.Select(f => f.Code), plan.Findings.Where(f => f.Code != "AttemptBlocksPlan").Select(f => f.Code)); // what was found before is untouched
    }

    [Fact]
    public async Task An_attempt_that_blocks_nothing_adds_nothing()
    {
        var rig = await RigAsync();
        var cases = new[]
        {
            Attempt(rig, writes: false),                                                                         // failed before any write: the plan was never blocked
            Attempt(rig) with { ReleasedAtUtc = Now, ReleasedById = Guid.NewGuid(), ReleasedBy = "admin", ReleaseNote = "Cleaned." }, // released
            Attempt(rig, plan: new string('1', 64)),                                                             // another plan: the base moved, or the text changed
            Attempt(rig, proposal: Guid.NewGuid())                                                               // another proposal
        };
        foreach (var row in cases)
        {
            rig.Ledger.Rows.Clear();
            rig.Ledger.Rows.Add(row);
            var plan = await PlanAsync(rig);
            Assert.DoesNotContain(plan.Findings, f => f.Code == "AttemptBlocksPlan");
            Assert.True(plan.Ready);
        }

        var bare = new WorkflowPublicationService(rig.Setup.Proposals, rig.Setup.Connections, rig.Setup.Secrets, rig.Setup.Reader, new Clock());
        rig.Ledger.Rows.Add(Attempt(rig));
        Assert.True((await bare.PlanAsync(rig.Setup.Proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, default)).Plan.Ready); // no ledger, nothing to say
    }

    [Fact]
    public async Task An_attempt_that_never_finished_is_running_until_it_has_not_moved_for_fifteen_minutes_and_then_it_can_be_released()
    {
        var rig = await RigAsync();
        var now = Now.AddHours(2);

        rig.Ledger.Rows.Add(Attempt(rig, PublicationStates.BranchCreated, writes: false, updated: now - TimeSpan.FromMinutes(14)));
        var running = (await PlanAsync(rig)).Findings.Single(f => f.Code == "AttemptBlocksPlan");
        Assert.Contains("still running", running.Message);
        Assert.DoesNotContain("superadmin", running.Message);

        rig.Ledger.Rows.Clear();
        rig.Ledger.Rows.Add(Attempt(rig, PublicationStates.BranchCreated, writes: false, updated: now - PublicationReleaseService.StaleAfter));
        var stale = (await PlanAsync(rig)).Findings.Single(f => f.Code == "AttemptBlocksPlan");
        Assert.Contains("never finished", stale.Message);
        Assert.Contains("A superadmin can release the attempt", stale.Message);
    }

    [Fact]
    public async Task A_published_attempt_whose_pull_request_is_gone_blocks_its_plan_but_one_the_destination_already_explains_is_not_reported_twice()
    {
        var rig = await RigAsync();
        rig.Ledger.Rows.Add(Attempt(rig, PublicationStates.Published, writes: false));
        var gone = await PlanAsync(rig);
        Assert.False(gone.Ready);
        var finding = Assert.Single(gone.Findings, f => f.Code == "AttemptBlocksPlan");
        Assert.Contains("pull request #7", finding.Message);
        Assert.Contains("closed without merging", finding.Message);

        // the branch is still there: HeadBranchExists says it, and releasing would be refused, so the plan does not offer it
        rig.Setup.Reader.Result = FakePublicationReader.FreshTarget() with { HeadBranchExists = true, HeadBranchSha = new string('9', 40) };
        var there = await PlanAsync(rig);
        Assert.False(there.Ready);
        Assert.DoesNotContain(there.Findings, f => f.Code == "AttemptBlocksPlan");
        Assert.Contains(there.Findings, f => f.Code == "HeadBranchExists");
    }

    [Fact]
    public async Task The_plan_is_not_ready_with_the_attempt_in_the_way_the_executor_refuses_it_and_once_released_the_same_plan_is_published()
    {
        var rig = await RigAsync();
        var attempt = Attempt(rig);
        rig.Ledger.Rows.Add(attempt);
        var blocked = await PlanAsync(rig);

        rig.Setup.Secrets.Values["publication-key"] = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----";
        var writer = new FakeWriter();
        var executor = new WorkflowPublicationExecutor(rig.Service, rig.Setup.Proposals, rig.Setup.Connections, rig.Setup.Secrets, rig.Ledger, writer,
            new WorkflowPublicationOptions(true, 11, 22, "publication-key", ["target-org/orion-api"]), new Clock());
        var actor = new ProposalActor(Guid.NewGuid(), "publisher");

        var refused = await Assert.ThrowsAsync<WorkflowPublicationException>(() =>
            executor.PublishAsync(actor, rig.Setup.Proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, blocked.PlanSha256, default));
        Assert.Equal("PublicationPlanNotReady", refused.Code);
        Assert.Equal(["AttemptBlocksPlan"], refused.Details);
        Assert.Empty(writer.Published);

        // a person released it after the product had looked: the same hash now goes through, and nothing about the plan changed
        Assert.True(await rig.Ledger.ReleaseAsync(attempt.Id, Guid.NewGuid(), "admin", "Branch deleted by hand.", "{}", Now.AddHours(1), default));
        var free = await PlanAsync(rig);
        Assert.True(free.Ready);
        Assert.Equal(blocked.PlanSha256, free.PlanSha256);
        var published = await executor.PublishAsync(actor, rig.Setup.Proposal.Id, WorkflowPublicationPlanTests.DestinationId, "orion-api", null, blocked.PlanSha256, default);
        Assert.Equal(PublicationStates.Published, published.State);
        Assert.Single(writer.Published);
        Assert.Equal(2, rig.Ledger.Rows.Count); // the released attempt is still there, beside the new one
    }
}
