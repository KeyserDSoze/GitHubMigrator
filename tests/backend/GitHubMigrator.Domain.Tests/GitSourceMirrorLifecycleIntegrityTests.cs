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
        Reject(receipt with { VerificationReceiptSha256 = Sha('4') });
        // F-060's stable content SHA excludes time; F-062's individual
        // verification-receipt SHA must detect tampering with that time.
        Reject(receipt with { VerifiedAtUtc =
            receipt.VerifiedAtUtc.AddSeconds(1) });
        Reject(receipt with { RemoteWritesAuthorized = true });
        Reject(receipt with { RemotePushInvoked = true });
        Reject(receipt with { MigrationComplete = true });
        Reject(receipt with { MayAutomaticallyRetry = true });
        Reject(receipt with { VerifiedAtUtc =
            DateTimeOffset.UtcNow.AddMinutes(-4) });
    }

    [Fact]
    public void A_previous_verification_cannot_satisfy_a_new_lease()
    {
        var plan = Plan();
        var admittedAt = DateTimeOffset.UtcNow;
        var old = GitSourceSnapshotMirrorIntegrity.Seal(
            plan, Sha('3'), 8, admittedAt.AddSeconds(-15));
        var error = Assert.Throws<GitSourceMirrorLifecycleException>(() =>
            GitSourceMirrorLifecycleIntegrity.VerifyProofForLease(
                plan, old, admittedAt, admittedAt.AddSeconds(1)));
        Assert.Equal("SourceMirrorLifecycleProofStale", error.Code);

        var fresh = GitSourceSnapshotMirrorIntegrity.Seal(
            plan, Sha('3'), 8, admittedAt.AddMilliseconds(1));
        Assert.Equal(old.EvidenceSha256, fresh.EvidenceSha256);
        Assert.NotEqual(old.VerificationReceiptSha256,
            fresh.VerificationReceiptSha256);
        GitSourceMirrorLifecycleIntegrity.VerifyProofForLease(
            plan, fresh, admittedAt, admittedAt.AddSeconds(1));
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

    [Fact]
    public async Task Heartbeat_extends_owned_lease_before_slow_source_completes()
    {
        var plan = Plan();
        var gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var physical = new FakePhysical { Gate = gate.Task };
        var journal = new FakeJournal();
        var wrapper = new DurableGitSourceSnapshotMirrorMaterializer(
            physical, journal, TimeSpan.FromMilliseconds(10));

        var run = wrapper.MaterializeAsync(plan, default);
        await journal.Renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.SetResult();
        var proof = await run;

        Assert.True(journal.Renewals >= 1);
        Assert.Equal(1, physical.Calls);
        Assert.Equal(1, journal.Commits);
        Assert.NotNull(journal.CompletedLease);
        Assert.True(journal.CompletedLease.ExpiresAtUtc >
            journal.OriginalLease.ExpiresAtUtc);
        Assert.Equal(proof.EvidenceSha256,
            GitSourceSnapshotMirrorIntegrity.Seal(
                plan, proof.ObjectSetSha256, proof.VerifiedObjectCount,
                proof.VerifiedAtUtc).EvidenceSha256);
    }

    [Fact]
    public async Task Lost_heartbeat_cancels_physical_work_and_never_commits()
    {
        var plan = Plan();
        var gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var physical = new FakePhysical { Gate = gate.Task };
        var journal = new FakeJournal { FailRenewal = true };
        var wrapper = new DurableGitSourceSnapshotMirrorMaterializer(
            physical, journal, TimeSpan.FromMilliseconds(10));

        var error = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
            () => wrapper.MaterializeAsync(plan, default));
        Assert.Equal("SourceMirrorLifecycleLeaseLost", error.Code);
        Assert.Equal(1, physical.Calls);
        Assert.Equal(0, journal.Commits);
        Assert.Equal(0, journal.Quarantines);
    }

    private sealed class FakePhysical : IGitSourceSnapshotMirrorMaterializer
    {
        public int Calls { get; private set; }
        public GitSourceSnapshotMirrorException? Failure { get; init; }
        public Task? Gate { get; init; }
        public async Task<GitSourceSnapshotMirrorProof> MaterializeAsync(
            GitHubTrustedRemoteReadPlan plan, CancellationToken ct)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            if (Gate is not null) await Gate.WaitAsync(ct);
            return GitSourceSnapshotMirrorIntegrity.Seal(
                plan, Sha('a'), 8, DateTimeOffset.UtcNow);
        }
    }

    private sealed class RefusingJournal : IGitSourceMirrorLifecycleJournal
    {
        public Task<GitSourceMirrorLease> AcquireAsync(
            GitHubTrustedRemoteReadPlan plan, Guid workerId, CancellationToken ct) =>
            throw new GitSourceMirrorLifecycleException(
                "SourceMirrorLifecycleDisabled");
        public Task<GitSourceMirrorLease> RenewAsync(
            GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
            CancellationToken ct) =>
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
        public int Renewals { get; private set; }
        public bool FailRenewal { get; init; }
        public GitSourceMirrorLease OriginalLease { get; private set; } = null!;
        public GitSourceMirrorLease? CompletedLease { get; private set; }
        public TaskCompletionSource Renewed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LastFailureCode { get; private set; }

        public Task<GitSourceMirrorLease> AcquireAsync(
            GitHubTrustedRemoteReadPlan plan, Guid workerId, CancellationToken ct)
        {
            OriginalLease = new GitSourceMirrorLease(plan.SourceMirrorId,
                plan.SourceBundle!.WorkpackId, workerId, Guid.NewGuid(),
                1, DateTimeOffset.UtcNow.AddMinutes(15));
            return Task.FromResult(OriginalLease);
        }
        public Task<GitSourceMirrorLease> RenewAsync(
            GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
            CancellationToken ct)
        {
            if (FailRenewal)
                throw new GitSourceMirrorLifecycleException(
                    "SourceMirrorLifecycleLeaseLost");
            Renewals++;
            Renewed.TrySetResult();
            return Task.FromResult(lease with {
                ExpiresAtUtc = lease.ExpiresAtUtc.AddMinutes(1)
            });
        }
        public Task<GitSourceMirrorLifecycleStatus> CompleteAsync(
            GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
            GitSourceSnapshotMirrorProof proof, CancellationToken ct)
        {
            Commits++;
            CompletedLease = lease;
            return Task.FromResult(new GitSourceMirrorLifecycleStatus(
                lease.MirrorId, plan.SourceSnapshotId, lease.WorkpackId,
                GitSourceMirrorLifecycleStates.Verified,
                lease.Generation, null, null,
                plan.SourceBundle!.BundleSha256,
                plan.SourceEvidenceSha256,
                GitSourceSnapshotMirrorIntegrity.SourceRefsSha(plan.SourceRefs),
                proof.ObjectSetSha256, proof.VerifiedObjectCount,
                proof.EvidenceSha256, null, proof.VerifiedAtUtc,
                false, false, false, false));
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
