using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitTransferWorkpackProcessorTests
{
    [Fact]
    public async Task Worker_reaches_only_a_denied_write_barrier_never_success()
    {
        var repo = new FakeJobs();
        var worker = new GitTransferWorkpackProcessor(repo);
        Assert.True(await worker.RunOnceAsync(default));
        Assert.Equal(1, repo.EvidenceCheckpoints);
        Assert.Equal(1, repo.WriteBarriers);
        Assert.False(repo.AnyWriteOperation);
        Assert.Equal(GitTransferWorkpackPhases.WriteBlocked, repo.Phase);
        Assert.Equal(GitTransferWorkpackStates.Blocked, repo.State);
        Assert.False(await worker.RunOnceAsync(default));
    }

    [Fact]
    public async Task Reclaimed_checkpoint_skips_completed_evidence_phase()
    {
        var repo = new FakeJobs {
            Phase = GitTransferWorkpackPhases.EvidenceChecked
        };
        Assert.True(await new GitTransferWorkpackProcessor(repo).RunOnceAsync(default));
        Assert.Equal(0, repo.EvidenceCheckpoints);
        Assert.Equal(1, repo.WriteBarriers);
        Assert.Equal(GitTransferWorkpackStates.Blocked, repo.State);
    }

    [Fact]
    public async Task Lost_checkpoint_never_crosses_write_barrier()
    {
        var repo = new FakeJobs { LoseCheckpoint = true };
        Assert.True(await new GitTransferWorkpackProcessor(repo).RunOnceAsync(default));
        Assert.Equal(0, repo.WriteBarriers);
        Assert.Equal(GitTransferWorkpackPhases.EvidencePending, repo.Phase);
    }

    [Fact]
    public async Task Permanent_evidence_failure_is_audited_not_bypassed()
    {
        var repo = new FakeJobs { ErrorOnEvidence = true };
        Assert.True(await new GitTransferWorkpackProcessor(repo).RunOnceAsync(default));
        Assert.Equal("TransferEvidenceStale", repo.LastFailure);
        Assert.True(repo.PermanentFailure);
        Assert.Equal(0, repo.WriteBarriers);
        Assert.False(repo.AnyWriteOperation);
    }

    private sealed class FakeJobs : IGitTransferWorkpackRepository
    {
        private bool _claimed;
        public bool LoseCheckpoint { get; init; }
        public bool ErrorOnEvidence { get; init; }
        public bool AnyWriteOperation => false;
        public string Phase { get; set; } = GitTransferWorkpackPhases.EvidencePending;
        public string State { get; set; } = GitTransferWorkpackStates.Queued;
        public int EvidenceCheckpoints { get; private set; }
        public int WriteBarriers { get; private set; }
        public string? LastFailure { get; private set; }
        public bool PermanentFailure { get; private set; }

        public Task<GitTransferWorkpackLease?> ClaimAsync(
            Guid token, TimeSpan duration, CancellationToken ct)
        {
            if (_claimed) return Task.FromResult<GitTransferWorkpackLease?>(null);
            _claimed = true;
            State = GitTransferWorkpackStates.Running;
            return Task.FromResult<GitTransferWorkpackLease?>(
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), token, 1,
                    new string('a',64), Phase));
        }
        public Task<bool> CheckpointEvidenceAsync(
            GitTransferWorkpackLease lease, CancellationToken ct)
        {
            if (ErrorOnEvidence)
                throw new GitTransferWorkpackException("TransferEvidenceStale");
            if (LoseCheckpoint) return Task.FromResult(false);
            EvidenceCheckpoints++;
            Phase = GitTransferWorkpackPhases.EvidenceChecked;
            return Task.FromResult(true);
        }
        public Task<bool> BlockAtWriteBarrierAsync(
            GitTransferWorkpackLease lease, CancellationToken ct)
        {
            WriteBarriers++;
            Phase = GitTransferWorkpackPhases.WriteBlocked;
            State = GitTransferWorkpackStates.Blocked;
            return Task.FromResult(true);
        }
        public Task<bool> FailAsync(GitTransferWorkpackLease lease,
            string safeCode, bool permanent, CancellationToken ct)
        {
            LastFailure = safeCode; PermanentFailure = permanent;
            State = GitTransferWorkpackStates.Failed;
            return Task.FromResult(true);
        }
        public Task<GitTransferWorkpackView> EnqueueAsync(
            Guid preparationId, Guid planId, Guid reconciliationId,
            string manifestSha, string diffSha, Guid actorId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<GitTransferWorkpackView?> GetAsync(
            Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<GitTransferWorkpackView>> ListAsync(
            Guid preparationId, Guid planId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<GitTransferWorkpackEvent>> EventsAsync(
            Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
