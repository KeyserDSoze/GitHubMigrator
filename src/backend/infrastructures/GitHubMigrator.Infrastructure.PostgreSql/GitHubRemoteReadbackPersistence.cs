using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitHubRemoteReadbackRecord
{
    public Guid Id { get; set; }
    public Guid GrantId { get; set; }
    public long RepositoryId { get; set; }
    public string State { get; set; } = "";
    public string BaselineSha256 { get; set; } = "";
    public string CandidateSetSha256 { get; set; } = "";
    public string RefSetSha256 { get; set; } = "";
    public string TipEvidenceSha256 { get; set; } = "";
    public string AssessmentSha256 { get; set; } = "";
    public string JournalEvidenceSha256 { get; set; } = "";
    public Guid ObservedBy { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public bool TransportAttempted { get; set; }
    public bool RemoteWritesAuthorized { get; set; }
    public bool MayAutomaticallyRetry { get; set; }
}

public sealed class GitHubRemoteReadbackEventRecord
{
    public long Id { get; set; }
    public Guid ReadbackId { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

// F-054: INSERT-only, hash-only journal. No GitHub sender, token or
// transport adapter is injected, nor any executable/retry transition.
public sealed class PostgresGitHubRemoteReadbackJournal(
    AppDbContext db, TimeProvider clock, GitHubRemoteTestGrantOptions options)
    : IGitHubRemoteReadbackJournal
{
    public async Task<GitHubRemoteReadbackStatus> RecordAsync(
        GitHubRemoteReadbackEvidence evidence, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var decision = GitHubRemoteReadbackIntegrity.Verify(evidence, now);
        AssertAllowed(evidence.RepositoryId);

        // Same F-050 numeric repository-wide transaction lock as F-052.
        // Only one replica may record a result for the grant.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({evidence.RepositoryId.ToString()}))", ct);

        var previous = await db.GitHubRemoteReadbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.GrantId == evidence.GrantId, ct);
        if (previous is not null)
        {
            // Exact repeat is idempotent, but different evidence is NEVER
            // quietly replaced. This also supports inspection after expiry.
            if (previous.RepositoryId != evidence.RepositoryId ||
                previous.JournalEvidenceSha256 != decision.JournalEvidenceSha256 ||
                previous.ObservedBy != evidence.ObserverId)
                throw new GitHubRemoteReadbackException("RemoteReadbackConflict");
            await tx.CommitAsync(ct);
            return Map(previous);
        }

        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == evidence.GrantId &&
                x.RepositoryId == evidence.RepositoryId, ct)
            ?? throw new GitHubRemoteReadbackException("RemoteReadbackGrantNotFound");
        var fence = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == evidence.RepositoryId, ct);
        var attempt = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grant.AttemptId, ct);
        if (grant.State != GitHubRemoteTestGrantStates.ReviewedWriteDisabled ||
            grant.DeadlineUtc <= now ||
            grant.ReviewedBy is null ||
            grant.ReviewedBy == grant.RequestedBy ||
            grant.BaselineSha256 != decision.BaselineSha256 ||
            grant.CandidateSetSha256 != decision.CandidateSetSha256 ||
            evidence.ObserverId == grant.RequestedBy ||
            evidence.ObserverId == grant.ReviewedBy ||
            !string.Equals(options.AllowedRepository,
                grant.Organization + "/" + grant.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.Snapshot.RemoteRefs.Organization,
                grant.Organization, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.Snapshot.RemoteRefs.Repository,
                grant.Repository, StringComparison.OrdinalIgnoreCase) ||
            grant.ExecutionAuthorized || grant.RemoteWritesPerformed ||
            grant.ReadyForExecution ||
            fence is null ||
            fence.State != GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled ||
            fence.DeadlineUtc <= now ||
            fence.RequestId != grant.FenceRequestId ||
            fence.Generation != grant.FenceGeneration ||
            fence.AttemptId != grant.AttemptId ||
            fence.PacketSha256 != grant.PacketSha256 ||
            fence.CandidateSetSha256 != grant.CandidateSetSha256 ||
            fence.PolicySha256 != grant.OriginalPolicySha256 ||
            evidence.ObserverId == fence.RequestedBy ||
            evidence.ObserverId == fence.ReviewedBy ||
            fence.ExecutionAuthorized || fence.RemoteWritesPerformed ||
            fence.ReadyForExecution ||
            attempt is null ||
            attempt.State != GitHubTransferAttemptStates.BlockedAtWriteBarrier ||
            attempt.RepositoryId != grant.RepositoryId ||
            attempt.PacketSha256 != grant.PacketSha256 ||
            attempt.CandidateSetSha256 != grant.CandidateSetSha256 ||
            attempt.PolicySha256 != grant.OriginalPolicySha256 ||
            attempt.ReviewedBy is null ||
            attempt.ReviewedBy == attempt.RequestedBy ||
            evidence.ObserverId == attempt.RequestedBy ||
            evidence.ObserverId == attempt.ReviewedBy ||
            attempt.TransportAttempted || attempt.ExecutionAuthorized ||
            attempt.RemoteWritesPerformed || attempt.ReadyForExecution)
            throw new GitHubRemoteReadbackException("RemoteReadbackBarrierChanged");

        var row = new GitHubRemoteReadbackRecord
        {
            Id = Guid.NewGuid(),
            GrantId = evidence.GrantId,
            RepositoryId = evidence.RepositoryId,
            State = decision.State,
            BaselineSha256 = decision.BaselineSha256,
            CandidateSetSha256 = decision.CandidateSetSha256,
            RefSetSha256 = decision.RefSetSha256,
            TipEvidenceSha256 = decision.TipEvidenceSha256,
            AssessmentSha256 = decision.AssessmentSha256,
            JournalEvidenceSha256 = decision.JournalEvidenceSha256,
            ObservedBy = evidence.ObserverId,
            ObservedAtUtc = decision.ObservedAtUtc,
            RecordedAtUtc = now,
            TransportAttempted = false,
            RemoteWritesAuthorized = false,
            MayAutomaticallyRetry = false
        };
        db.GitHubRemoteReadbacks.Add(row);
        db.GitHubRemoteReadbackEvents.Add(new GitHubRemoteReadbackEventRecord
        {
            ReadbackId = row.Id, Type = "Recorded",
            ActorId = evidence.ObserverId, OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(row);
    }

    public async Task<GitHubRemoteReadbackStatus?> GetAsync(
        Guid grantId, long repositoryId, CancellationToken ct)
    {
        AssertAllowed(repositoryId);
        var row = await db.GitHubRemoteReadbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.GrantId == grantId &&
                x.RepositoryId == repositoryId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<GitHubRemoteReadbackEvent>> EventsAsync(
        Guid grantId, long repositoryId, CancellationToken ct)
    {
        AssertAllowed(repositoryId);
        var row = await db.GitHubRemoteReadbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.GrantId == grantId &&
                x.RepositoryId == repositoryId, ct)
            ?? throw new GitHubRemoteReadbackException("RemoteReadbackNotFound");
        return (await db.GitHubRemoteReadbackEvents.AsNoTracking()
            .Where(x => x.ReadbackId == row.Id)
            .OrderBy(x => x.Id).Take(50).ToArrayAsync(ct))
            .Select(x => new GitHubRemoteReadbackEvent(
                x.Id, x.ReadbackId, x.Type, x.ActorId,
                x.OccurredAtUtc)).ToArray();
    }

    private void AssertAllowed(long repositoryId)
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            repositoryId != options.AllowedRepositoryId ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw new GitHubRemoteReadbackException("RemoteReadbackDisabled");
    }

    private static GitHubRemoteReadbackStatus Map(GitHubRemoteReadbackRecord x) =>
        new(x.Id, x.GrantId, x.RepositoryId, x.State,
            x.BaselineSha256, x.CandidateSetSha256, x.RefSetSha256,
            x.TipEvidenceSha256, x.AssessmentSha256,
            x.JournalEvidenceSha256, x.ObservedBy,
            x.ObservedAtUtc, x.RecordedAtUtc,
            x.TransportAttempted, x.RemoteWritesAuthorized,
            x.MayAutomaticallyRetry);
}
