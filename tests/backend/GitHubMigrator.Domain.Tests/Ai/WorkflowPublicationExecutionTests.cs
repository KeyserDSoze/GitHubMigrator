using GitHubMigrator.Business;
using static GitHubMigrator.Domain.Tests.Ai.WorkflowPublicationPlanTests;
using static GitHubMigrator.Domain.Tests.Ai.WorkflowProposalServiceTests;

namespace GitHubMigrator.Domain.Tests.Ai;

internal sealed class InMemoryLedger : IPublicationLedger
{
    public List<PublicationRecord> Rows { get; } = [];
    public Func<bool>? RefuseNextAdvance { get; set; }

    // the same rule as the database: one live attempt per plan, a failure before any write frees the plan
    public Task<PublicationRecord?> TryStartAsync(PublicationRecord started, CancellationToken ct)
    {
        if (Rows.Any(r => r.ProposalId == started.ProposalId && r.PlanSha256 == started.PlanSha256 && !(r.State == PublicationStates.Failed && !r.WritesPossible)))
            return Task.FromResult<PublicationRecord?>(null);
        Rows.Add(started);
        return Task.FromResult<PublicationRecord?>(started);
    }

    public Task<bool> AdvanceAsync(Guid id, string from, string to, string? commitSha, int? pullNumber, string? pullUrl, DateTimeOffset at, CancellationToken ct)
    {
        if (RefuseNextAdvance?.Invoke() == true) return Task.FromResult(false);
        var i = Rows.FindIndex(r => r.Id == id && r.State == from);
        if (i < 0 || !PublicationStates.MayFollow(from, to) || to == PublicationStates.Failed) return Task.FromResult(false);
        Rows[i] = Rows[i] with { State = to, CommitSha = commitSha ?? Rows[i].CommitSha, PullNumber = pullNumber ?? Rows[i].PullNumber, PullUrl = pullUrl ?? Rows[i].PullUrl, UpdatedAtUtc = at };
        return Task.FromResult(true);
    }

    public Task FailAsync(Guid id, string errorCode, bool writesPossible, DateTimeOffset at, CancellationToken ct)
    {
        var i = Rows.FindIndex(r => r.Id == id && r.State != PublicationStates.Published && r.State != PublicationStates.Failed);
        if (i >= 0) Rows[i] = Rows[i] with { State = PublicationStates.Failed, FailedAtState = Rows[i].State, ErrorCode = errorCode, WritesPossible = writesPossible, UpdatedAtUtc = at };
        return Task.CompletedTask;
    }

    public Task<PublicationRecord?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));
    public Task<IReadOnlyList<PublicationRecord>> ListAsync(Guid proposalId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PublicationRecord>>(Rows.Where(r => r.ProposalId == proposalId).OrderByDescending(r => r.StartedAtUtc).ToArray());
}

internal sealed class FakeWriter : IGitHubPublicationWriter
{
    public List<PublicationExecution> Published { get; } = [];
    public List<string> Inspected { get; } = [];
    public Func<PublicationExecution, IPublicationProgress, CancellationToken, Task<PublicationOutcome>>? Behaviour { get; set; }
    public Exception? InspectFails { get; set; }
    public (long AppId, long InstallationId, string Pem)? Seen { get; private set; }

    public Task<PublicationCredentialObservation> InspectAsync(long appId, long installationId, string privateKeyPem, string organization, string repository, CancellationToken ct)
    {
        Inspected.Add($"{organization}/{repository}");
        return InspectFails is null ? Task.FromResult(new PublicationCredentialObservation(installationId, 7, ["contents:write"], DateTimeOffset.UnixEpoch)) : Task.FromException<PublicationCredentialObservation>(InspectFails);
    }

    public async Task<PublicationOutcome> PublishAsync(long appId, long installationId, string privateKeyPem, PublicationExecution plan, IPublicationProgress progress, CancellationToken ct)
    {
        Seen = (appId, installationId, privateKeyPem);
        Published.Add(plan);
        if (Behaviour is not null) return await Behaviour(plan, progress, ct);
        foreach (var state in new[] { PublicationStates.BranchCreating, PublicationStates.BranchCreated, PublicationStates.FileCommitting })
            await progress.RecordAsync(state, null, null, null, ct);
        var commit = new string('e', 40);
        await progress.RecordAsync(PublicationStates.FileCommitted, commit, null, null, ct);
        await progress.RecordAsync(PublicationStates.PullRequestOpening, commit, null, null, ct);
        await progress.RecordAsync(PublicationStates.PullRequestOpened, commit, 7, "https://github.example/target-org/orion-api/pull/7", ct);
        return new(commit, 7, "https://github.example/target-org/orion-api/pull/7");
    }
}

public sealed class WorkflowPublicationExecutorTests
{
    private const string KeyReference = "publication-key";
    private static readonly ProposalActor Publisher = new(Guid.Parse("cccccccc-0000-0000-0000-000000000003"), "publisher");

    private static WorkflowPublicationOptions Options(bool enabled = true, long app = 11, long installation = 22, string key = KeyReference, params string[] repositories) =>
        new(enabled, app, installation, key, repositories.Length == 0 ? ["target-org/orion-api"] : repositories);

    private sealed record Rig(WorkflowPublicationExecutor Executor, Setup Setup, FakeWriter Writer, InMemoryLedger Ledger, PublicationPlan Plan);

    private static async Task<Rig> RigAsync(WorkflowPublicationOptions? options = null, MigrationConnection? destination = null, string? pem = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----")
    {
        var setup = await ApprovedAsync(destination: destination);
        if (pem is not null) setup.Secrets.Values[KeyReference] = pem;
        var writer = new FakeWriter();
        var ledger = new InMemoryLedger();
        var executor = new WorkflowPublicationExecutor(setup.Service, setup.Proposals, setup.Connections, setup.Secrets, ledger, writer, options ?? Options(), new Clock());
        return new(executor, setup, writer, ledger, await PlanAsync(setup));
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now.AddHours(3); }

    private static Task<PublicationRecord> PublishAsync(Rig rig, string? hash = "plan", string repository = "orion-api") =>
        rig.Executor.PublishAsync(Publisher, rig.Setup.Proposal.Id, DestinationId, repository, null, hash == "plan" ? rig.Plan.PlanSha256 : hash, default);

    private static async Task<WorkflowPublicationException> RefusedAsync(Rig rig, string? hash = "plan", string repository = "orion-api") =>
        await Assert.ThrowsAsync<WorkflowPublicationException>(() => PublishAsync(rig, hash, repository));

    [Fact]
    public async Task A_confirmed_plan_is_written_once_and_the_ledger_follows_it_to_published()
    {
        var rig = await RigAsync();
        var record = await PublishAsync(rig);

        Assert.Equal((PublicationStates.Published, 7, new string('e', 40)), (record.State, record.PullNumber, record.CommitSha));
        Assert.Equal((rig.Plan.PlanSha256, "publisher", false), (record.PlanSha256, record.RequestedBy, record.WritesPossible));
        var sent = Assert.Single(rig.Writer.Published);
        Assert.Equal((rig.Plan.Organization, rig.Plan.Repository, rig.Plan.BaseBranch, rig.Plan.BaseSha, rig.Plan.HeadBranch, rig.Plan.WorkflowPath),
            (sent.Organization, sent.Repository, sent.BaseBranch, sent.BaseSha!, sent.HeadBranch, sent.WorkflowPath));
        Assert.Equal((Clean, rig.Plan.WorkflowSha256, rig.Plan.PullRequestTitle, rig.Plan.PullRequestBody, rig.Plan.CommitMessage), (sent.WorkflowYaml, sent.WorkflowSha256, sent.PullRequestTitle, sent.PullRequestBody, sent.CommitMessage));
        Assert.Equal((11, 22), (rig.Writer.Seen!.Value.AppId, rig.Writer.Seen!.Value.InstallationId));
        Assert.Single(rig.Ledger.Rows);
        Assert.Equal(Now.AddHours(3), record.StartedAtUtc);
    }

    [Fact]
    public async Task It_refuses_to_start_when_the_operator_did_not_enable_it_or_configured_it_completely()
    {
        foreach (var options in new[]
        {
            Options(enabled: false), Options(app: 0), Options(installation: 0), Options(key: ""), new WorkflowPublicationOptions(true, 1, 2, KeyReference, []), WorkflowPublicationOptions.Disabled
        })
        {
            var rig = await RigAsync(options);
            Assert.False(rig.Executor.Enabled);
            Assert.Equal("PublicationDisabled", (await RefusedAsync(rig)).Code);
            Assert.Empty(rig.Writer.Published);
            Assert.Empty(rig.Ledger.Rows);
        }
        var enabled = await RigAsync();
        Assert.True(enabled.Executor.Enabled);
        Assert.Equal(["target-org/orion-api"], enabled.Executor.AllowedRepositories);
    }

    [Fact]
    public async Task The_person_must_confirm_the_hash_of_the_plan_they_saw_and_it_must_still_be_the_plan()
    {
        var rig = await RigAsync();
        var readsBefore = rig.Setup.Reader.Calls.Count;
        foreach (var missing in new string?[] { null, "", "short", new string('Z', 64), rig.Plan.PlanSha256.ToUpperInvariant() })
            Assert.Equal("PlanConfirmationRequired", (await RefusedAsync(rig, missing)).Code);
        Assert.Equal(readsBefore, rig.Setup.Reader.Calls.Count); // not even the destination was read
        Assert.Equal("PublicationPlanChanged", (await RefusedAsync(rig, new string('0', 64))).Code);

        // the base moved since the plan was made: the same confirmation names something that is no longer true
        rig.Setup.Reader.Result = FakePublicationReader.FreshTarget() with { BaseSha = new string('9', 40) };
        Assert.Equal("PublicationPlanChanged", (await RefusedAsync(rig)).Code);
        Assert.Empty(rig.Writer.Published);
        Assert.Empty(rig.Ledger.Rows);
    }

    [Fact]
    public async Task A_plan_with_something_in_the_way_is_not_executed_and_names_what()
    {
        var rig = await RigAsync();
        rig.Setup.Reader.Result = FakePublicationReader.FreshTarget() with { WorkflowFile = WorkflowFileState.Different, HeadBranchExists = true };
        var plan = await PlanAsync(rig.Setup);
        var refused = await Assert.ThrowsAsync<WorkflowPublicationException>(() => rig.Executor.PublishAsync(Publisher, rig.Setup.Proposal.Id, DestinationId, "orion-api", null, plan.PlanSha256, default));
        Assert.Equal("PublicationPlanNotReady", refused.Code);
        Assert.Equal(["HeadBranchExists", "WorkflowPathOccupied"], refused.Details.Order());

        rig.Setup.Reader.Result = FakePublicationReader.FreshTarget() with { WorkflowFile = WorkflowFileState.Identical };
        var done = await PlanAsync(rig.Setup);
        Assert.Equal("PublicationPlanNotReady", (await Assert.ThrowsAsync<WorkflowPublicationException>(() =>
            rig.Executor.PublishAsync(Publisher, rig.Setup.Proposal.Id, DestinationId, "orion-api", null, done.PlanSha256, default))).Code);
        Assert.Empty(rig.Writer.Published);
        Assert.Empty(rig.Ledger.Rows);
    }

    [Fact]
    public async Task Only_allow_listed_repositories_proposals_nobody_approved_and_a_separate_credential_pass()
    {
        var rig = await RigAsync(Options(repositories: "target-org/somewhere-else"));
        Assert.Equal("PublicationRepositoryNotAllowed", (await RefusedAsync(rig)).Code);

        var caseless = await RigAsync(Options(repositories: "TARGET-ORG/Orion-API"));
        Assert.Equal(PublicationStates.Published, (await PublishAsync(caseless)).State);

        var pending = await ApprovedAsync(approve: false);
        pending.Secrets.Values[KeyReference] = "pem";
        var executor = new WorkflowPublicationExecutor(pending.Service, pending.Proposals, pending.Connections, pending.Secrets, new InMemoryLedger(), new FakeWriter(), Options(), new Clock());
        Assert.Equal("ProposalNotApproved", (await Assert.ThrowsAsync<WorkflowProposalException>(() =>
            executor.PublishAsync(Publisher, pending.Proposal.Id, DestinationId, "orion-api", null, new string('a', 64), default))).Code);

        // the credential that writes is never the credential that reads
        var shared = await RigAsync(destination: Destination() with { SecretReference = KeyReference });
        shared.Setup.Secrets.Values[KeyReference] = "pem";
        Assert.Equal("PublicationCredentialSeparationFailed", (await RefusedAsync(shared)).Code);
        Assert.Empty(shared.Writer.Published);
        Assert.Empty(shared.Ledger.Rows);
    }

    [Fact]
    public async Task A_missing_signing_key_is_refused_before_anything_is_recorded_or_written()
    {
        foreach (var pem in new[] { null, "", "   ", new string('x', 40_000) })
        {
            var rig = await RigAsync(pem: pem);
            if (pem is not null) rig.Setup.Secrets.Values[KeyReference] = pem;
            Assert.Equal("PublicationSigningKeyMissing", (await RefusedAsync(rig)).Code);
            Assert.Empty(rig.Writer.Published);
            Assert.Empty(rig.Ledger.Rows);
        }
    }

    [Fact]
    public async Task A_refusal_before_any_write_frees_the_plan_but_one_after_a_write_never_does()
    {
        var rig = await RigAsync();
        rig.Writer.Behaviour = (_, _, _) => throw new PublicationWriteException("PublicationInstallationMismatch", writesPossible: false);
        var first = await RefusedAsync(rig);
        Assert.Equal("PublicationInstallationMismatch", first.Code);
        Assert.Equal((PublicationStates.Failed, PublicationStates.Started, false), (first.Record!.State, first.Record.FailedAtState, first.Record.WritesPossible));

        // the same plan may be tried again, and the failed attempt stays on record
        rig.Writer.Behaviour = null;
        Assert.Equal(PublicationStates.Published, (await PublishAsync(rig)).State);
        Assert.Equal([PublicationStates.Failed, PublicationStates.Published], rig.Ledger.Rows.Select(r => r.State));

        var risky = await RigAsync();
        risky.Writer.Behaviour = async (_, progress, ct) =>
        {
            await progress.RecordAsync(PublicationStates.BranchCreating, null, null, null, ct);
            await progress.RecordAsync(PublicationStates.BranchCreated, null, null, null, ct);
            throw new PublicationWriteException("PublicationFileAnswerLost", writesPossible: true);
        };
        var failed = await RefusedAsync(risky);
        Assert.Equal(("PublicationFileAnswerLost", PublicationStates.Failed, PublicationStates.BranchCreated, true), (failed.Code, failed.Record!.State, failed.Record.FailedAtState, failed.Record.WritesPossible));
        // a writer that claims "nothing was written" after the branch was recorded is not believed: the ledger says something exists
        var denying = await RigAsync();
        denying.Writer.Behaviour = async (_, progress, ct) =>
        {
            await progress.RecordAsync(PublicationStates.BranchCreating, null, null, null, ct);
            await progress.RecordAsync(PublicationStates.BranchCreated, null, null, null, ct);
            throw new PublicationWriteException("PublicationReadFailed", writesPossible: false);
        };
        Assert.True((await RefusedAsync(denying)).Record!.WritesPossible);
        Assert.Equal("PublicationAlreadyAttempted", (await RefusedAsync(denying)).Code);

        // ...while a definite refusal of the very first write, before the branch is recorded as created, leaves the plan free
        var refused = await RigAsync();
        refused.Writer.Behaviour = async (_, progress, ct) =>
        {
            await progress.RecordAsync(PublicationStates.BranchCreating, null, null, null, ct);
            throw new PublicationWriteException("PublicationBranchRejected", writesPossible: false);
        };
        var definite = await RefusedAsync(refused);
        Assert.Equal((PublicationStates.BranchCreating, false), (definite.Record!.FailedAtState, definite.Record.WritesPossible));
        refused.Writer.Behaviour = null;
        Assert.Equal(PublicationStates.Published, (await PublishAsync(refused)).State);

        // never again for that plan: a person looks at the destination first
        Assert.Equal("PublicationAlreadyAttempted", (await RefusedAsync(risky)).Code);
        Assert.Single(risky.Writer.Published);
    }

    [Fact]
    public async Task Whatever_goes_wrong_is_written_down_with_whether_something_may_exist()
    {
        // before any write was attempted
        var early = await RigAsync();
        early.Writer.Behaviour = (_, _, _) => throw new InvalidOperationException("boom: contains a secret value that must not be stored");
        var earlyFailure = await RefusedAsync(early);
        Assert.Equal(("PublicationUnexpectedError", false), (earlyFailure.Code, earlyFailure.Record!.WritesPossible));
        Assert.DoesNotContain("secret value", earlyFailure.Record.ErrorCode);

        // after one
        var late = await RigAsync();
        late.Writer.Behaviour = async (_, progress, ct) =>
        {
            await progress.RecordAsync(PublicationStates.BranchCreating, null, null, null, ct);
            throw new HttpRequestException("connection reset");
        };
        var lateFailure = await RefusedAsync(late);
        Assert.Equal(("PublicationUnexpectedError", PublicationStates.BranchCreating, true), (lateFailure.Code, lateFailure.Record!.FailedAtState, lateFailure.Record.WritesPossible));

        // a cancellation mid-way is not silence either
        var cancelled = await RigAsync();
        cancelled.Writer.Behaviour = async (_, progress, ct) =>
        {
            await progress.RecordAsync(PublicationStates.BranchCreating, null, null, null, ct);
            await progress.RecordAsync(PublicationStates.BranchCreated, null, null, null, ct);
            throw new OperationCanceledException();
        };
        var cancelFailure = await RefusedAsync(cancelled);
        Assert.Equal(("PublicationCancelled", true), (cancelFailure.Code, cancelFailure.Record!.WritesPossible));
    }

    [Fact]
    public async Task The_ledger_cannot_be_skipped_or_run_backwards_and_a_lost_race_writes_nothing()
    {
        var rig = await RigAsync();
        rig.Writer.Behaviour = async (_, progress, ct) =>
        {
            await progress.RecordAsync(PublicationStates.BranchCreating, null, null, null, ct);
            await progress.RecordAsync(PublicationStates.FileCommitting, null, null, null, ct); // skipped BranchCreated
            return new("c", 1, null);
        };
        Assert.Equal("PublicationLedgerOutOfOrder", (await RefusedAsync(rig)).Code);

        var other = await RigAsync();
        other.Ledger.RefuseNextAdvance = () => true;
        other.Writer.Behaviour = async (_, progress, ct) =>
        {
            await progress.RecordAsync(PublicationStates.BranchCreating, null, null, null, ct);
            return new("c", 1, null);
        };
        Assert.Equal("PublicationLedgerConflict", (await RefusedAsync(other)).Code);

        // a live attempt of the same plan: the second start is refused before the writer is called
        var racing = await RigAsync();
        var started = racing.Ledger.Rows;
        started.Add(new(Guid.NewGuid(), racing.Setup.Proposal.Id, racing.Plan.PlanSha256, racing.Plan.WorkflowSha256, "target-org", "orion-api", "main", new string('c', 40), racing.Plan.HeadBranch,
            racing.Plan.WorkflowPath, PublicationStates.BranchCreating, null, null, null, null, null, false, Publisher.Id, "publisher", Now, Now));
        Assert.Equal("PublicationAlreadyAttempted", (await RefusedAsync(racing)).Code);
        Assert.Empty(racing.Writer.Published);
    }

    [Fact]
    public async Task The_credential_check_writes_nothing_and_is_limited_to_the_allow_list()
    {
        var rig = await RigAsync();
        var seen = await rig.Executor.CheckCredentialAsync("target-org", "orion-api", default);
        Assert.Equal((22, 7), (seen.InstallationId, seen.RepositoryId));
        Assert.Equal(["target-org/orion-api"], rig.Writer.Inspected);
        Assert.Empty(rig.Writer.Published);
        Assert.Empty(rig.Ledger.Rows);

        Assert.Equal("PublicationRepositoryNotAllowed", (await Assert.ThrowsAsync<WorkflowPublicationException>(() => rig.Executor.CheckCredentialAsync("target-org", "other", default))).Code);
        Assert.Equal("PublicationDisabled", (await Assert.ThrowsAsync<WorkflowPublicationException>(() => (WorkflowPublicationExecutorFor(Options(enabled: false), rig)).CheckCredentialAsync("target-org", "orion-api", default))).Code);
        rig.Writer.InspectFails = new PublicationWriteException("PublicationInstallationMismatch", false);
        Assert.Equal("PublicationInstallationMismatch", (await Assert.ThrowsAsync<WorkflowPublicationException>(() => rig.Executor.CheckCredentialAsync("target-org", "orion-api", default))).Code);
        rig.Setup.Secrets.Values.Remove(KeyReference);
        rig.Writer.InspectFails = null;
        Assert.Equal("PublicationSigningKeyMissing", (await Assert.ThrowsAsync<WorkflowPublicationException>(() => rig.Executor.CheckCredentialAsync("target-org", "orion-api", default))).Code);
    }

    private static WorkflowPublicationExecutor WorkflowPublicationExecutorFor(WorkflowPublicationOptions options, Rig rig) =>
        new(rig.Setup.Service, rig.Setup.Proposals, rig.Setup.Connections, rig.Setup.Secrets, rig.Ledger, rig.Writer, options, new Clock());

    [Fact]
    public void Configuration_lists_and_states_are_read_strictly()
    {
        Assert.Equal(["org/a", "Org2/b.c"], WorkflowPublicationOptions.ParseRepositories(" org/a, Org2/b.c;org/A ,  nonsense, a/b/c, /x, y/, org/ bad ,\nORG/a"));
        Assert.Empty(WorkflowPublicationOptions.ParseRepositories(null));
        Assert.Empty(WorkflowPublicationOptions.ParseRepositories("   "));

        var chain = PublicationStates.Chain;
        for (var i = 0; i + 1 < chain.Count; i++)
        {
            Assert.True(PublicationStates.MayFollow(chain[i], chain[i + 1]), chain[i]);                       // the next step
            Assert.False(PublicationStates.MayFollow(chain[i + 1], chain[i]), chain[i]);                      // never backwards
            Assert.False(PublicationStates.MayFollow(chain[i], chain[i]), chain[i]);                          // never the same state again
            if (i + 2 < chain.Count) Assert.False(PublicationStates.MayFollow(chain[i], chain[i + 2]), chain[i]); // never a skipped step
        }
        foreach (var live in chain.Take(chain.Count - 1)) Assert.True(PublicationStates.MayFollow(live, PublicationStates.Failed), live);
        foreach (var final in new[] { PublicationStates.Published, PublicationStates.Failed })
            foreach (var any in chain.Append(PublicationStates.Failed)) Assert.False(PublicationStates.MayFollow(final, any), final + " -> " + any);
        Assert.False(PublicationStates.MayFollow("Unknown", PublicationStates.BranchCreating));
        Assert.False(PublicationStates.MayFollow(PublicationStates.Started, "Unknown"));
    }
}
