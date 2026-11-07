using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitSourceMirrorLifecycleIntegrityTests
{
    private static string Sha(char c) => new(c, 64);

    internal static GitHubTrustedRemoteReadPlan Plan()
    {
        var snapshot = Guid.NewGuid();
        var pack = Guid.NewGuid();
        GitMirrorRef[] refs = [
            new("refs/heads/main", new string('a', 40)),
            new("refs/tags/v1", new string('b', 40))
        ];
        var bundle = new GitSourceBundleView(
            pack, Guid.NewGuid(), Guid.NewGuid(), "source-one",
            Sha('c'), Sha('d'), pack.ToString("N") + ".bundle",
            Sha('e'), 1024, refs.Length, DateTimeOffset.UtcNow, true, false);
        return new GitHubTrustedRemoteReadPlan(
            snapshot, "safe-org", "safe-test", Sha('c'), refs,
            Sha('f'), Sha('1'), Sha('2'), "")
        {
            SourceMirrorId = GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                snapshot, pack),
            SourceBundle = bundle
        };
    }

    [Fact]
    public void Physically_verified_proof_is_bound_to_exact_source_workpack_and_epoch()
    {
        var plan = Plan();
        var receipt = GitSourceSnapshotMirrorIntegrity.Seal(
            plan, Sha('3'), 42, DateTimeOffset.UtcNow);
        GitSourceMirrorLifecycleIntegrity.VerifyProof(
            plan, receipt, DateTimeOffset.UtcNow);

        void Reject(GitSourceSnapshotMirrorProof fake)
        {
            var error = Assert.Throws<GitSourceMirrorLifecycleException>(() =>
                GitSourceMirrorLifecycleIntegrity.VerifyProof(
                    plan, fake, DateTimeOffset.UtcNow));
            Assert.Equal("SourceMirrorLifecycleProofInvalid", error.Code);
        }

        Reject(receipt with { PhysicalMirrorId = Guid.NewGuid() });
        Reject(receipt with { EvidenceSha256 = Sha('8') });
        Reject(receipt with { WorkpackId = Guid.NewGuid() });
        Reject(receipt with { BundleSha256 = Sha('9') });
        Reject(receipt with { ObjectSetSha256 = Sha('4') });
        Reject(receipt with { RemoteWritesAuthorized = true });
        Reject(receipt with { RemotePushInvoked = true });
        Reject(receipt with { MigrationComplete = true });
        Reject(receipt with { MayAutomaticallyRetry = true });
        Reject(receipt with { VerifiedAtUtc =
            DateTimeOffset.UtcNow.AddMinutes(-4) });
    }

    [Fact]
    public async Task Wrapper_never_calls_physical_source_without_a_database_lease()
    {
        var plan = Plan();
        var physical = new FakePhysical();
        var journal = new RefusingJournal();
        var wrapper = new DurableGitSourceSnapshotMirrorMaterializer(
            physical, journal);
        var error = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
            () => wrapper.MaterializeAsync(plan, default));
        Assert.Equal("SourceMirrorLifecycleDisabled", error.Code);
        Assert.Equal(0, physical.Calls);
    }

    [Fact]
    public async Task Failed_corrupt_physical_source_is_quarantined_not_retried()
    {
        var plan = Plan();
        var physical = new FakePhysical {
            Failure = new GitSourceSnapshotMirrorException(
                "SourceMirrorGitClosureInvalid")
        };
        var journal = new FakeJournal();
        var wrapper = new DurableGitSourceSnapshotMirrorMaterializer(
            physical, journal);
        var error = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => wrapper.MaterializeAsync(plan, default));
        Assert.Equal("SourceMirrorGitClosureInvalid", error.Code);
        Assert.Equal(1, physical.Calls);
        Assert.Equal(1, journal.Quarantines);
        Assert.Equal(0, journal.Commits);
        Assert.Equal("SourceMirrorGitClosureInvalid", journal.LastFailureCode);
    }

    private sealed class FakePhysical : IGitSourceSnapshotMirrorMaterializer
    {
        public int Calls { get; private set; }
        public GitSourceSnapshotMirrorException? Failure { get; init; }
        public Task<GitSourceSnapshotMirrorProof> MaterializeAsync(
            GitHubTrustedRemoteReadPlan plan, CancellationToken ct)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(GitSourceSnapshotMirrorIntegrity.Seal(
                plan, Sha('a'), 8, DateTimeOffset.UtcNow));
        }
    }

    private sealed class RefusingJournal : IGitSourceMirrorLifecycleJournal
    {
        public Task<GitSourceMirrorLease> AcquireAsync(
            GitHubTrustedRemoteReadPlan plan, Guid workerId, CancellationToken ct) =>
            throw new GitSourceMirrorLifecycleException(
                "SourceMirrorLifecycleDisabled");
        public Task<GitSourceMirrorLifecycleStatus> CompleteAsync(
            GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
            GitSourceSnapshotMirrorProof proof, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task QuarantineAsync(
            GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
            string failureCode, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<GitSourceMirrorLifecycleStatus?> GetAsync(
            Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<GitSourceMirrorLifecycleEvent>> EventsAsync(
            Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class FakeJournal : IGitSourceMirrorLifecycleJournal
    {
        public int Commits { get; private set; }
        public int Quarantines { get; private set; }
        public string? LastFailureCode { get; private set; }

        public Task<GitSourceMirrorLease> AcquireAsync(
            GitHubTrustedRemoteReadPlan plan, Guid workerId, CancellationToken ct) =>
            Task.FromResult(new GitSourceMirrorLease(plan.SourceMirrorId,
                plan.SourceBundle!.WorkpackId, workerId, Guid.NewGuid(),
                1, DateTimeOffset.UtcNow.AddMinutes(15)));
        public Task<GitSourceMirrorLifecycleStatus> CompleteAsync(
            GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
            GitSourceSnapshotMirrorProof proof, CancellationToken ct)
        {
            Commits++;
            throw new NotImplementedException();
        }
        public Task QuarantineAsync(
            GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
            string failureCode, CancellationToken ct)
        {
            Quarantines++;
            LastFailureCode = failureCode;
            return Task.CompletedTask;
        }
        public Task<GitSourceMirrorLifecycleStatus?> GetAsync(
            Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<GitSourceMirrorLifecycleEvent>> EventsAsync(
            Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
    }
}
