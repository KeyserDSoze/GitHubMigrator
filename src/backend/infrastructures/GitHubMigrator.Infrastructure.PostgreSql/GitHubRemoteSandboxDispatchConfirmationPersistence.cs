using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitHubRemoteSandboxDispatchConfirmationRecord
{
    public Guid Id { get; set; }
    public Guid IntentId { get; set; }
    public Guid SessionId { get; set; }
    public Guid HazardId { get; set; }
    public Guid GrantId { get; set; }
    public long RepositoryId { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid ConfirmedBy { get; set; }
    public string IntentEvidenceSha256 { get; set; } = "";
    public string SessionEvidenceSha256 { get; set; } = "";
    public string PacketSha256 { get; set; } = "";
    public string CandidateSetSha256 { get; set; } = "";
    public string LiveRefEvidenceSha256 { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string EvidenceSha256 { get; set; } = "";
    public string State { get; set; } = "";
    public bool CanDispatch { get; set; }
    public bool TransportInvoked { get; set; }
    public bool MayAutomaticallyRetry { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitHubRemoteSandboxDispatchConfirmationEventRecord
{
    public long Id { get; set; }
    public Guid ConfirmationId { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

// F-081 is an audited *confirmation*, not a capability. The reviewed
// F-080 intent and every predecessor must still be live after taking
// exactly the two established PostgreSQL repository advisory locks.
public sealed class PostgresGitHubRemoteSandboxDispatchConfirmationJournal(
    AppDbContext db, GitHubRemoteTransmitHazardOptions options,
    PostgresGitHubRemoteSandboxDispatchIntentJournal intents,
    PostgresGitHubRemoteSandboxReviewSessionJournal sessions)
{
    public async Task<GitHubRemoteSandboxDispatchConfirmationReceipt> ConfirmAsync(
        Guid intentId, Guid preparationId, Guid planId, Guid scopeId,
        Guid actorId, CancellationToken ct)
    {
        var repo = RequireAllowed();
        if (intentId == Guid.Empty || preparationId == Guid.Empty ||
            planId == Guid.Empty || scopeId == Guid.Empty ||
            actorId == Guid.Empty)
            throw Refused("SandboxDispatchConfirmationInputInvalid");

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(732911, hashtext({repo.ToString()}))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({repo.ToString()}))", ct);
        var now = await DbNowAsync(ct);
        var inspected = await intents.InspectScopedAsync(
            intentId, preparationId, planId, scopeId, ct);
        if (inspected is null)
            throw Refused("SandboxDispatchConfirmationIntentNotFound");
        var intent = inspected.Intent;
        if (inspected.Status != GitHubRemoteSandboxDispatchIntentIntegrity.Current ||
            intent.ExpiresAtUtc <= now || intent.RecordedAtUtc > now ||
            intent.RequestedBy == actorId || intent.CanExecute ||
            intent.TransportInvoked || intent.MayAutomaticallyRetry ||
            intent.MigrationComplete)
            throw Refused("SandboxDispatchConfirmationIntentStale");

        var review = await sessions.InspectScopedAsync(intent.SessionId,
            preparationId, planId, scopeId, repo, ct);
        if (review is null ||
            review.Status != GitHubRemoteSandboxReviewSessionIntegrity.Current ||
            review.Session.ExpiresAtUtc <= now ||
            review.Session.RecordedAtUtc > now ||
            review.Session.ObservedBy == actorId ||
            review.Session.HazardId != intent.HazardId ||
            review.Session.GrantId != intent.GrantId ||
            review.Session.EvidenceSha256 != intent.SessionEvidenceSha256 ||
            review.Session.PacketSha256 != intent.PacketSha256 ||
            review.Session.CandidateSetSha256 != intent.CandidateSetSha256 ||
            review.Session.LiveRefEvidenceSha256 != intent.LiveRefEvidenceSha256)
            throw Refused("SandboxDispatchConfirmationReviewChanged");

        var session = review.Session;
        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == intent.HazardId &&
                x.RepositoryId == repo, ct);
        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == intent.GrantId &&
                x.RepositoryId == repo, ct);
        var fence = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == repo, ct);
        var attempt = grant is null ? null :
            await db.GitHubTransferAttempts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == grant.AttemptId &&
                    x.RepositoryId == repo, ct);
        var lease = await db.GitHubTargetPreviewLeases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == repo, ct);

        if (hazard is null || grant is null || fence is null ||
            attempt is null || lease is null ||
            hazard.State != GitHubRemoteTransmitHazardStates.PreparedWriteDisabled ||
            hazard.MarkedBy is not null || hazard.DeadlineUtc <= now ||
            hazard.TransportAuthorized || hazard.TransportInvoked ||
            hazard.RemoteWritesPerformed || hazard.MayAutomaticallyRetry ||
            hazard.PreparedBy == actorId ||
            hazard.GrantId != intent.GrantId ||
            hazard.PacketSha256 != intent.PacketSha256 ||
            hazard.CandidateSetSha256 != intent.CandidateSetSha256 ||
            hazard.BaselineSha256 != session.BaselineSha256 ||
            hazard.FenceRequestId != session.FenceRequestId ||
            hazard.FenceGeneration != session.FenceGeneration ||
            grant.State != GitHubRemoteTestGrantStates.ReviewedWriteDisabled ||
            grant.DeadlineUtc <= now ||
            grant.RequestedBy == actorId || grant.ReviewedBy is null ||
            grant.ReviewedBy == actorId ||
            grant.PreparationId != preparationId || grant.PlanId != planId ||
            grant.ScopeId != scopeId ||
            grant.AttemptId != attempt.Id ||
            grant.PacketSha256 != intent.PacketSha256 ||
            grant.CandidateSetSha256 != intent.CandidateSetSha256 ||
            grant.PreflightSha256 != session.PreflightSha256 ||
            grant.BaselineSha256 != session.BaselineSha256 ||
            grant.FenceRequestId != session.FenceRequestId ||
            grant.FenceGeneration != session.FenceGeneration ||
            grant.ExecutionAuthorized || grant.RemoteWritesPerformed ||
            grant.ReadyForExecution ||
            fence.State != GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled ||
            fence.RequestId != session.FenceRequestId ||
            fence.Generation != session.FenceGeneration ||
            fence.AttemptId != attempt.Id ||
            fence.PreparationId != preparationId ||
            fence.PlanId != planId || fence.ScopeId != scopeId ||
            fence.DeadlineUtc <= now ||
            fence.PacketSha256 != intent.PacketSha256 ||
            fence.CandidateSetSha256 != intent.CandidateSetSha256 ||
            fence.RequestedBy == actorId || fence.ReviewedBy == actorId ||
            fence.ExecutionAuthorized || fence.RemoteWritesPerformed ||
            fence.ReadyForExecution ||
            attempt.State != GitHubTransferAttemptStates.BlockedAtWriteBarrier ||
            attempt.Id != fence.AttemptId || attempt.Id != grant.AttemptId ||
            attempt.PreparationId != preparationId ||
            attempt.PlanId != planId || attempt.ScopeId != scopeId ||
            attempt.PacketSha256 != intent.PacketSha256 ||
            attempt.PolicySha256 != grant.OriginalPolicySha256 ||
            fence.PolicySha256 != grant.OriginalPolicySha256 ||
            attempt.CandidateSetSha256 != intent.CandidateSetSha256 ||
            attempt.RequestedBy == actorId || attempt.ReviewedBy == actorId ||
            attempt.TransportAttempted || attempt.ExecutionAuthorized ||
            attempt.RemoteWritesPerformed || attempt.ReadyForExecution ||
            lease.State != GitHubTargetPreviewLeaseIntegrity.Held ||
            lease.LeaseUntilUtc is null || lease.LeaseUntilUtc <= now ||
            lease.LeaseToken != attempt.LeaseToken ||
            lease.Generation != attempt.LeaseGeneration ||
            lease.PacketSha256 != intent.PacketSha256 ||
            lease.ExecutionAuthorized || lease.RemoteWritesPerformed ||
            lease.ReadyForExecution)
            throw Refused("SandboxDispatchConfirmationAuthorityChanged");

        var previous = await db.GitHubRemoteSandboxDispatchConfirmations
            .AsNoTracking().SingleOrDefaultAsync(x => x.IntentId == intent.Id ||
                x.HazardId == intent.HazardId, ct);
        if (previous is not null)
        {
            var original = await VerifyAuditAsync(previous, ct);
            if (original.IntentId != intent.Id ||
                original.SessionId != intent.SessionId ||
                original.RequestedBy != intent.RequestedBy ||
                original.ConfirmedBy != actorId ||
                original.IntentEvidenceSha256 != intent.EvidenceSha256 ||
                original.SessionEvidenceSha256 != intent.SessionEvidenceSha256 ||
                original.PacketSha256 != intent.PacketSha256 ||
                original.CandidateSetSha256 != intent.CandidateSetSha256 ||
                original.LiveRefEvidenceSha256 !=
                    intent.LiveRefEvidenceSha256)
                throw Refused("SandboxDispatchConfirmationConflict");
            await tx.CommitAsync(ct);
            return original;
        }

        var expiry = new[] {
            intent.ExpiresAtUtc, session.ExpiresAtUtc,
            hazard.DeadlineUtc, grant.DeadlineUtc,
            fence.DeadlineUtc, lease.LeaseUntilUtc!.Value
        }.Min();
        if (expiry <= now)
            throw Refused("SandboxDispatchConfirmationIntentStale");
        var value = new GitHubRemoteSandboxDispatchConfirmationReceipt(
            Guid.NewGuid(), intent.Id, intent.SessionId, intent.HazardId,
            intent.GrantId, repo, intent.RequestedBy, actorId,
            intent.EvidenceSha256, intent.SessionEvidenceSha256,
            intent.PacketSha256, intent.CandidateSetSha256,
            intent.LiveRefEvidenceSha256, now, expiry, "",
            GitHubRemoteSandboxDispatchConfirmationIntegrity.Recorded,
            false, false, false, false);
        value = value with {
            EvidenceSha256 =
                GitHubRemoteSandboxDispatchConfirmationIntegrity.Digest(value)
        };
        db.GitHubRemoteSandboxDispatchConfirmations.Add(new()
        {
            Id = value.Id, IntentId = value.IntentId,
            SessionId = value.SessionId, HazardId = value.HazardId,
            GrantId = value.GrantId, RepositoryId = value.RepositoryId,
            RequestedBy = value.RequestedBy, ConfirmedBy = value.ConfirmedBy,
            IntentEvidenceSha256 = value.IntentEvidenceSha256,
            SessionEvidenceSha256 = value.SessionEvidenceSha256,
            PacketSha256 = value.PacketSha256,
            CandidateSetSha256 = value.CandidateSetSha256,
            LiveRefEvidenceSha256 = value.LiveRefEvidenceSha256,
            RecordedAtUtc = value.RecordedAtUtc,
            ExpiresAtUtc = value.ExpiresAtUtc,
            EvidenceSha256 = value.EvidenceSha256, State = value.State,
            CanDispatch = false, TransportInvoked = false,
            MayAutomaticallyRetry = false, MigrationComplete = false
        });
        db.GitHubRemoteSandboxDispatchConfirmationEvents.Add(new()
        {
            ConfirmationId = value.Id, Type = "Confirmed",
            ActorId = actorId, OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return value;
    }

    public async Task<GitHubRemoteSandboxDispatchConfirmationInspection?>
        InspectScopedAsync(Guid confirmationId, Guid intentId,
            Guid preparationId, Guid planId, Guid scopeId,
            CancellationToken ct)
    {
        var repo = RequireAllowed();
        if (confirmationId == Guid.Empty || intentId == Guid.Empty ||
            preparationId == Guid.Empty || planId == Guid.Empty ||
            scopeId == Guid.Empty)
            throw Refused("SandboxDispatchConfirmationInputInvalid");
        var stored = await db.GitHubRemoteSandboxDispatchConfirmations
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == confirmationId && x.IntentId == intentId &&
                x.RepositoryId == repo &&
                db.GitHubRemoteTestGrants.Any(g =>
                    g.Id == x.GrantId && g.RepositoryId == repo &&
                    g.PreparationId == preparationId &&
                    g.PlanId == planId && g.ScopeId == scopeId), ct);
        if (stored is null) return null;
        var value = await VerifyAuditAsync(stored, ct);
        var intent = await intents.InspectScopedAsync(
            intentId, preparationId, planId, scopeId, ct);
        if (intent is null ||
            value.IntentEvidenceSha256 != intent.Intent.EvidenceSha256 ||
            value.SessionEvidenceSha256 != intent.Intent.SessionEvidenceSha256 ||
            value.SessionId != intent.Intent.SessionId ||
            value.HazardId != intent.Intent.HazardId ||
            value.GrantId != intent.Intent.GrantId ||
            value.RequestedBy != intent.Intent.RequestedBy ||
            value.PacketSha256 != intent.Intent.PacketSha256 ||
            value.CandidateSetSha256 != intent.Intent.CandidateSetSha256 ||
            value.LiveRefEvidenceSha256 !=
                intent.Intent.LiveRefEvidenceSha256)
            throw Refused("SandboxDispatchConfirmationHistoryCorrupt");
        var now = await DbNowAsync(ct);
        var current = intent.Status ==
            GitHubRemoteSandboxDispatchIntentIntegrity.Current &&
            value.ExpiresAtUtc > now;
        return new(value,
            current ? GitHubRemoteSandboxDispatchConfirmationIntegrity.Current :
                GitHubRemoteSandboxDispatchConfirmationIntegrity.Historical,
            !current, false, false, false);
    }

    private async Task<GitHubRemoteSandboxDispatchConfirmationReceipt>
        VerifyAuditAsync(GitHubRemoteSandboxDispatchConfirmationRecord s,
            CancellationToken ct)
    {
        var value = new GitHubRemoteSandboxDispatchConfirmationReceipt(
            s.Id, s.IntentId, s.SessionId, s.HazardId, s.GrantId,
            s.RepositoryId, s.RequestedBy, s.ConfirmedBy,
            s.IntentEvidenceSha256, s.SessionEvidenceSha256,
            s.PacketSha256, s.CandidateSetSha256, s.LiveRefEvidenceSha256,
            s.RecordedAtUtc, s.ExpiresAtUtc, s.EvidenceSha256, s.State,
            s.CanDispatch, s.TransportInvoked, s.MayAutomaticallyRetry,
            s.MigrationComplete);
        try
        {
            GitHubRemoteSandboxDispatchConfirmationIntegrity.Verify(value);
        }
        catch (GitHubRemoteSandboxDispatchConfirmationException)
        {
            throw Refused("SandboxDispatchConfirmationHistoryCorrupt");
        }
        var audit = await db.GitHubRemoteSandboxDispatchConfirmationEvents
            .AsNoTracking().Where(x => x.ConfirmationId == value.Id)
            .Take(2).ToArrayAsync(ct);
        if (audit.Length != 1 || audit[0].Type != "Confirmed" ||
            audit[0].ActorId != value.ConfirmedBy ||
            audit[0].OccurredAtUtc != value.RecordedAtUtc)
            throw Refused("SandboxDispatchConfirmationHistoryCorrupt");
        return value;
    }

    private Task<DateTimeOffset> DbNowAsync(CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    private long RequireAllowed()
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw Refused("SandboxDispatchConfirmationDisabled");
        return options.AllowedRepositoryId;
    }

    private static GitHubRemoteSandboxDispatchConfirmationException Refused(
        string code) => new(code);
}
