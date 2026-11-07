using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitHubRemoteSandboxReviewSessionRecord
{
    public Guid Id { get; set; }
    public Guid HazardId { get; set; }
    public Guid GrantId { get; set; }
    public Guid FenceRequestId { get; set; }
    public long FenceGeneration { get; set; }
    public long RepositoryId { get; set; }
    public Guid ObservedBy { get; set; }
    public string PacketSha256 { get; set; } = "";
    public string CandidateSetSha256 { get; set; } = "";
    public string PreflightSha256 { get; set; } = "";
    public string BaselineSha256 { get; set; } = "";
    public string DispatchReviewSha256 { get; set; } = "";
    public string LiveRefEvidenceSha256 { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string EvidenceSha256 { get; set; } = "";
    public string State { get; set; } = "";
    public bool TransportInvoked { get; set; }
    public bool RemoteWritesAuthorized { get; set; }
    public bool MayAutomaticallyRetry { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitHubRemoteSandboxReviewSessionEventRecord
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

// F-078 is NOT registered in production DI/API. The only supported use
// is AFTER F-076's authenticated and F-075's locked F-074 review;
// recording a DTO is not independent proof of its origin. Every record
// remains permanently non-executable, even if forged.
public sealed class PostgresGitHubRemoteSandboxReviewSessionJournal(
    AppDbContext db, GitHubRemoteTransmitHazardOptions options)
    : IGitHubRemoteSandboxReviewSessionRecorder
{
    public async Task<GitHubRemoteSandboxReviewSessionReceipt> RecordAsync(
        GitHubRemoteCreateOnlyDispatchReview review,
        Guid independentActor, CancellationToken ct)
    {
        if (review is null || independentActor == Guid.Empty ||
            review.HazardId == Guid.Empty || review.GrantId == Guid.Empty ||
            review.FenceRequestId == Guid.Empty ||
            review.FenceGeneration <= 0 || review.RepositoryId <= 0 ||
            review.State != GitHubRemoteCreateOnlyDispatchIntegrity.State ||
            review.RemoteTransportInvoked ||
            review.RemoteWritesAuthorized || review.MayAutomaticallyRetry ||
            review.MigrationComplete ||
            !Valid(review.EvidenceSha256) ||
            !Valid(review.PacketSha256) ||
            !Valid(review.CandidateSetSha256) ||
            !Valid(review.PreflightSha256) ||
            !Valid(review.BaselineSha256) ||
            !Valid(review.ReviewedPolicySha256) ||
            !Valid(review.OriginalPolicySha256) ||
            !Valid(review.LastRefObservationSha256))
            throw Refused("SandboxReviewSessionInputInvalid");
        RequireAllowed(review.RepositoryId);

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        // Order is identical to F-075; the second lock serializes F-055
        // possible-send markers and F-052 cancellation during the append.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(732911, hashtext({review.RepositoryId.ToString()}))",
            ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({review.RepositoryId.ToString()}))",
            ct);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == review.HazardId &&
                x.RepositoryId == review.RepositoryId, ct);
        var grant = hazard is null ? null :
            await db.GitHubRemoteTestGrants.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == hazard.GrantId &&
                    x.RepositoryId == review.RepositoryId, ct);
        var fence = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == review.RepositoryId, ct);
        var attempt = grant is null ? null :
            await db.GitHubTransferAttempts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == grant.AttemptId &&
                    x.RepositoryId == review.RepositoryId, ct);
        var lease = await db.GitHubTargetPreviewLeases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == review.RepositoryId, ct);
        if (hazard is null || grant is null || fence is null ||
            attempt is null || lease is null ||
            hazard.State != GitHubRemoteTransmitHazardStates.PreparedWriteDisabled ||
            hazard.MarkedBy is not null || hazard.DeadlineUtc <= now ||
            hazard.TransportAuthorized || hazard.TransportInvoked ||
            hazard.RemoteWritesPerformed || hazard.MayAutomaticallyRetry ||
            hazard.GrantId != review.GrantId ||
            hazard.FenceRequestId != review.FenceRequestId ||
            hazard.FenceGeneration != review.FenceGeneration ||
            hazard.PacketSha256 != review.PacketSha256 ||
            hazard.CandidateSetSha256 != review.CandidateSetSha256 ||
            hazard.BaselineSha256 != review.BaselineSha256 ||
            hazard.PreparedBy == independentActor ||
            grant.AttemptId != attempt.Id ||
            grant.PreparationId != attempt.PreparationId ||
            grant.PlanId != attempt.PlanId ||
            grant.ScopeId != attempt.ScopeId ||
            fence.AttemptId != attempt.Id ||
            fence.PreparationId != grant.PreparationId ||
            fence.PlanId != grant.PlanId ||
            fence.ScopeId != grant.ScopeId ||
            !string.Equals(grant.Organization, fence.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(grant.Repository, fence.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.Organization, fence.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.Repository, fence.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            grant.State != GitHubRemoteTestGrantStates.ReviewedWriteDisabled ||
            grant.DeadlineUtc <= now ||
            grant.Id != review.GrantId ||
            grant.FenceRequestId != review.FenceRequestId ||
            grant.FenceGeneration != review.FenceGeneration ||
            grant.PacketSha256 != review.PacketSha256 ||
            grant.CandidateSetSha256 != review.CandidateSetSha256 ||
            grant.BaselineSha256 != review.BaselineSha256 ||
            grant.OriginalPolicySha256 != review.OriginalPolicySha256 ||
            grant.PreflightSha256 != review.PreflightSha256 ||
            grant.RequestedBy == independentActor ||
            grant.ReviewedBy is null ||
            grant.ReviewedBy == grant.RequestedBy ||
            grant.ReviewedBy == independentActor ||
            grant.ExecutionAuthorized || grant.RemoteWritesPerformed ||
            grant.ReadyForExecution ||
            fence.RequestId != review.FenceRequestId ||
            fence.Generation != review.FenceGeneration ||
            fence.State != GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled ||
            fence.DeadlineUtc <= now ||
            fence.PacketSha256 != review.PacketSha256 ||
            fence.CandidateSetSha256 != review.CandidateSetSha256 ||
            fence.PolicySha256 != review.OriginalPolicySha256 ||
            fence.RequestedBy == independentActor ||
            fence.ReviewedBy is null ||
            fence.ReviewedBy == independentActor ||
            fence.ExecutionAuthorized || fence.RemoteWritesPerformed ||
            fence.ReadyForExecution ||
            attempt.State != GitHubTransferAttemptStates.BlockedAtWriteBarrier ||
            attempt.PacketSha256 != review.PacketSha256 ||
            attempt.CandidateSetSha256 != review.CandidateSetSha256 ||
            attempt.PolicySha256 != review.OriginalPolicySha256 ||
            attempt.RequestedBy == independentActor ||
            attempt.ReviewedBy == independentActor ||
            attempt.TransportAttempted || attempt.ExecutionAuthorized ||
            attempt.RemoteWritesPerformed || attempt.ReadyForExecution ||
            lease.State != GitHubTargetPreviewLeaseIntegrity.Held ||
            lease.LeaseUntilUtc is null || lease.LeaseUntilUtc <= now ||
            lease.LeaseToken != attempt.LeaseToken ||
            lease.Generation != attempt.LeaseGeneration ||
            lease.ActorId != attempt.RequestedBy ||
            lease.PacketSha256 != review.PacketSha256 ||
            lease.ExecutionAuthorized || lease.RemoteWritesPerformed ||
            lease.ReadyForExecution ||
            review.ObservedAtUtc > now.AddSeconds(5) ||
            review.ObservedAtUtc <= now.AddSeconds(-10) ||
            review.ExpiresAtUtc <= now)
            throw Refused("SandboxReviewSessionAuthorityChanged");

        var grantEvents = await db.GitHubRemoteTestGrantEvents.AsNoTracking()
            .Where(x => x.GrantId == grant.Id).OrderBy(x => x.Id)
            .Select(x => x.Type).Take(3).ToArrayAsync(ct);
        var hazardEvents = await db.GitHubRemoteTransmitHazardEvents.AsNoTracking()
            .Where(x => x.HazardId == hazard.Id).OrderBy(x => x.Id)
            .Select(x => x.Type).Take(2).ToArrayAsync(ct);
        if (!grantEvents.SequenceEqual(new[] {
                GitHubRemoteTestGrantStates.Proposed,
                GitHubRemoteTestGrantStates.ReviewedWriteDisabled }) ||
            !hazardEvents.SequenceEqual(new[] {
                GitHubRemoteTransmitHazardStates.PreparedWriteDisabled }))
            throw Refused("SandboxReviewSessionAuditInvalid");

        // F-077 binds this historical preflight SHA to the immutable
        // original F-051 observation and a unique Recorded event.
        var prior = await db.GitHubRemoteAppPreflightEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == review.FenceRequestId &&
                x.Generation == review.FenceGeneration &&
                x.RepositoryId == review.RepositoryId &&
                x.EvidenceSha256 == review.PreflightSha256, ct);
        if (prior is null ||
            prior.PacketSha256 != review.PacketSha256 ||
            prior.CandidateSetSha256 != review.CandidateSetSha256 ||
            prior.OriginalPolicySha256 != review.OriginalPolicySha256 ||
            prior.CurrentPolicySha256 != review.ReviewedPolicySha256 ||
            prior.ObservedBaselineSha256 != review.BaselineSha256 ||
            prior.ExpectedBaselineSha256 != review.BaselineSha256 ||
            prior.State != GitHubRemoteAppPreflightIntegrity.VerifiedState ||
            prior.RemoteWritesPerformed || prior.ExecutionAuthorized ||
            prior.ReadyForExecution ||
            prior.EvidenceSha256 != GitHubRemoteAppPreflightIntegrity
                .RecomputeHistoricalEvidenceSha256(
                    new(prior.RequestId, prior.Generation,
                        prior.RepositoryId, prior.PacketSha256,
                        prior.CandidateSetSha256,
                        prior.OriginalPolicySha256, prior.CurrentPolicySha256,
                        prior.ExpectedBaselineSha256,
                        prior.ObservedBaselineSha256,
                        prior.MatchingBaselineRefs, prior.AbsentCreateCandidates,
                        prior.ObservedAtUtc, prior.TokenExpiresAtUtc,
                        prior.EvidenceSha256, prior.State,
                        false, false, false),
                    prior.PolicyObservedAtUtc))
            throw Refused("SandboxReviewSessionPreflightInvalid");
        var priorEvents = await db.GitHubRemoteAppPreflightEvents.AsNoTracking()
            .Where(x => x.RequestId == prior.RequestId &&
                x.Generation == prior.Generation &&
                x.EvidenceSha256 == prior.EvidenceSha256)
            .Take(2).ToArrayAsync(ct);
        if (priorEvents.Length != 1 ||
            priorEvents[0].Type != "Recorded" ||
            priorEvents[0].RepositoryId != review.RepositoryId ||
            priorEvents[0].OccurredAtUtc != prior.RecordedAtUtc)
            throw Refused("SandboxReviewSessionPreflightInvalid");

        var previous = await db.GitHubRemoteSandboxReviewSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.HazardId == review.HazardId, ct);
        if (previous is not null)
        {
            var original = await VerifyAuditAsync(previous, ct);
            if (original.ObservedBy != independentActor ||
                original.DispatchReviewSha256 != review.EvidenceSha256 ||
                original.LiveRefEvidenceSha256 !=
                    review.LastRefObservationSha256 ||
                original.PreflightSha256 != review.PreflightSha256 ||
                original.FenceRequestId != review.FenceRequestId ||
                original.FenceGeneration != review.FenceGeneration)
                throw Refused("SandboxReviewSessionConflict");
            await tx.CommitAsync(ct);
            return original;
        }

        var expires = new[] {
            review.ExpiresAtUtc, grant.DeadlineUtc,
            fence.DeadlineUtc, hazard.DeadlineUtc, lease.LeaseUntilUtc.Value
        }.Min();
        if (expires <= now)
            throw Refused("SandboxReviewSessionExpired");
        var session = new GitHubRemoteSandboxReviewSessionReceipt(
            Guid.NewGuid(), review.HazardId, review.GrantId,
            review.FenceRequestId, review.FenceGeneration,
            review.RepositoryId, independentActor,
            review.PacketSha256, review.CandidateSetSha256,
            review.PreflightSha256, review.BaselineSha256,
            review.EvidenceSha256, review.LastRefObservationSha256,
            now, expires, "", GitHubRemoteSandboxReviewSessionIntegrity.Recorded,
            false, false, false, false);
        session = session with {
            EvidenceSha256 = GitHubRemoteSandboxReviewSessionIntegrity.Digest(session)
        };
        db.GitHubRemoteSandboxReviewSessions.Add(new()
        {
            Id = session.Id, HazardId = session.HazardId,
            GrantId = session.GrantId,
            FenceRequestId = session.FenceRequestId,
            FenceGeneration = session.FenceGeneration,
            RepositoryId = session.RepositoryId, ObservedBy = session.ObservedBy,
            PacketSha256 = session.PacketSha256,
            CandidateSetSha256 = session.CandidateSetSha256,
            PreflightSha256 = session.PreflightSha256,
            BaselineSha256 = session.BaselineSha256,
            DispatchReviewSha256 = session.DispatchReviewSha256,
            LiveRefEvidenceSha256 = session.LiveRefEvidenceSha256,
            RecordedAtUtc = session.RecordedAtUtc,
            ExpiresAtUtc = session.ExpiresAtUtc,
            EvidenceSha256 = session.EvidenceSha256,
            State = session.State,
            TransportInvoked = false, RemoteWritesAuthorized = false,
            MayAutomaticallyRetry = false, MigrationComplete = false
        });
        db.GitHubRemoteSandboxReviewSessionEvents.Add(new()
        {
            SessionId = session.Id, Type = "Recorded",
            ActorId = independentActor, OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return session;
    }

    public async Task<GitHubRemoteSandboxReviewSessionInspection?> InspectAsync(
        Guid sessionId, long repositoryId, CancellationToken ct)
    {
        RequireAllowed(repositoryId);
        if (sessionId == Guid.Empty)
            throw Refused("SandboxReviewSessionInputInvalid");
        var stored = await db.GitHubRemoteSandboxReviewSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sessionId &&
                x.RepositoryId == repositoryId, ct);
        if (stored is null) return null;
        var session = await VerifyAuditAsync(stored, ct);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == session.HazardId &&
                x.RepositoryId == session.RepositoryId, ct);
        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == session.GrantId &&
                x.RepositoryId == session.RepositoryId, ct);
        var current = session.ExpiresAtUtc > now &&
            hazard is not null && grant is not null &&
            hazard.State == GitHubRemoteTransmitHazardStates.PreparedWriteDisabled &&
            hazard.MarkedBy is null && !hazard.TransportInvoked &&
            !hazard.TransportAuthorized && !hazard.RemoteWritesPerformed &&
            !hazard.MayAutomaticallyRetry &&
            grant.State == GitHubRemoteTestGrantStates.ReviewedWriteDisabled &&
            grant.PreflightSha256 == session.PreflightSha256 &&
            grant.PacketSha256 == session.PacketSha256 &&
            grant.DeadlineUtc > now &&
            hazard.GrantId == session.GrantId &&
            hazard.FenceRequestId == session.FenceRequestId &&
            hazard.FenceGeneration == session.FenceGeneration;
        return new(session,
            current ? GitHubRemoteSandboxReviewSessionIntegrity.Current :
                GitHubRemoteSandboxReviewSessionIntegrity.Historical,
            !current, false, false, false);
    }

    private async Task<GitHubRemoteSandboxReviewSessionReceipt> VerifyAuditAsync(
        GitHubRemoteSandboxReviewSessionRecord s, CancellationToken ct)
    {
        var value = new GitHubRemoteSandboxReviewSessionReceipt(
            s.Id, s.HazardId, s.GrantId, s.FenceRequestId,
            s.FenceGeneration, s.RepositoryId, s.ObservedBy,
            s.PacketSha256, s.CandidateSetSha256, s.PreflightSha256,
            s.BaselineSha256, s.DispatchReviewSha256,
            s.LiveRefEvidenceSha256, s.RecordedAtUtc, s.ExpiresAtUtc,
            s.EvidenceSha256, s.State, s.TransportInvoked,
            s.RemoteWritesAuthorized, s.MayAutomaticallyRetry,
            s.MigrationComplete);
        try
        {
            GitHubRemoteSandboxReviewSessionIntegrity.Verify(value);
        }
        catch (GitHubRemoteSandboxReviewSessionException)
        {
            throw Refused("SandboxReviewSessionHistoryCorrupt");
        }
        var audit = await db.GitHubRemoteSandboxReviewSessionEvents.AsNoTracking()
            .Where(x => x.SessionId == s.Id).Take(2).ToArrayAsync(ct);
        if (audit.Length != 1 || audit[0].Type != "Recorded" ||
            audit[0].ActorId != s.ObservedBy ||
            audit[0].OccurredAtUtc != s.RecordedAtUtc)
            throw Refused("SandboxReviewSessionHistoryCorrupt");
        return value;
    }

    private void RequireAllowed(long repositoryId)
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            repositoryId != options.AllowedRepositoryId ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw Refused("SandboxReviewSessionDisabled");
    }

    private static bool Valid(string? value) =>
        GitSourceBundleStorageCheck.ValidSha(value);

    private static GitHubRemoteSandboxReviewSessionException Refused(
        string code) => new(code);
}

