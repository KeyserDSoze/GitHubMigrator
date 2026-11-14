using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using static GitHubMigrator.Domain.Tests.Ai.WorkflowProposalServiceTests;

namespace GitHubMigrator.Domain.Tests.Ai;

// An attempt keeps its exact plan blocked for good. A person who has cleaned the destination releases it, and the PRODUCT only believes that after it has read the destination again.
public sealed class PublicationReleaseTests
{
    private static readonly string BaseSha = new('c', 40);
    private static readonly string HeadSha = new('9', 40);
    private static readonly Guid ProposalId = Guid.NewGuid();
    private static readonly DateTimeOffset Later = Now.AddHours(3);
    private static readonly ProposalActor Admin = new(Guid.NewGuid(), "admin");

    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private sealed class ScriptedObserver : IGitHubPublicationObserver
    {
        public List<(string Org, string Repo, string Base, string Head, int? Pull, string Token)> Calls { get; } = [];
        public PublicationObservation Result { get; set; } = new(true, false, BaseSha, null, [], WorkflowFileState.Absent, WorkflowFileState.Absent);
        public Exception? Throw { get; set; }
        public Action? WhileReading { get; set; }

        public Task<PullObservation?> ReadPullAsync(string organization, string repository, int number, string headBranch, string token, CancellationToken ct) => throw new NotSupportedException();

        public Task<PublicationObservation> ObserveAsync(string organization, string repository, string baseBranch, string headBranch, string workflowPath, string workflowSha256,
            string? replacedWorkflowSha256, int? pullNumber, string token, CancellationToken ct)
        {
            Calls.Add((organization, repository, baseBranch, headBranch, pullNumber, token));
            WhileReading?.Invoke();
            return Throw is null ? Task.FromResult(Result) : Task.FromException<PublicationObservation>(Throw);
        }
    }

    private sealed record Rig(PublicationReleaseService Service, ScriptedObserver Observer, InMemoryLedger Ledger, PublicationRecord Row, MemorySecrets Secrets);

    private static PublicationRecord Failed(bool writes = true, string at = PublicationStates.FileCommitting) =>
        new(Guid.NewGuid(), ProposalId, new string('d', 64), new string('a', 64), "target-org", "orion-api", "main", BaseSha, "migrate/azdo-42-aaaaaaaa", ".github/workflows/azdo-42-x.yml",
            PublicationStates.Failed, null, null, null, "PublicationFileFailed", at, writes, Guid.NewGuid(), "publisher", Now, Now);

    private static PublicationRecord Running(string state = PublicationStates.BranchCreated, DateTimeOffset? updated = null) =>
        Failed() with { State = state, ErrorCode = null, FailedAtState = null, WritesPossible = false, UpdatedAtUtc = updated ?? Now };

    private static PublicationRecord Published() =>
        Failed() with { State = PublicationStates.Published, ErrorCode = null, FailedAtState = null, WritesPossible = false, CommitSha = new string('e', 40), PullNumber = 7, PullUrl = "https://github.example/target-org/orion-api/pull/7" };

    private static Rig NewRig(PublicationRecord? row = null, MigrationConnection? destination = null, string? token = "destination-pat", DateTimeOffset? at = null)
    {
        var ledger = new InMemoryLedger();
        var record = row ?? Failed();
        ledger.Rows.Add(record);
        var secrets = new MemorySecrets();
        if (token is not null) secrets.Values["ref-1"] = token;
        var observer = new ScriptedObserver();
        var service = new PublicationReleaseService(ledger, new FakeDestinations(destination ?? WorkflowPublicationPlanTests.Destination()), secrets, observer, new Clock(at ?? Later));
        return new(service, observer, ledger, record, secrets);
    }

    private static Task<PublicationRecord> ReleaseAsync(Rig rig, string? note = "Deleted the branch by hand.", Guid? proposal = null, Guid? publication = null, Guid? destination = null) =>
        rig.Service.ReleaseAsync(Admin, proposal ?? rig.Row.ProposalId, publication ?? rig.Row.Id, destination ?? WorkflowPublicationPlanTests.DestinationId, note, default);

    private static async Task<WorkflowPublicationException> RefusedAsync(Rig rig, string? note = "Deleted the branch by hand.", Guid? proposal = null, Guid? publication = null) =>
        await Assert.ThrowsAsync<WorkflowPublicationException>(() => ReleaseAsync(rig, note, proposal, publication));

    private static PullObservation Pull(int number = 7, string state = "closed", bool merged = false) => new(number, state, merged, HeadSha, "main", merged ? new string('3', 40) : null);

    private static PublicationObservation Seen(string? head = null, params PullObservation[] pulls) => new(true, false, BaseSha, head, pulls, WorkflowFileState.Absent, WorkflowFileState.Absent);

    [Fact]
    public async Task An_attempt_that_may_have_written_is_released_when_the_destination_shows_nothing_of_it_and_the_exact_plan_is_free_again()
    {
        var rig = NewRig();
        Assert.True(rig.Row.BlocksItsPlan);
        var released = await ReleaseAsync(rig, "  Deleted the branch by hand.  ");

        Assert.Equal((Later, Admin.Id, "admin", "Deleted the branch by hand."), (released.ReleasedAtUtc, released.ReleasedById, released.ReleasedBy, released.ReleaseNote));
        Assert.False(released.BlocksItsPlan);
        // the attempt itself is as it was: what failed, where, and that a write was possible stay on record
        Assert.Equal(rig.Row with { ReleasedAtUtc = Later, ReleasedById = Admin.Id, ReleasedBy = "admin", ReleaseNote = "Deleted the branch by hand." }, released);
        Assert.Equal((PublicationStates.Failed, "PublicationFileFailed", true), (released.State, released.ErrorCode, released.WritesPossible));

        // read with the destination's credential, for the branch and base the ledger recorded, and nothing else
        var call = Assert.Single(rig.Observer.Calls);
        Assert.Equal(("target-org", "orion-api", "main", "migrate/azdo-42-aaaaaaaa", (int?)null, "destination-pat"), call);

        // the same plan can be started again; before the release it could not
        var again = rig.Row with { Id = Guid.NewGuid(), State = PublicationStates.Started, ErrorCode = null, FailedAtState = null, WritesPossible = false, ReleasedAtUtc = null, ReleasedById = null, ReleasedBy = null, ReleaseNote = null };
        Assert.NotNull(await rig.Ledger.TryStartAsync(again, default));
    }

    [Fact]
    public async Task Before_the_release_the_same_plan_cannot_be_started_again()
    {
        var rig = NewRig();
        var again = rig.Row with { Id = Guid.NewGuid(), State = PublicationStates.Started, ErrorCode = null, FailedAtState = null, WritesPossible = false };
        Assert.Null(await rig.Ledger.TryStartAsync(again, default));
    }

    [Fact]
    public async Task A_pull_request_that_was_closed_without_merging_is_inert_and_does_not_prevent_the_release_but_one_that_is_open_or_was_merged_does()
    {
        var closed = NewRig();
        closed.Observer.Result = Seen(head: null, Pull(7, "closed"), Pull(8, "closed"));
        Assert.NotNull((await ReleaseAsync(closed)).ReleasedAtUtc);

        foreach (var (pulls, expected) in new (PullObservation[], string)[]
        {
            ([Pull(7, "open")], "PullRequestOpen"),
            ([Pull(7, "closed", merged: true)], "PullRequestMerged"),
            ([Pull(7, "closed"), Pull(8, "open")], "PullRequestOpen")
        })
        {
            var rig = NewRig();
            rig.Observer.Result = Seen(head: null, pulls);
            var refused = await RefusedAsync(rig);
            Assert.Equal("PublicationLeftoversExist", refused.Code);
            Assert.Equal([expected], refused.Details);
            Assert.Null(Assert.Single(rig.Ledger.Rows).ReleasedAtUtc);
        }
    }

    [Fact]
    public async Task Whatever_is_still_at_the_destination_refuses_the_release_and_names_what_it_is_and_nothing_is_marked()
    {
        var branch = NewRig();
        branch.Observer.Result = Seen(head: HeadSha);
        var refused = await RefusedAsync(branch);
        Assert.Equal("PublicationLeftoversExist", refused.Code);
        Assert.Equal(["BranchExists"], refused.Details);
        Assert.Equal(branch.Row, refused.Record);

        var everything = NewRig();
        everything.Observer.Result = Seen(head: HeadSha, Pull(7, "open"), Pull(8, "closed", merged: true));
        Assert.Equal(["BranchExists", "PullRequestMerged", "PullRequestOpen"], (await RefusedAsync(everything)).Details.Order(StringComparer.Ordinal));

        var missing = NewRig();
        missing.Observer.Result = new PublicationObservation(false, false, null, null, [], WorkflowFileState.Absent, WorkflowFileState.Absent);
        Assert.Equal(["RepositoryNotFound"], (await RefusedAsync(missing)).Details); // a repository that cannot be seen is not one that is clean
    }

    [Fact]
    public async Task A_destination_that_cannot_be_read_is_not_a_clean_destination_and_a_cancellation_is_not_swallowed()
    {
        foreach (Exception failure in new Exception[] { new HttpRequestException("TargetUnreadable"), new InvalidOperationException("bad") })
        {
            var rig = NewRig();
            rig.Observer.Throw = failure;
            var refused = await RefusedAsync(rig);
            Assert.Equal("DestinationUnreadable", refused.Code);
            Assert.Null(Assert.Single(rig.Ledger.Rows).ReleasedAtUtc);
        }
        var cancelled = NewRig();
        cancelled.Observer.Throw = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ReleaseAsync(cancelled));
    }

    [Fact]
    public async Task A_release_says_why_and_the_note_is_checked_before_anything_is_read()
    {
        foreach (var note in new string?[] { null, "", "   ", "ok", " ab ", "\u0007\u0007\u0007", "\n\n" })
        {
            var rig = NewRig();
            Assert.Equal("NoteRequired", (await RefusedAsync(rig, note)).Code);
            Assert.Empty(rig.Observer.Calls);
        }

        var exact = NewRig();
        Assert.Equal("abc", (await ReleaseAsync(exact, "abc")).ReleaseNote);

        var long_ = NewRig();
        Assert.Equal(PublicationReleaseService.NoteMaxLength, (await ReleaseAsync(long_, new string('x', 900))).ReleaseNote!.Length);

        var control = NewRig();
        Assert.Equal("Removed it by hand", (await ReleaseAsync(control, "Removed\u0007 it by hand")).ReleaseNote);
    }

    [Fact]
    public async Task Another_proposals_attempt_or_one_that_does_not_exist_is_not_found_and_nothing_is_read()
    {
        var rig = NewRig();
        Assert.Equal("PublicationNotFound", (await RefusedAsync(rig, proposal: Guid.NewGuid())).Code);
        Assert.Equal("PublicationNotFound", (await RefusedAsync(rig, publication: Guid.NewGuid())).Code);
        Assert.Empty(rig.Observer.Calls);
    }

    [Fact]
    public async Task An_attempt_released_already_or_one_that_never_blocked_is_not_released_and_nothing_is_read()
    {
        var done = NewRig(Failed() with { ReleasedAtUtc = Now, ReleasedById = Guid.NewGuid(), ReleasedBy = "someone", ReleaseNote = "Done before." });
        var twice = await RefusedAsync(done);
        Assert.Equal("PublicationAlreadyReleased", twice.Code);
        Assert.Equal("someone", twice.Record!.ReleasedBy);
        Assert.Empty(done.Observer.Calls);

        var free = NewRig(Failed(writes: false, at: PublicationStates.Started));
        Assert.Equal("PublicationAlreadyFree", (await RefusedAsync(free)).Code); // it failed before any write: the plan was never blocked
        Assert.Empty(free.Observer.Calls);
    }

    [Fact]
    public async Task An_attempt_that_may_still_be_running_is_never_released_and_the_destination_is_not_even_read()
    {
        foreach (var state in new[] { PublicationStates.Started, PublicationStates.BranchCreating, PublicationStates.BranchCreated, PublicationStates.FileCommitting, PublicationStates.FileCommitted,
                     PublicationStates.PullRequestOpening, PublicationStates.PullRequestOpened })
        {
            var rig = NewRig(Running(state, updated: Later - TimeSpan.FromMinutes(14) - TimeSpan.FromSeconds(59)));
            Assert.Equal("PublicationStillRunning", (await RefusedAsync(rig)).Code);
            Assert.Empty(rig.Observer.Calls);
            Assert.Equal(state, Assert.Single(rig.Ledger.Rows).State);
        }
    }

    [Fact]
    public async Task An_attempt_that_has_not_moved_for_fifteen_minutes_is_abandoned_as_a_failure_after_which_writes_are_possible_and_then_released()
    {
        var rig = NewRig(Running(PublicationStates.FileCommitting, updated: Later - PublicationReleaseService.StaleAfter));
        var released = await ReleaseAsync(rig);

        Assert.Equal(PublicationStates.Failed, released.State);
        Assert.Equal(("PublicationAbandoned", PublicationStates.FileCommitting, true), (released.ErrorCode, released.FailedAtState, released.WritesPossible));
        Assert.Equal(Later, released.UpdatedAtUtc); // abandoned now, released in the same instant
        Assert.Equal((Later, "admin"), (released.ReleasedAtUtc, released.ReleasedBy));
        Assert.False(released.BlocksItsPlan);
        Assert.Single(rig.Observer.Calls);
    }

    [Fact]
    public async Task A_stale_attempt_is_abandoned_only_if_the_destination_is_clean_otherwise_it_stays_as_it_was()
    {
        var rig = NewRig(Running(PublicationStates.FileCommitted, updated: Later - TimeSpan.FromMinutes(40)));
        rig.Observer.Result = Seen(head: HeadSha);
        var refused = await RefusedAsync(rig);
        Assert.Equal("PublicationLeftoversExist", refused.Code);
        Assert.Equal(PublicationStates.FileCommitted, Assert.Single(rig.Ledger.Rows).State);
        Assert.Null(rig.Ledger.Rows[0].ErrorCode);

        var unreadable = NewRig(Running(PublicationStates.FileCommitted, updated: Later - TimeSpan.FromMinutes(40)));
        unreadable.Observer.Throw = new HttpRequestException("down");
        Assert.Equal("DestinationUnreadable", (await RefusedAsync(unreadable)).Code);
        Assert.Equal(PublicationStates.FileCommitted, Assert.Single(unreadable.Ledger.Rows).State);
    }

    [Fact]
    public async Task An_attempt_that_moves_while_the_destination_is_being_read_is_not_abandoned_and_not_released()
    {
        var rig = NewRig(Running(PublicationStates.FileCommitting, updated: Later - TimeSpan.FromMinutes(40)));
        // the slow process woke up and wrote its next step while the product was looking
        rig.Observer.WhileReading = () => rig.Ledger.Rows[0] = rig.Ledger.Rows[0] with { State = PublicationStates.FileCommitted, CommitSha = new string('e', 40), UpdatedAtUtc = Later };

        var refused = await RefusedAsync(rig);
        Assert.Equal("PublicationLedgerConflict", refused.Code);
        var row = Assert.Single(rig.Ledger.Rows);
        Assert.Equal((PublicationStates.FileCommitted, (DateTimeOffset?)null), (row.State, row.ReleasedAtUtc));
        Assert.Equal(PublicationStates.FileCommitted, refused.Record!.State);
    }

    [Fact]
    public async Task Two_people_releasing_at_once_one_wins_and_the_other_is_told_it_was_already_decided()
    {
        var rig = NewRig();
        rig.Ledger.RefuseNextRelease = () => true; // the other one got there between the read and the write
        var refused = await RefusedAsync(rig);
        Assert.Equal("PublicationLedgerConflict", refused.Code);
        Assert.Null(Assert.Single(rig.Ledger.Rows).ReleasedAtUtc);
        Assert.Empty(rig.Ledger.Evidence);

        rig.Ledger.RefuseNextRelease = null;
        Assert.NotNull((await ReleaseAsync(rig)).ReleasedAtUtc);
        Assert.Equal("PublicationAlreadyReleased", (await RefusedAsync(rig)).Code);
        Assert.Single(rig.Ledger.Evidence);
    }

    [Fact]
    public async Task A_published_attempt_is_released_only_when_its_own_pull_request_is_seen_closed_without_merging_and_its_branch_is_gone()
    {
        var rig = NewRig(Published());
        rig.Observer.Result = Seen(head: null, Pull(7, "closed"));
        var released = await ReleaseAsync(rig);
        Assert.Equal((PublicationStates.Published, 7), (released.State, released.PullNumber));
        Assert.NotNull(released.ReleasedAtUtc);
        Assert.Equal(7, rig.Observer.Calls.Single().Pull); // the recorded number is looked at directly

        foreach (var (seen, expected) in new (PublicationObservation, string[])[]
        {
            (Seen(head: null), ["PublishedPullRequestNotClosed"]),                                         // the pull request is not shown at all
            (Seen(head: null, Pull(9, "closed")), ["PublishedPullRequestNotClosed"]),                       // another number is not the attempt's
            (Seen(head: null, Pull(7, "open")), ["PublishedPullRequestNotClosed", "PullRequestOpen"]),
            (Seen(head: null, Pull(7, "closed", merged: true)), ["PublishedPullRequestNotClosed", "PullRequestMerged"]),
            (Seen(head: HeadSha, Pull(7, "closed")), ["BranchExists"])
        })
        {
            var other = NewRig(Published());
            other.Observer.Result = seen;
            Assert.Equal(expected, (await RefusedAsync(other)).Details.Order(StringComparer.Ordinal));
            Assert.Null(Assert.Single(other.Ledger.Rows).ReleasedAtUtc);
        }
    }

    [Fact]
    public async Task What_the_destination_showed_is_stored_with_the_release_and_holds_no_workflow_text_and_no_credential()
    {
        var rig = NewRig();
        rig.Observer.Result = Seen(head: null, Pull(7, "closed")) with { FileOnBase = WorkflowFileState.Different };
        await ReleaseAsync(rig);

        var (id, json) = Assert.Single(rig.Ledger.Evidence);
        Assert.Equal(rig.Row.Id, id);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(Later, root.GetProperty("readAtUtc").GetDateTimeOffset());
        Assert.Equal(("Failed", true, false, BaseSha, "Different"),
            (root.GetProperty("ledgerState").GetString(), root.GetProperty("ledgerWritesPossible").GetBoolean(), root.GetProperty("headBranchExists").GetBoolean(),
             root.GetProperty("baseTipSha").GetString(), root.GetProperty("fileOnBase").GetString()));
        var pull = Assert.Single(root.GetProperty("pulls").EnumerateArray());
        Assert.Equal((7, "closed", false), (pull.GetProperty("Number").GetInt32(), pull.GetProperty("State").GetString(), pull.GetProperty("Merged").GetBoolean()));
        Assert.DoesNotContain("destination-pat", json);
        Assert.DoesNotContain("workflowYaml", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("run:", json);
    }

    [Fact]
    public async Task Only_a_verified_github_destination_of_the_organization_the_attempt_wrote_to_is_used_and_it_must_have_a_credential_all_before_any_read()
    {
        var rig = NewRig();
        Assert.Equal("DestinationNotFound", (await Assert.ThrowsAsync<WorkloadInputException>(() => ReleaseAsync(rig, destination: Guid.NewGuid()))).Code);

        foreach (var wrong in new[]
        {
            WorkflowPublicationPlanTests.Destination(direction: ConnectionDirection.Source),
            WorkflowPublicationPlanTests.Destination(provider: ProviderKeys.AzureDevOpsServices),
            WorkflowPublicationPlanTests.Destination(validation: "Unauthorized")
        })
        {
            var other = NewRig(destination: wrong);
            Assert.Equal("UnsupportedDestination", (await Assert.ThrowsAsync<WorkloadInputException>(() => ReleaseAsync(other))).Code);
            Assert.Empty(other.Observer.Calls);
        }

        var mismatch = NewRig(destination: WorkflowPublicationPlanTests.Destination() with { Organization = "someone-else" });
        Assert.Equal("DestinationMismatch", (await RefusedAsync(mismatch)).Code);
        Assert.Empty(mismatch.Observer.Calls);

        var noToken = NewRig(token: null);
        Assert.Equal("DestinationCredentialMissing", (await Assert.ThrowsAsync<WorkloadInputException>(() => ReleaseAsync(noToken))).Code);
        Assert.Empty(noToken.Observer.Calls);
        Assert.Null(noToken.Ledger.Rows[0].ReleasedAtUtc);
    }
}
