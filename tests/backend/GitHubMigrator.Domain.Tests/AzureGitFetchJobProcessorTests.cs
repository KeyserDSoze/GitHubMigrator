using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class AzureGitFetchJobProcessorTests
{
    [Fact]
    public async Task New_job_exports_mirror_then_verified_bundle_without_any_destination_write()
    {
        var repo = new FakeJobs();
        var fetch = new FakeSource();
        Assert.True(await new AzureGitFetchJobProcessor(repo, fetch)
            .RunOnceAsync(default));
        Assert.Equal(1, fetch.MirrorFetches);
        Assert.Equal(1, fetch.Bundles);
        Assert.Equal(1, repo.MirrorCheckpoints);
        Assert.Equal(1, repo.CompletedCheckpoints);
        Assert.False(repo.ReadyForExecution);
        Assert.Equal(AzureGitFetchJobStates.Completed, repo.State);
        Assert.False(await new AzureGitFetchJobProcessor(repo, fetch)
            .RunOnceAsync(default));
    }

    [Fact]
    public async Task Crash_after_mirror_checkpoint_resumes_bundle_without_remote_fetch()
    {
        var repo = new FakeJobs { Phase = AzureGitFetchJobPhases.MirrorVerified };
        var fetch = new FakeSource();
        Assert.True(await new AzureGitFetchJobProcessor(repo, fetch)
            .RunOnceAsync(default));
        Assert.Equal(0, fetch.MirrorFetches);
        Assert.Equal(1, fetch.Bundles);
        Assert.Equal(AzureGitFetchJobStates.Completed, repo.State);
    }

    [Fact]
    public async Task Lost_lease_after_remote_fetch_never_writes_next_checkpoint()
    {
        var repo = new FakeJobs { LoseCheckpoint = true };
        var fetch = new FakeSource();
        Assert.True(await new AzureGitFetchJobProcessor(repo, fetch)
            .RunOnceAsync(default));
        Assert.Equal(1, fetch.MirrorFetches);
        Assert.Equal(0, fetch.Bundles);
        Assert.Equal(0, repo.CompletedCheckpoints);
    }

    [Fact]
    public async Task Unreachable_remote_retries_without_persisting_provider_details()
    {
        var repo = new FakeJobs();
        var fetch = new FakeSource { FailRemote = true };
        Assert.True(await new AzureGitFetchJobProcessor(repo, fetch)
            .RunOnceAsync(default));
        Assert.Equal("AzureGitFetchOrFsckFailed", repo.LastFailure);
        Assert.False(repo.PermanentFailure);
        Assert.Equal(0, repo.MirrorCheckpoints);
        Assert.Equal(0, repo.CompletedCheckpoints);
    }

    [Fact]
    public async Task Invalid_source_proof_is_terminal_not_a_retryable_write()
    {
        var repo = new FakeJobs();
        var fetch = new FakeSource { RefMismatch = true };
        Assert.True(await new AzureGitFetchJobProcessor(repo, fetch)
            .RunOnceAsync(default));
        Assert.Equal("AzureGitSourceRefMismatch", repo.LastFailure);
        Assert.True(repo.PermanentFailure);
        Assert.False(repo.ReadyForExecution);
    }

    private sealed class FakeSource : IAzureGitSourceAcquisition
    {
        public bool FailRemote { get; init; }
        public bool RefMismatch { get; init; }
        public int MirrorFetches { get; private set; }
        public int Bundles { get; private set; }

        public Task FetchMirrorAsync(Guid p, Guid plan, Guid workpack,
            string sha, CancellationToken ct)
        {
            MirrorFetches++;
            if (FailRemote) throw new GitSourceBundleException("AzureGitFetchOrFsckFailed");
            if (RefMismatch) throw new GitSourceBundleException("AzureGitSourceRefMismatch");
            return Task.CompletedTask;
        }

        public Task<GitSourceBundleView> BundleVerifiedMirrorAsync(
            Guid p, Guid plan, Guid workpack, string sha, CancellationToken ct)
        {
            Bundles++;
            return Task.FromResult(new GitSourceBundleView(
                workpack, plan, Guid.NewGuid(), "repo-1",
                new string('a', 64), sha,
                workpack.ToString("N") + ".bundle", new string('b', 64),
                1024, 1, DateTimeOffset.UtcNow, true, false));
        }
    }

    private sealed class FakeJobs : IAzureGitFetchJobRepository
    {
        private bool _claimed;
        public bool LoseCheckpoint { get; init; }
        public string State { get; private set; } = AzureGitFetchJobStates.Queued;
        public string Phase { get; set; } = AzureGitFetchJobPhases.Pending;
        public bool ReadyForExecution => false;
        public int MirrorCheckpoints { get; private set; }
        public int CompletedCheckpoints { get; private set; }
        public string? LastFailure { get; private set; }
        public bool PermanentFailure { get; private set; }

        public Task<AzureGitFetchLease?> ClaimAsync(
            Guid token, TimeSpan duration, CancellationToken ct)
        {
            if (_claimed) return Task.FromResult<AzureGitFetchLease?>(null);
            _claimed = true; State = AzureGitFetchJobStates.Running;
            return Task.FromResult<AzureGitFetchLease?>(new(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new string('c', 64), token, 1, Phase));
        }

        public Task<bool> MirrorVerifiedAsync(AzureGitFetchLease lease, CancellationToken ct)
        {
            if (LoseCheckpoint) return Task.FromResult(false);
            MirrorCheckpoints++; Phase = AzureGitFetchJobPhases.MirrorVerified;
            return Task.FromResult(true);
        }

        public Task<bool> BundleVerifiedAsync(
            AzureGitFetchLease lease, string digest, CancellationToken ct)
        {
            CompletedCheckpoints++;
            Phase = AzureGitFetchJobPhases.BundleVerified;
            State = AzureGitFetchJobStates.Completed;
            return Task.FromResult(true);
        }

        public Task<bool> FailAsync(AzureGitFetchLease lease,
            string code, bool permanent, CancellationToken ct)
        {
            LastFailure = code; PermanentFailure = permanent;
            State = permanent ? AzureGitFetchJobStates.Failed : AzureGitFetchJobStates.Queued;
            return Task.FromResult(true);
        }

        public Task<AzureGitFetchJobView> EnqueueAsync(
            Guid p, Guid plan, Guid workpack, string sha, Guid actor, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<AzureGitFetchJobView?> GetAsync(
            Guid p, Guid plan, Guid jobId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureGitFetchJobView>> ListAsync(
            Guid p, Guid plan, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<AzureGitFetchEventView>> EventsAsync(
            Guid p, Guid plan, Guid job, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
