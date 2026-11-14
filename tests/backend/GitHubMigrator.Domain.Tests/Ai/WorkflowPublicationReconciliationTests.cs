using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using static GitHubMigrator.Domain.Tests.Ai.WorkflowProposalServiceTests;

namespace GitHubMigrator.Domain.Tests.Ai;

// Setting what the ledger says beside what the destination shows. The verdict is what is there; the findings say what to make of it and never ask the
// product to act: nothing here retries, cleans up or changes a state.
public sealed class PublicationReconcilerTests
{
    private static readonly string BaseSha = new('c', 40);
    private static readonly string CommitSha = new('e', 40);
    private static readonly string OtherSha = new('9', 40);

    private static PublicationRecord Record(string state = PublicationStates.Published, bool writes = false, int? pull = 7, string? commit = "commit", string mode = PublicationModes.Create) =>
        new(Guid.NewGuid(), Guid.NewGuid(), new string('d', 64), new string('a', 64), "target-org", "orion-api", "main", BaseSha, "migrate/azdo-42-aaaaaaaa",
            ".github/workflows/azdo-42-x.yml", state, commit == "commit" ? CommitSha : commit, pull, pull is null ? null : "https://github.example/target-org/orion-api/pull/" + pull,
            state == PublicationStates.Failed ? "PublicationFileFailed" : null, state == PublicationStates.Failed ? PublicationStates.FileCommitting : null, writes,
            Guid.NewGuid(), "publisher", Now, Now, mode, mode == PublicationModes.Update ? new string('b', 40) : null, mode == PublicationModes.Update ? new string('f', 64) : null);

    private static PullObservation Pull(int number = 7, string state = "open", bool merged = false, string? head = "commit") =>
        new(number, state, merged, head == "commit" ? CommitSha : head, "main", merged ? new string('3', 40) : null);

    private static PublicationObservation Seen(string? head = "commit", string? tip = "base", WorkflowFileState onHead = WorkflowFileState.Identical, WorkflowFileState onBase = WorkflowFileState.Absent,
        bool archived = false, PullObservation[]? pulls = null) =>
        new(true, archived, tip == "base" ? BaseSha : tip, head == "commit" ? CommitSha : head, pulls ?? [], onHead, onBase);

    private static PublicationReconciliation Assess(PublicationRecord record, PublicationObservation? seen, bool unreadable = false) =>
        PublicationReconciler.Assess(record, seen, unreadable, Now);

    private static string[] Codes(PublicationReconciliation r) => r.Findings.Select(f => f.Code).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void A_published_attempt_whose_pull_request_is_open_at_the_commit_the_product_wrote_is_awaiting_a_person_and_nothing_is_said_against_it()
    {
        var r = Assess(Record(), Seen(pulls: [Pull()]));
        Assert.Equal(ReconciliationVerdicts.OpenPullRequest, r.Verdict);
        Assert.Empty(r.Findings);
        Assert.Equal((PublicationStates.Published, false, CommitSha, 7), (r.LedgerState, r.LedgerWritesPossible, r.LedgerCommitSha, r.LedgerPullNumber));
    }

    [Fact]
    public void An_open_pull_request_whose_branch_moved_or_whose_file_is_not_the_approved_text_is_called_changed_and_warned_about()
    {
        var moved = Assess(Record(), Seen(head: OtherSha, pulls: [Pull(head: OtherSha)]));
        Assert.Equal(ReconciliationVerdicts.OpenPullRequestChanged, moved.Verdict);
        Assert.Equal(["BranchMoved", "OpenPullRequestChanged"], Codes(moved));
        Assert.All(moved.Findings, f => Assert.Equal("Warning", f.Severity));

        var edited = Assess(Record(), Seen(onHead: WorkflowFileState.Different, pulls: [Pull()]));
        Assert.Equal(ReconciliationVerdicts.OpenPullRequestChanged, edited.Verdict);
        Assert.Equal(["OpenPullRequestChanged"], Codes(edited));
        Assert.Contains("not the approved text", edited.Findings[0].Message);

        // the head of the pull request says one thing and the branch another
        Assert.Equal(ReconciliationVerdicts.OpenPullRequestChanged, Assess(Record(), Seen(pulls: [Pull(head: OtherSha)])).Verdict);
    }

    [Fact]
    public void A_merged_pull_request_is_merged_only_when_the_base_branch_has_the_approved_text()
    {
        var merged = Assess(Record(), Seen(onBase: WorkflowFileState.Identical, pulls: [Pull(state: "closed", merged: true)]));
        Assert.Equal(ReconciliationVerdicts.Merged, merged.Verdict);
        Assert.Empty(merged.Findings);

        var changed = Assess(Record(), Seen(onBase: WorkflowFileState.Different, pulls: [Pull(state: "closed", merged: true)]));
        Assert.Equal((ReconciliationVerdicts.MergedBaseDiffers, "Warning"), (changed.Verdict, changed.Findings.Single().Severity));
        Assert.Contains("changed after", changed.Findings.Single().Message);

        var removed = Assess(Record(), Seen(onBase: WorkflowFileState.Absent, pulls: [Pull(state: "closed", merged: true)]));
        Assert.Equal(ReconciliationVerdicts.MergedBaseDiffers, removed.Verdict);
        Assert.Contains("no file", removed.Findings.Single().Message);

        // the base moved because of the merge itself: said as information, not as a problem
        var moved = Assess(Record(), Seen(tip: OtherSha, onBase: WorkflowFileState.Identical, pulls: [Pull(state: "closed", merged: true)]));
        Assert.Equal(ReconciliationVerdicts.Merged, moved.Verdict);
        Assert.Equal([("BaseMoved", "Info")], moved.Findings.Select(f => (f.Code, f.Severity)));
    }

    [Fact]
    public void A_pull_request_closed_without_merging_left_nothing_on_the_base_branch()
    {
        var r = Assess(Record(), Seen(pulls: [Pull(state: "closed")]));
        Assert.Equal(ReconciliationVerdicts.ClosedWithoutMerge, r.Verdict);
        Assert.Equal([("ClosedWithoutMerge", "Info")], r.Findings.Select(f => (f.Code, f.Severity)));
    }

    [Fact]
    public void An_attempt_that_may_have_written_and_left_a_branch_with_no_pull_request_says_what_is_on_the_branch_and_that_the_product_will_not_act()
    {
        var identical = Assess(Record(PublicationStates.Failed, writes: true, pull: null, commit: null), Seen(head: OtherSha, onHead: WorkflowFileState.Identical));
        Assert.Equal(ReconciliationVerdicts.BranchWithoutPullRequest, identical.Verdict);
        Assert.Equal(["BranchWithoutPullRequest", "ProductWillNotRetry"], Codes(identical));
        Assert.Contains("holds the approved text", identical.Findings.First(f => f.Code == "BranchWithoutPullRequest").Message);

        var empty = Assess(Record(PublicationStates.Failed, writes: true, pull: null, commit: null), Seen(head: OtherSha, onHead: WorkflowFileState.Absent));
        Assert.Contains("no workflow file", empty.Findings.First(f => f.Code == "BranchWithoutPullRequest").Message);
        var other = Assess(Record(PublicationStates.Failed, writes: true, pull: null, commit: null), Seen(head: OtherSha, onHead: WorkflowFileState.Different));
        Assert.Contains("not the approved text", other.Findings.First(f => f.Code == "BranchWithoutPullRequest").Message);
    }

    [Fact]
    public void An_attempt_that_may_have_written_and_left_nothing_is_nothing_found_and_the_ledger_still_blocks_that_exact_plan()
    {
        var r = Assess(Record(PublicationStates.Failed, writes: true, pull: null, commit: null), Seen(head: null, onHead: WorkflowFileState.Absent));
        Assert.Equal(ReconciliationVerdicts.NothingFound, r.Verdict);
        Assert.Equal(["LedgerStillBlocksPlan", "NothingFound"], Codes(r));
        Assert.Contains("A superadmin can release it", r.Findings.Single(f => f.Code == "LedgerStillBlocksPlan").Message);
        Assert.Contains("the base branch can move", r.Findings.Single(f => f.Code == "LedgerStillBlocksPlan").Message);
        Assert.DoesNotContain("ProductWillNotRetry", Codes(r));
        Assert.True(r.LedgerBlocksPlan);
        Assert.False(r.LedgerReleased);

        // a failure before any write that left nothing is just nothing
        var clean = Assess(Record(PublicationStates.Failed, writes: false, pull: null, commit: null), Seen(head: null, onHead: WorkflowFileState.Absent));
        Assert.Equal(ReconciliationVerdicts.NothingFound, clean.Verdict);
        Assert.Equal(["NothingFound"], Codes(clean));
    }

    [Fact]
    public void A_release_is_not_offered_while_the_branch_exists_and_a_released_attempt_says_so_and_no_longer_blocks()
    {
        var left = Assess(Record(PublicationStates.Failed, writes: true, pull: null, commit: null), Seen(head: OtherSha, onHead: WorkflowFileState.Absent));
        Assert.Equal(["BranchWithoutPullRequest", "ProductWillNotRetry"], Codes(left));
        Assert.True(left.LedgerBlocksPlan);

        // a closed pull request does not make the branch go away: with the branch still there nothing is offered either
        var closedButBranch = Assess(Record(PublicationStates.Failed, writes: true, pull: null, commit: null), Seen(head: OtherSha, onHead: WorkflowFileState.Absent, pulls: [Pull(state: "closed", head: OtherSha)]));
        Assert.Equal(ReconciliationVerdicts.ClosedWithoutMerge, closedButBranch.Verdict);
        Assert.Equal(["ClosedWithoutMerge", "ProductWillNotRetry"], Codes(closedButBranch));
        Assert.True(closedButBranch.LedgerBlocksPlan);

        var released = Assess(Record(PublicationStates.Failed, writes: true, pull: null, commit: null) with { ReleasedAtUtc = Now, ReleasedById = Guid.NewGuid(), ReleasedBy = "admin", ReleaseNote = "Deleted the branch." },
            Seen(head: null, onHead: WorkflowFileState.Absent));
        Assert.Equal(ReconciliationVerdicts.NothingFound, released.Verdict);
        Assert.Equal(["AttemptReleased", "NothingFound"], Codes(released));
        Assert.Equal("Info", released.Findings.Single(f => f.Code == "AttemptReleased").Severity);
        Assert.Contains("released this attempt", released.Findings.Single(f => f.Code == "AttemptReleased").Message);
        Assert.True(released.LedgerReleased);
        Assert.False(released.LedgerBlocksPlan);
        Assert.DoesNotContain("LedgerStillBlocksPlan", Codes(released));

        // what the attempt left is still reported on a released attempt, but the release does not claim it is gone
        var again = Assess(Record(PublicationStates.Failed, writes: true, pull: null, commit: null) with { ReleasedAtUtc = Now, ReleasedById = Guid.NewGuid(), ReleasedBy = "admin", ReleaseNote = "Deleted the branch." },
            Seen(head: OtherSha, onHead: WorkflowFileState.Absent));
        Assert.Equal(ReconciliationVerdicts.BranchWithoutPullRequest, again.Verdict);
        Assert.Contains("AttemptReleased", Codes(again));
        Assert.DoesNotContain("ProductWillNotRetry", Codes(again));
    }

    [Fact]
    public void A_published_attempt_whose_pull_request_was_closed_and_whose_branch_is_gone_still_blocks_its_plan_and_can_be_released_but_not_while_the_branch_or_the_pull_request_is_there()
    {
        var closedGone = Assess(Record(), Seen(head: null, onHead: WorkflowFileState.Absent, pulls: [Pull(state: "closed")]));
        Assert.Equal(ReconciliationVerdicts.ClosedWithoutMerge, closedGone.Verdict);
        Assert.Equal(["ClosedWithoutMerge", "LedgerStillBlocksPlan"], Codes(closedGone));
        Assert.True(closedGone.LedgerBlocksPlan);

        var branchThere = Assess(Record(), Seen(pulls: [Pull(state: "closed")]));
        Assert.Equal(["ClosedWithoutMerge"], Codes(branchThere));
        var open = Assess(Record(), Seen(head: null, onHead: WorkflowFileState.Absent, pulls: [Pull()]));
        Assert.DoesNotContain("LedgerStillBlocksPlan", Codes(open));
        var merged = Assess(Record(), Seen(head: null, onHead: WorkflowFileState.Absent, onBase: WorkflowFileState.Identical, pulls: [Pull(state: "closed", merged: true)]));
        Assert.DoesNotContain("LedgerStillBlocksPlan", Codes(merged));

        var released = Assess(Record() with { ReleasedAtUtc = Now, ReleasedById = Guid.NewGuid(), ReleasedBy = "admin", ReleaseNote = "Closed on purpose." },
            Seen(head: null, onHead: WorkflowFileState.Absent, pulls: [Pull(state: "closed")]));
        Assert.Equal(["AttemptReleased", "ClosedWithoutMerge"], Codes(released));
        Assert.False(released.LedgerBlocksPlan);
    }

    [Fact]
    public void An_attempt_the_ledger_says_wrote_nothing_but_whose_branch_exists_is_an_inconsistency_not_a_clean_failure()
    {
        var r = Assess(Record(PublicationStates.Failed, writes: false, pull: null, commit: null), Seen(head: OtherSha, onHead: WorkflowFileState.Absent));
        Assert.Equal(ReconciliationVerdicts.BranchWithoutPullRequest, r.Verdict);
        Assert.Contains("UnexpectedObjects", Codes(r));
        Assert.Equal("Warning", r.Findings.Single(f => f.Code == "UnexpectedObjects").Severity);
    }

    [Fact]
    public void An_attempt_that_did_not_finish_is_flagged_and_what_exists_is_still_reported()
    {
        var r = Assess(Record(PublicationStates.FileCommitted, pull: null), Seen(pulls: []));
        Assert.Equal(ReconciliationVerdicts.BranchWithoutPullRequest, r.Verdict);
        Assert.Contains("AttemptNotFinished", Codes(r));
        Assert.Contains(PublicationStates.FileCommitted, r.Findings.Single(f => f.Code == "AttemptNotFinished").Message);
    }

    [Fact]
    public void A_published_attempt_whose_pull_request_the_destination_does_not_show_is_a_discrepancy()
    {
        var withNumber = Assess(Record(), Seen(onHead: WorkflowFileState.Identical, pulls: []));
        Assert.Equal(ReconciliationVerdicts.BranchWithoutPullRequest, withNumber.Verdict);
        Assert.Contains("PullRequestMissing", Codes(withNumber));
        Assert.Contains("#7", withNumber.Findings.First(f => f.Code == "PullRequestMissing").Message);

        var branchGone = Assess(Record(), Seen(head: null, onHead: WorkflowFileState.Absent, pulls: []));
        Assert.Equal(ReconciliationVerdicts.NothingFound, branchGone.Verdict);
        Assert.Contains("PullRequestMissing", Codes(branchGone));

        var noNumber = Assess(Record(pull: null), Seen(head: null, onHead: WorkflowFileState.Absent, pulls: []));
        Assert.Contains("PullRequestMissing", Codes(noNumber));
    }

    [Fact]
    public void The_branch_name_is_the_attempts_so_several_pull_requests_from_it_are_named_and_the_one_the_ledger_recorded_is_the_one_judged()
    {
        var unrecorded = Assess(Record(PublicationStates.Failed, writes: true, pull: null, commit: null), Seen(pulls: [Pull(9), Pull(8, "closed")]));
        Assert.Contains("MultiplePullRequests", Codes(unrecorded));
        Assert.Equal(ReconciliationVerdicts.OpenPullRequest, unrecorded.Verdict); // the newest, which is open

        var recorded = Assess(Record(pull: 7), Seen(pulls: [Pull(9, "closed"), Pull(7)]));
        Assert.Equal(ReconciliationVerdicts.OpenPullRequest, recorded.Verdict);
        Assert.Equal(["OtherPullRequests"], Codes(recorded));
        Assert.Equal("Info", recorded.Findings.Single().Severity);

        // one pull request and no number recorded: that one is the attempt's
        var single = Assess(Record(PublicationStates.Failed, writes: true, pull: null, commit: null), Seen(pulls: [Pull(5, "closed", merged: true)], onBase: WorkflowFileState.Identical));
        Assert.Equal(ReconciliationVerdicts.Merged, single.Verdict);
        Assert.DoesNotContain("MultiplePullRequests", Codes(single));
    }

    [Fact]
    public void Text_already_on_the_base_with_nothing_of_the_attempt_left_is_said_so_and_a_missing_base_is_not_read_as_an_empty_file()
    {
        var already = Assess(Record(PublicationStates.Failed, writes: true, pull: null, commit: null), Seen(head: null, onHead: WorkflowFileState.Absent, onBase: WorkflowFileState.Identical));
        Assert.Equal(ReconciliationVerdicts.AlreadyOnBase, already.Verdict);

        var noBase = Assess(Record(), Seen(tip: null, onBase: WorkflowFileState.Absent, pulls: [Pull()]));
        Assert.Contains("BaseBranchNotFound", Codes(noBase));
        Assert.Equal("Warning", noBase.Findings.Single(f => f.Code == "BaseBranchNotFound").Severity);
        Assert.DoesNotContain("BaseMoved", Codes(noBase));
    }

    [Fact]
    public void An_unreadable_destination_or_a_missing_repository_says_nothing_about_what_exists_and_an_archived_one_is_information()
    {
        foreach (var r in new[] { Assess(Record(), null, unreadable: true), Assess(Record(), Seen(), unreadable: true) })
        {
            Assert.Equal(ReconciliationVerdicts.TargetUnreadable, r.Verdict);
            Assert.Equal(["TargetUnreadable"], Codes(r));
        }
        Assert.Null(Assess(Record(), null, unreadable: true).Observed);

        var missing = Assess(Record(), new PublicationObservation(false, false, null, null, [], WorkflowFileState.Absent, WorkflowFileState.Absent));
        Assert.Equal(ReconciliationVerdicts.RepositoryNotFound, missing.Verdict);
        Assert.Equal(["RepositoryNotFound"], Codes(missing));

        var archived = Assess(Record(), Seen(archived: true, pulls: [Pull()]));
        Assert.Equal(ReconciliationVerdicts.OpenPullRequest, archived.Verdict);
        Assert.Equal([("RepositoryArchived", "Info")], archived.Findings.Select(f => (f.Code, f.Severity)));
    }

    [Fact]
    public void An_attempt_with_no_commit_recorded_cannot_be_called_moved()
    {
        var r = Assess(Record(PublicationStates.Failed, writes: true, pull: null, commit: null), Seen(head: OtherSha, onHead: WorkflowFileState.Identical, pulls: [Pull(head: OtherSha)]));
        Assert.DoesNotContain("BranchMoved", Codes(r));
        Assert.Equal(ReconciliationVerdicts.OpenPullRequest, r.Verdict);
    }
}

public sealed class PublicationReconciliationServiceTests
{
    private sealed class FakeObserver : IGitHubPublicationObserver
    {
        public List<(string Org, string Repo, string Base, string Head, string Path, string Sha, string? Replaced, int? Pull, string Token)> Calls { get; } = [];
        public PublicationObservation Result { get; set; } = new(true, false, new string('c', 40), null, [], WorkflowFileState.Absent, WorkflowFileState.Absent);
        public Exception? Throw { get; set; }

        public Task<PullObservation?> ReadPullAsync(string organization, string repository, int number, string headBranch, string token, CancellationToken ct) => throw new NotSupportedException();

        public Task<PublicationObservation> ObserveAsync(string organization, string repository, string baseBranch, string headBranch, string workflowPath, string workflowSha256,
            string? replacedWorkflowSha256, int? pullNumber, string token, CancellationToken ct)
        {
            Calls.Add((organization, repository, baseBranch, headBranch, workflowPath, workflowSha256, replacedWorkflowSha256, pullNumber, token));
            return Throw is null ? Task.FromResult(Result) : Task.FromException<PublicationObservation>(Throw);
        }
    }

    private sealed record Rig(PublicationReconciliationService Service, FakeObserver Observer, InMemoryLedger Ledger, PublicationRecord Record, MemorySecrets Secrets);

    private static async Task<Rig> RigAsync(MigrationConnection? destination = null, string? token = "destination-pat", PublicationRecord? record = null)
    {
        var ledger = new InMemoryLedger();
        var row = record ?? new PublicationRecord(Guid.NewGuid(), Guid.NewGuid(), new string('d', 64), new string('a', 64), "target-org", "orion-api", "main", new string('c', 40),
            "migrate/azdo-42-aaaaaaaa", ".github/workflows/azdo-42-x.yml", PublicationStates.Failed, null, null, null, "PublicationFileFailed", PublicationStates.FileCommitting, true,
            Guid.NewGuid(), "publisher", Now, Now, PublicationModes.Update, new string('b', 40), new string('f', 64));
        ledger.Rows.Add(row);
        var secrets = new MemorySecrets();
        if (token is not null) secrets.Values["ref-1"] = token;
        var observer = new FakeObserver();
        var service = new PublicationReconciliationService(ledger, new FakeDestinations(destination ?? WorkflowPublicationPlanTests.Destination()), secrets, observer, TimeProvider.System);
        await Task.CompletedTask;
        return new(service, observer, ledger, row, secrets);
    }

    private static Task<PublicationReconciliation> ReconcileAsync(Rig rig, Guid? proposal = null, Guid? publication = null, Guid? destination = null) =>
        rig.Service.ReconcileAsync(proposal ?? rig.Record.ProposalId, publication ?? rig.Record.Id, destination ?? WorkflowPublicationPlanTests.DestinationId, default);

    [Fact]
    public async Task It_reads_with_the_destination_credential_what_the_ledger_recorded_and_gives_the_assessment()
    {
        var rig = await RigAsync();
        var result = await ReconcileAsync(rig);

        var call = Assert.Single(rig.Observer.Calls);
        Assert.Equal(("target-org", "orion-api", "main", "migrate/azdo-42-aaaaaaaa", ".github/workflows/azdo-42-x.yml", new string('a', 64), new string('f', 64), (int?)null, "destination-pat"),
            (call.Org, call.Repo, call.Base, call.Head, call.Path, call.Sha, call.Replaced, call.Pull, call.Token)); // the text an update replaced is named so it can be recognised
        Assert.Equal((ReconciliationVerdicts.NothingFound, rig.Record.Id), (result.Verdict, result.PublicationId));
        Assert.Contains("LedgerStillBlocksPlan", result.Findings.Select(f => f.Code));
        // a view: the ledger is as it was
        Assert.Equal(rig.Record, Assert.Single(rig.Ledger.Rows));
    }

    [Fact]
    public async Task The_number_the_ledger_recorded_is_passed_on_so_it_can_be_looked_at_directly()
    {
        var rig = await RigAsync(record: new PublicationRecord(Guid.NewGuid(), Guid.NewGuid(), new string('d', 64), new string('a', 64), "target-org", "orion-api", "main", new string('c', 40),
            "migrate/azdo-42-aaaaaaaa", ".github/workflows/azdo-42-x.yml", PublicationStates.Published, new string('e', 40), 7, "https://github.example/target-org/orion-api/pull/7", null, null, false,
            Guid.NewGuid(), "publisher", Now, Now));
        await ReconcileAsync(rig);
        Assert.Equal(7, rig.Observer.Calls.Single().Pull);
        Assert.Null(rig.Observer.Calls.Single().Replaced); // a creation replaced nothing
    }

    [Fact]
    public async Task An_attempt_of_another_proposal_or_one_that_does_not_exist_is_not_found_and_nothing_is_read()
    {
        var rig = await RigAsync();
        foreach (var (proposal, publication) in new (Guid?, Guid?)[] { (Guid.NewGuid(), null), (null, Guid.NewGuid()) })
        {
            var ex = await Assert.ThrowsAsync<WorkflowPublicationException>(() => ReconcileAsync(rig, proposal, publication));
            Assert.Equal("PublicationNotFound", ex.Code);
        }
        Assert.Empty(rig.Observer.Calls);
    }

    [Fact]
    public async Task Only_a_verified_github_destination_of_the_organization_the_attempt_wrote_to_is_used_and_it_must_have_a_credential()
    {
        var rig = await RigAsync();
        Assert.Equal("DestinationNotFound", (await Assert.ThrowsAsync<WorkloadInputException>(() => ReconcileAsync(rig, destination: Guid.NewGuid()))).Code);

        foreach (var wrong in new[]
        {
            WorkflowPublicationPlanTests.Destination(direction: ConnectionDirection.Source),
            WorkflowPublicationPlanTests.Destination(provider: ProviderKeys.AzureDevOpsServices),
            WorkflowPublicationPlanTests.Destination(validation: "Unauthorized")
        })
        {
            var wrongRig = await RigAsync(wrong);
            Assert.Equal("UnsupportedDestination", (await Assert.ThrowsAsync<WorkloadInputException>(() => ReconcileAsync(wrongRig))).Code);
            Assert.Empty(wrongRig.Observer.Calls);
        }

        var otherOrg = WorkflowPublicationPlanTests.Destination() with { Organization = "someone-else" };
        var mismatch = await RigAsync(otherOrg);
        Assert.Equal("DestinationMismatch", (await Assert.ThrowsAsync<WorkflowPublicationException>(() => ReconcileAsync(mismatch))).Code);
        Assert.Empty(mismatch.Observer.Calls);

        var sameOrgOtherCase = await RigAsync(WorkflowPublicationPlanTests.Destination() with { Organization = "TARGET-ORG" });
        await ReconcileAsync(sameOrgOtherCase);
        Assert.Single(sameOrgOtherCase.Observer.Calls);

        var noToken = await RigAsync(token: null);
        Assert.Equal("DestinationCredentialMissing", (await Assert.ThrowsAsync<WorkloadInputException>(() => ReconcileAsync(noToken))).Code);
        Assert.Empty(noToken.Observer.Calls);
    }

    [Fact]
    public async Task A_destination_that_cannot_be_read_is_a_verdict_not_an_error_and_a_cancellation_is_not_swallowed()
    {
        foreach (Exception failure in new Exception[] { new HttpRequestException("TargetUnreadable"), new InvalidOperationException("bad") })
        {
            var rig = await RigAsync();
            rig.Observer.Throw = failure;
            var result = await ReconcileAsync(rig);
            Assert.Equal((ReconciliationVerdicts.TargetUnreadable, (PublicationObservation?)null), (result.Verdict, result.Observed));
        }
        var cancelled = await RigAsync();
        cancelled.Observer.Throw = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ReconcileAsync(cancelled));
    }
}
