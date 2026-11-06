using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubTrustedRemoteReadCoordinatorTests
{
    [Fact]
    public async Task Only_the_runner_generated_receipt_can_reach_the_journal()
    {
        var fixture = new Fixture();
        var status = await fixture.Coordinator().ExecuteAsync(
            fixture.Command, "ephemeral-in-memory-token", default);
        Assert.Equal(2, fixture.PlanReader.Reads);
        Assert.Equal(1, fixture.Provider.Reads);
        Assert.Equal(1, fixture.Mirror.Reads);
        Assert.Equal(2, fixture.Materializer.Reads);
        Assert.Equal(1, fixture.Journal.Writes);
        Assert.Equal(fixture.Evidence.MirrorReceipt.EvidenceSha256,
            fixture.Journal.Last!.MirrorReceipt.EvidenceSha256);
        Assert.Equal(fixture.Evidence.MirrorRequest.SourceSnapshotId,
            fixture.Journal.Last!.MirrorRequest.SourceSnapshotId);
        Assert.Equal(GitHubRemoteObjectReconciliationStates.FullCopyEvidenceOnly,
            status.State);
        Assert.False(status.RemotePushInvoked);
        Assert.False(status.RemoteWritesAuthorized);
        Assert.False(status.MayAutomaticallyRetry);
        Assert.False(status.MigrationComplete);
        Assert.DoesNotContain("ephemeral-in-memory-token",
            System.Text.Json.JsonSerializer.Serialize(status));
    }

    [Fact]
    public async Task Disabled_and_tampered_source_plan_prevent_all_GitHub_reads()
    {
        var fixture = new Fixture();
        var ex = await Assert.ThrowsAsync<GitHubTrustedRemoteReadException>(
            () => fixture.Coordinator(enabled: false).ExecuteAsync(
                fixture.Command, "ephemeral-token", default));
        Assert.Equal("TrustedRemoteReadDisabled", ex.Code);
        Assert.Equal(0, fixture.PlanReader.Reads);

        fixture.PlanReader.Current = fixture.PlanReader.Current with
        { SourceEvidenceSha256 = new string('0', 64) };
        ex = await Assert.ThrowsAsync<GitHubTrustedRemoteReadException>(
            () => fixture.Coordinator().ExecuteAsync(
                fixture.Command, "ephemeral-token", default));
        Assert.Equal("TrustedRemoteReadPlanInvalid", ex.Code);
        Assert.Equal(0, fixture.Provider.Reads);
        Assert.Equal(0, fixture.Mirror.Reads);
        Assert.Equal(0, fixture.Journal.Writes);
    }

    [Fact]
    public async Task Expired_token_and_malformed_handles_never_contact_provider()
    {
        var fixture = new Fixture();
        var ex = await Assert.ThrowsAsync<GitHubTrustedRemoteReadException>(
            () => fixture.Coordinator().ExecuteAsync(
                fixture.Command with {
                    TokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(10)
                }, "ephemeral-token", default));
        Assert.Equal("TrustedRemoteReadTokenStale", ex.Code);
        Assert.Equal(0, fixture.Provider.Reads);

        ex = await Assert.ThrowsAsync<GitHubTrustedRemoteReadException>(
            () => fixture.Coordinator().ExecuteAsync(
                fixture.Command with { HazardId = Guid.Empty },
                "ephemeral-token", default));
        Assert.Equal("TrustedRemoteReadInputInvalid", ex.Code);
        ex = await Assert.ThrowsAsync<GitHubTrustedRemoteReadException>(
            () => fixture.Coordinator().ExecuteAsync(
                fixture.Command, "credential\ninjection", default));
        Assert.Equal("TrustedRemoteReadInputInvalid", ex.Code);
        Assert.Equal(0, fixture.Mirror.Reads);
    }

    [Fact]
    public async Task Unexpected_remote_ref_is_rejected_before_expensive_fetch()
    {
        var fixture = new Fixture();
        var e = fixture.Evidence;
        fixture.Provider.Observation = GitHubRefObservationIntegrity.Seal(
            e.MirrorRequest.Organization, e.MirrorRequest.Repository,
            DateTimeOffset.UtcNow,
            [..e.MirrorRequest.ExpectedRemoteRefs.Refs,
             new("refs/heads/unexpected", new string('e', 40), "commit")]);
        var ex = await Assert.ThrowsAsync<GitHubTrustedRemoteReadException>(
            () => fixture.Coordinator().ExecuteAsync(
                fixture.Command, "ephemeral-token", default));
        Assert.Equal("TrustedRemoteReadRefsDrift", ex.Code);
        Assert.Equal(0, fixture.Mirror.Reads);
        Assert.Equal(0, fixture.Journal.Writes);
    }

    [Fact]
    public async Task Source_mapping_change_during_fetch_cannot_be_committed()
    {
        var fixture = new Fixture();
        fixture.PlanReader.AfterFirst = p =>
        {
            var snapshot = Guid.NewGuid();
            var updated = p with {
                SourceSnapshotId = snapshot,
                SourceMirrorId = GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                    snapshot, p.SourceBundle!.WorkpackId)
            };
            return updated with {
                PlanEvidenceSha256 = GitHubTrustedRemoteReadCoordinator.Fingerprint(updated)
            };
        };
        var ex = await Assert.ThrowsAsync<GitHubTrustedRemoteReadException>(
            () => fixture.Coordinator().ExecuteAsync(
                fixture.Command, "ephemeral-token", default));
        Assert.Equal("TrustedRemoteReadPlanChanged", ex.Code);
        Assert.Equal(1, fixture.Mirror.Reads);
        Assert.Equal(0, fixture.Journal.Writes);
    }

    [Fact]
    public async Task Failed_or_cancelled_Git_download_never_records_a_receipt()
    {
        var fixture = new Fixture();
        fixture.Mirror.Fail = true;
        var ex = await Assert.ThrowsAsync<GitHubReadOnlyObjectMirrorException>(
            () => fixture.Coordinator().ExecuteAsync(
                fixture.Command, "ephemeral-token", default));
        Assert.Equal("SyntheticReadOnlyGitFetchFailure", ex.Code);
        Assert.Equal(0, fixture.Journal.Writes);

        var stopped = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stopped.Coordinator().ExecuteAsync(
                stopped.Command, "ephemeral-token", cancellation.Token));
        Assert.Equal(0, stopped.Journal.Writes);
    }

    private sealed class Fixture
    {
        public GitHubRemoteObjectReconciliationEvidence Evidence { get; }
        public GitHubTrustedRemoteReadCommand Command { get; }
        public GitHubReadOnlyObjectMirrorOptions Options { get; }
        public FakePlans PlanReader { get; }
        public FakeProvider Provider { get; }
        public FakeMirror Mirror { get; }
        public FakeMaterializer Materializer { get; }
        public FakeJournal Journal { get; }

        public Fixture()
        {
            var (original, options) =
                GitHubRemoteObjectReconciliationIntegrityTests.Valid();
            Options = options;
            var snapshot = Guid.NewGuid();
            var workpack = Guid.NewGuid();
            var mirrorId = GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                snapshot, workpack);
            var request = original.MirrorRequest with {
                SourceSnapshotId = mirrorId
            };
            var old = original.MirrorReceipt;
            var closure = GitObjectClosureAuditIntegrity.Seal(new(
                mirrorId, Guid.NewGuid(), request.ExpectedSourceRefs,
                request.ExpectedRemoteRefs.AsMirrorRefs()), "sha1",
                old.SourceObjectSetSha256, old.SourceObjectSetSha256,
                old.VerifiedReachableObjects, old.VerifiedReachableObjects,
                request.ExpectedRemoteRefs.ObservedAtUtc);
            var receipt = GitHubReadOnlyObjectMirrorIntegrity.Seal(
                request, new(closure.TargetSandboxId,
                    request.ExpectedRemoteRefs.AsMirrorRefs(), closure),
                Options, DateTimeOffset.UtcNow);
            Evidence = original with {
                MirrorRequest = request, MirrorReceipt = receipt
            };
            var decision = GitHubRemoteObjectReconciliationIntegrity.Verify(
                Evidence, Options, DateTimeOffset.UtcNow);
            var bundle = new GitSourceBundleView(workpack,
                Guid.NewGuid(), Guid.NewGuid(), "f060-repo",
                new string('a', 64), new string('b', 64),
                workpack.ToString("N") + ".bundle", new string('c', 64),
                4096, request.ExpectedSourceRefs.Count,
                DateTimeOffset.UtcNow, true, false);
            Command = new(Evidence.GrantId, Evidence.ReadbackId,
                Evidence.HazardId, Evidence.RepositoryId, Evidence.ObserverId,
                Evidence.OriginalBaseline, Evidence.CreateCandidates,
                Evidence.MirrorRequest.TokenExpiresAtUtc);
            var raw = new GitHubTrustedRemoteReadPlan(
                snapshot,
                Evidence.MirrorRequest.Organization,
                Evidence.MirrorRequest.Repository,
                new string('a', 64),
                Evidence.MirrorRequest.ExpectedSourceRefs,
                decision.OriginalBaselineSha256,
                decision.CandidateSetSha256,
                decision.TargetRefSetSha256, "")
            {
                SourceMirrorId = mirrorId,
                SourceBundle = bundle
            };
            PlanReader = new(raw with {
                PlanEvidenceSha256 = GitHubTrustedRemoteReadCoordinator.Fingerprint(raw)
            });
            Provider = new(Evidence.MirrorRequest.ExpectedRemoteRefs);
            Mirror = new(Evidence.MirrorReceipt);
            Materializer = new();
            Journal = new(Options);
        }

        public GitHubTrustedRemoteReadCoordinator Coordinator(bool enabled = true) =>
            new(PlanReader, Provider, Mirror, Materializer, Journal, Options,
                new(enabled), TimeProvider.System);
    }

    private sealed class FakePlans(GitHubTrustedRemoteReadPlan original)
        : IGitHubTrustedRemoteReadPlanReader
    {
        public int Reads { get; private set; }
        public GitHubTrustedRemoteReadPlan Current { get; set; } = original;
        public Func<GitHubTrustedRemoteReadPlan, GitHubTrustedRemoteReadPlan>?
            AfterFirst { get; set; }
        public Task<GitHubTrustedRemoteReadPlan> ReadAsync(
            GitHubTrustedRemoteReadCommand command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Reads == 1 || AfterFirst is null
                ? Current : AfterFirst(Current));
        }
    }

    private sealed class FakeProvider(GitHubRefObservation observation)
        : IGitHubRefObservationReader
    {
        public int Reads { get; private set; }
        public GitHubRefObservation Observation { get; set; } = observation;
        public Task<GitHubRefObservation> ReadAsync(
            string organization, string repository,
            string token, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Observation);
        }
    }

    private sealed class FakeMirror(GitHubReadOnlyObjectMirrorReceipt receipt)
        : IGitHubReadOnlyObjectMirrorRunner
    {
        public int Reads { get; private set; }
        public bool Fail { get; set; }
        public Task<GitHubReadOnlyObjectMirrorReceipt> VerifyAsync(
            GitHubReadOnlyObjectMirrorRequest request,
            string installationToken, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads++;
            if (Fail)
                throw new GitHubReadOnlyObjectMirrorException(
                    "SyntheticReadOnlyGitFetchFailure");
            return Task.FromResult(receipt);
        }
    }

    private sealed class FakeMaterializer : IGitSourceSnapshotMirrorMaterializer
    {
        public int Reads { get; private set; }
        public bool Fail { get; set; }

        public Task<GitSourceSnapshotMirrorProof> MaterializeAsync(
            GitHubTrustedRemoteReadPlan plan, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads++;
            if (Fail)
                throw new GitSourceSnapshotMirrorException(
                    "SyntheticPhysicalSourceFailure");
            return Task.FromResult(GitSourceSnapshotMirrorIntegrity.Seal(
                plan, new string('e', 64), 20, DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeJournal(GitHubReadOnlyObjectMirrorOptions options)
        : IGitHubRemoteObjectReconciliationJournal
    {
        public int Writes { get; private set; }
        public GitHubRemoteObjectReconciliationEvidence? Last { get; private set; }
        public Task<GitHubRemoteObjectReconciliationStatus> RecordAsync(
            GitHubRemoteObjectReconciliationEvidence evidence, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Writes++;
            Last = evidence;
            var proof = GitHubRemoteObjectReconciliationIntegrity.Verify(
                evidence, options, DateTimeOffset.UtcNow);
            return Task.FromResult(new GitHubRemoteObjectReconciliationStatus(
                Guid.NewGuid(), evidence.GrantId, evidence.ReadbackId,
                evidence.HazardId, evidence.RepositoryId, proof.SourceSnapshotId,
                proof.State, proof.OriginalBaselineSha256,
                proof.CandidateSetSha256, proof.TargetRefSetSha256,
                proof.RemoteRefObservationSha256, proof.FullObjectClosureSha256,
                proof.RemoteMirrorReceiptSha256, proof.EvidenceSha256,
                evidence.ObserverId, proof.VerifiedAtUtc, DateTimeOffset.UtcNow,
                false, false, false, false));
        }
        public Task<GitHubRemoteObjectReconciliationStatus?> GetAsync(
            Guid hazardId, long repositoryId, CancellationToken ct) =>
            Task.FromResult<GitHubRemoteObjectReconciliationStatus?>(null);
        public Task<IReadOnlyList<GitHubRemoteObjectReconciliationEvent>> EventsAsync(
            Guid hazardId, long repositoryId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GitHubRemoteObjectReconciliationEvent>>([]);
    }
}
