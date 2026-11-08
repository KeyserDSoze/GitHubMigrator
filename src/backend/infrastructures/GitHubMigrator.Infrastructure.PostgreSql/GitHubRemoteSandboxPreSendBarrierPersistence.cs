using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitHubRemoteSandboxPreSendBarrierRecord
{
    public Guid Id { get; set; }
    public Guid ConfirmationId { get; set; }
    public Guid IntentId { get; set; }
    public Guid SessionId { get; set; }
    public Guid HazardId { get; set; }
    public Guid GrantId { get; set; }
    public long RepositoryId { get; set; }
    public Guid MarkedBy { get; set; }
    public string ConfirmationEvidenceSha256 { get; set; } = "";
    public string IntentEvidenceSha256 { get; set; } = "";
    public string SessionEvidenceSha256 { get; set; } = "";
    public string PacketSha256 { get; set; } = "";
    public string CandidateSetSha256 { get; set; } = "";
    public string LiveRefEvidenceSha256 { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string EvidenceSha256 { get; set; } = "";
    public string State { get; set; } = "";
    public bool CanExecute { get; set; }
    public bool TransportInvoked { get; set; }
    public bool MayAutomaticallyRetry { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitHubRemoteSandboxPreSendBarrierEventRecord
{
    public long Id { get; set; }
    public Guid BarrierId { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

// Internal-only precursor for a future dedicated sandbox transport.
// It atomically marks F-055 MayHaveTransmitted BEFORE any possible
// outbound I/O. The marker is NOT a send authorization, and replay is
// categorically refused even for the same actor or identical SHA.
public sealed class PostgresGitHubRemoteSandboxPreSendBarrierJournal(
    AppDbContext db, GitHubRemoteTransmitHazardOptions options,
    PostgresGitHubRemoteSandboxDispatchConfirmationJournal confirmations)
{
    public async Task<GitHubRemoteSandboxPreSendBarrierReceipt>
        RecordPossibleSendAsync(Guid confirmationId, Guid intentId,
            Guid preparationId, Guid planId, Guid scopeId,
            Guid independentOperator, CancellationToken ct)
    {
        var repo = RequireAllowed();
        if (confirmationId == Guid.Empty || intentId == Guid.Empty ||
            preparationId == Guid.Empty || planId == Guid.Empty ||
            scopeId == Guid.Empty || independentOperator == Guid.Empty)
            throw Refused("PreSendBarrierInputInvalid");

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        // Matches F-045/F-047 and F-050/F-052/F-055/F-078/F-080/F-081:
        // read the live authority only after acquiring both locks.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(732911, hashtext({repo.ToString()}))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({repo.ToString()}))", ct);
        var now = await DbNowAsync(ct);

        var inspected = await confirmations.InspectScopedAsync(
            confirmationId, intentId, preparationId, planId, scopeId, ct);
        if (inspected is null)
            throw Refused("PreSendBarrierConfirmationNotFound");
        var confirmation = inspected.Confirmation;
        if (inspected.Status !=
                GitHubRemoteSandboxDispatchConfirmationIntegrity.Current ||
            confirmation.ExpiresAtUtc <= now ||
            confirmation.RecordedAtUtc > now ||
            confirmation.CanDispatch || confirmation.TransportInvoked ||
            confirmation.MayAutomaticallyRetry || confirmation.MigrationComplete ||
            (confirmation.ConfirmedBy == independentOperator ||
            confirmation.RequestedBy == independentOperator))
            throw Refused("PreSendBarrierConfirmationStale");

        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == confirmation.HazardId &&
                x.RepositoryId == repo, ct);
        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == confirmation.GrantId &&
                x.RepositoryId == repo, ct);
        var fence = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == repo, ct);
        var attempt = grant is null ? null :
            await db.GitHubTransferAttempts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == grant.AttemptId &&
                    x.RepositoryId == repo, ct);
        var lease = await db.GitHubTargetPreviewLeases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == repo, ct);
        var session = await db.GitHubRemoteSandboxReviewSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == confirmation.SessionId &&
                x.RepositoryId == repo, ct);
        var intent = await db.GitHubRemoteSandboxDispatchIntents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == confirmation.IntentId &&
                x.RepositoryId == repo, ct);

        if (hazard is null || grant is null || fence is null ||
            attempt is null || lease is null || session is null ||
            intent is null ||
            hazard.State != GitHubRemoteTransmitHazardStates.PreparedWriteDisabled ||
            hazard.MarkedBy is not null || hazard.DeadlineUtc <= now ||
            hazard.TransportAuthorized || hazard.TransportInvoked ||
            hazard.RemoteWritesPerformed || hazard.MayAutomaticallyRetry ||
            hazard.PreparedBy == independentOperator ||
            hazard.GrantId != grant.Id ||
            hazard.FenceRequestId != session.FenceRequestId ||
            hazard.FenceGeneration != session.FenceGeneration ||
            hazard.PacketSha256 != confirmation.PacketSha256 ||
            hazard.CandidateSetSha256 != confirmation.CandidateSetSha256 ||
            hazard.BaselineSha256 != session.BaselineSha256 ||
            grant.State != GitHubRemoteTestGrantStates.ReviewedWriteDisabled ||
            grant.DeadlineUtc <= now ||
            grant.RequestedBy == independentOperator ||
            grant.ReviewedBy is null || grant.ReviewedBy == independentOperator ||
            grant.FenceRequestId != session.FenceRequestId ||
            grant.FenceGeneration != session.FenceGeneration ||
            grant.AttemptId != attempt.Id ||
            grant.PreparationId != preparationId ||
            grant.PlanId != planId || grant.ScopeId != scopeId ||
            grant.PacketSha256 != confirmation.PacketSha256 ||
            grant.CandidateSetSha256 != confirmation.CandidateSetSha256 ||
            grant.BaselineSha256 != session.BaselineSha256 ||
            grant.PreflightSha256 != session.PreflightSha256 ||
            grant.ExecutionAuthorized || grant.RemoteWritesPerformed ||
            grant.ReadyForExecution ||
            fence.State != GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled ||
            fence.RequestId != session.FenceRequestId ||
            fence.Generation != session.FenceGeneration ||
            fence.AttemptId != attempt.Id ||
            fence.PreparationId != preparationId ||
            fence.PlanId != planId || fence.ScopeId != scopeId ||
            fence.DeadlineUtc <= now ||
            fence.PacketSha256 != confirmation.PacketSha256 ||
            fence.CandidateSetSha256 != confirmation.CandidateSetSha256 ||
            fence.PolicySha256 != grant.OriginalPolicySha256 ||
            fence.RequestedBy == independentOperator ||
            fence.ReviewedBy == independentOperator ||
            fence.ExecutionAuthorized || fence.RemoteWritesPerformed ||
            fence.ReadyForExecution ||
            attempt.State != GitHubTransferAttemptStates.BlockedAtWriteBarrier ||
            attempt.PreparationId != preparationId ||
            attempt.PlanId != planId || attempt.ScopeId != scopeId ||
            attempt.PacketSha256 != confirmation.PacketSha256 ||
            attempt.CandidateSetSha256 != confirmation.CandidateSetSha256 ||
            attempt.PolicySha256 != grant.OriginalPolicySha256 ||
            attempt.RequestedBy == independentOperator ||
            attempt.ReviewedBy == independentOperator ||
            attempt.TransportAttempted || attempt.ExecutionAuthorized ||
            attempt.RemoteWritesPerformed || attempt.ReadyForExecution ||
            lease.State != GitHubTargetPreviewLeaseIntegrity.Held ||
            lease.LeaseUntilUtc is null || lease.LeaseUntilUtc <= now ||
            lease.LeaseToken != attempt.LeaseToken ||
            lease.Generation != attempt.LeaseGeneration ||
            lease.PacketSha256 != confirmation.PacketSha256 ||
            lease.ExecutionAuthorized || lease.RemoteWritesPerformed ||
            lease.ReadyForExecution ||
            session.EvidenceSha256 != confirmation.SessionEvidenceSha256 ||
            session.ObservedBy == independentOperator ||
            session.ExpiresAtUtc <= now ||
            session.TransportInvoked || session.RemoteWritesAuthorized ||
            session.MayAutomaticallyRetry || session.MigrationComplete ||
            intent.EvidenceSha256 != confirmation.IntentEvidenceSha256 ||
            intent.Id != confirmation.IntentId ||
            intent.SessionId != session.Id ||
            intent.HazardId != hazard.Id ||
            intent.GrantId != grant.Id ||
            intent.ExpiresAtUtc <= now ||
            intent.RequestedBy != confirmation.RequestedBy ||
            intent.RequestedBy == independentOperator ||
            intent.CanExecute || intent.TransportInvoked ||
            intent.MayAutomaticallyRetry || intent.MigrationComplete)
            throw Refused("PreSendBarrierAuthorityChanged");

        // The transaction MUST not be replayable, including after lost
        // acknowledgement. A second invocation always fails closed.
        if (await db.GitHubRemoteSandboxPreSendBarriers.AsNoTracking()
            .AnyAsync(x => x.ConfirmationId == confirmationId ||
                x.HazardId == hazard.Id || x.IntentId == intentId, ct))
            throw Refused("PreSendBarrierEpochConsumed");

        var finalNow = await DbNowAsync(ct);
        if (finalNow >= confirmation.ExpiresAtUtc ||
            finalNow >= intent.ExpiresAtUtc ||
            finalNow >= session.ExpiresAtUtc ||
            finalNow >= grant.DeadlineUtc ||
            finalNow >= fence.DeadlineUtc ||
            finalNow >= hazard.DeadlineUtc ||
            finalNow >= lease.LeaseUntilUtc)
            throw Refused("PreSendBarrierAuthorityExpired");

        // CAS + F-055 event and F-082 receipt/event must COMMIT TOGETHER.
        // If any insert, cancellation or connection dies, all roll back.
        // No outbound Git call occurs before, during, or after this code.
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH marked AS (
              UPDATE github_remote_transmit_hazards
              SET state='MayHaveTransmitted',
                  marked_by={independentOperator},
                  updated_at_utc={finalNow}
              WHERE id={hazard.Id} AND repository_id={repo}
                AND grant_id={grant.Id}
                AND state='PreparedWriteDisabled'
                AND marked_by IS NULL AND deadline_utc>{finalNow}
                AND packet_sha256={confirmation.PacketSha256}
                AND candidate_set_sha256={confirmation.CandidateSetSha256}
                AND baseline_sha256={session.BaselineSha256}
                AND transport_authorized=false AND transport_invoked=false
                AND remote_writes_performed=false AND may_automatically_retry=false
              RETURNING id
            )
            INSERT INTO github_remote_transmit_hazard_events (
                hazard_id,type,actor_id,occurred_at_utc)
            SELECT id,'MayHaveTransmitted',{independentOperator},{finalNow}
            FROM marked
            """, ct);
        if (changed != 1)
            throw Refused("PreSendBarrierMarkLost");

        var value = new GitHubRemoteSandboxPreSendBarrierReceipt(
            Guid.NewGuid(), confirmation.Id, confirmation.IntentId,
            confirmation.SessionId, confirmation.HazardId,
            confirmation.GrantId, repo, independentOperator,
            confirmation.EvidenceSha256,
            confirmation.IntentEvidenceSha256,
            confirmation.SessionEvidenceSha256,
            confirmation.PacketSha256,
            confirmation.CandidateSetSha256,
            confirmation.LiveRefEvidenceSha256, finalNow, "",
            GitHubRemoteSandboxPreSendBarrierIntegrity.Recorded,
            false, false, false, false);
        value = value with {
            EvidenceSha256 = GitHubRemoteSandboxPreSendBarrierIntegrity.Digest(value)
        };
        db.GitHubRemoteSandboxPreSendBarriers.Add(new()
        {
            Id = value.Id, ConfirmationId = value.ConfirmationId,
            IntentId = value.IntentId, SessionId = value.SessionId,
            HazardId = value.HazardId, GrantId = value.GrantId,
            RepositoryId = repo, MarkedBy = independentOperator,
            ConfirmationEvidenceSha256 = value.ConfirmationEvidenceSha256,
            IntentEvidenceSha256 = value.IntentEvidenceSha256,
            SessionEvidenceSha256 = value.SessionEvidenceSha256,
            PacketSha256 = value.PacketSha256,
            CandidateSetSha256 = value.CandidateSetSha256,
            LiveRefEvidenceSha256 = value.LiveRefEvidenceSha256,
            RecordedAtUtc = finalNow, EvidenceSha256 = value.EvidenceSha256,
            State = value.State, CanExecute = false,
            TransportInvoked = false, MayAutomaticallyRetry = false,
            MigrationComplete = false
        });
        db.GitHubRemoteSandboxPreSendBarrierEvents.Add(new()
        {
            BarrierId = value.Id, Type = "Recorded",
            ActorId = independentOperator, OccurredAtUtc = finalNow
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return value;
    }

    // Historical inspection NEVER returns a transport capability. It
    // cross-checks the original F-055 possible-send marker and events.
    public async Task<GitHubRemoteSandboxPreSendBarrierInspection?>
        InspectScopedAsync(Guid barrierId, Guid confirmationId,
            Guid intentId, Guid preparationId, Guid planId,
            Guid scopeId, CancellationToken ct)
    {
        var repo = RequireAllowed();
        if (barrierId == Guid.Empty || confirmationId == Guid.Empty ||
            intentId == Guid.Empty || preparationId == Guid.Empty ||
            planId == Guid.Empty || scopeId == Guid.Empty)
            throw Refused("PreSendBarrierInputInvalid");

        var row = await db.GitHubRemoteSandboxPreSendBarriers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == barrierId &&
                x.ConfirmationId == confirmationId && x.IntentId == intentId &&
                x.RepositoryId == repo &&
                db.GitHubRemoteTestGrants.Any(g =>
                    g.Id == x.GrantId && g.RepositoryId == repo &&
                    g.PreparationId == preparationId &&
                    g.PlanId == planId && g.ScopeId == scopeId), ct);
        if (row is null) return null;
        var value = await VerifyAuditAsync(row, ct);
        var persistedConfirmation =
            await db.GitHubRemoteSandboxDispatchConfirmations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == value.ConfirmationId &&
                x.RepositoryId == repo, ct);
        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == value.HazardId &&
                x.RepositoryId == repo, ct);
        if (persistedConfirmation is null ||
            persistedConfirmation.IntentId != value.IntentId ||
            persistedConfirmation.SessionId != value.SessionId ||
            persistedConfirmation.HazardId != value.HazardId ||
            persistedConfirmation.GrantId != value.GrantId ||
            persistedConfirmation.EvidenceSha256 !=
                value.ConfirmationEvidenceSha256 ||
            persistedConfirmation.IntentEvidenceSha256 != value.IntentEvidenceSha256 ||
            persistedConfirmation.SessionEvidenceSha256 != value.SessionEvidenceSha256 ||
            persistedConfirmation.PacketSha256 != value.PacketSha256 ||
            persistedConfirmation.CandidateSetSha256 != value.CandidateSetSha256 ||
            persistedConfirmation.LiveRefEvidenceSha256 !=
                value.LiveRefEvidenceSha256 ||
            hazard is null ||
            hazard.State != GitHubRemoteTransmitHazardStates.MayHaveTransmitted ||
            hazard.MarkedBy != value.MarkedBy ||
            hazard.UpdatedAtUtc != value.RecordedAtUtc ||
            hazard.GrantId != value.GrantId ||
            hazard.PacketSha256 != value.PacketSha256 ||
            hazard.CandidateSetSha256 != value.CandidateSetSha256)
            throw Refused("PreSendBarrierHistoryCorrupt");
        var hazardEvents = await db.GitHubRemoteTransmitHazardEvents.AsNoTracking()
            .Where(x => x.HazardId == value.HazardId)
            .OrderBy(x => x.Id).Take(3).ToArrayAsync(ct);
        if (hazardEvents.Length != 2 ||
            hazardEvents[0].Type !=
                GitHubRemoteTransmitHazardStates.PreparedWriteDisabled ||
            hazardEvents[1].Type !=
                GitHubRemoteTransmitHazardStates.MayHaveTransmitted ||
            hazardEvents[1].ActorId != value.MarkedBy ||
            hazardEvents[1].OccurredAtUtc != value.RecordedAtUtc)
            throw Refused("PreSendBarrierHistoryCorrupt");
        return new(value, "HistoricalOnly", false, false, false);
    }

    private async Task<GitHubRemoteSandboxPreSendBarrierReceipt>
        VerifyAuditAsync(GitHubRemoteSandboxPreSendBarrierRecord s,
            CancellationToken ct)
    {
        var value = new GitHubRemoteSandboxPreSendBarrierReceipt(
            s.Id, s.ConfirmationId, s.IntentId, s.SessionId,
            s.HazardId, s.GrantId, s.RepositoryId, s.MarkedBy,
            s.ConfirmationEvidenceSha256, s.IntentEvidenceSha256,
            s.SessionEvidenceSha256, s.PacketSha256,
            s.CandidateSetSha256, s.LiveRefEvidenceSha256,
            s.RecordedAtUtc, s.EvidenceSha256, s.State,
            s.CanExecute, s.TransportInvoked,
            s.MayAutomaticallyRetry, s.MigrationComplete);
        try { GitHubRemoteSandboxPreSendBarrierIntegrity.Verify(value); }
        catch (GitHubRemoteSandboxPreSendBarrierException)
        { throw Refused("PreSendBarrierHistoryCorrupt"); }
        var audit = await db.GitHubRemoteSandboxPreSendBarrierEvents.AsNoTracking()
            .Where(x => x.BarrierId == value.Id)
            .Take(2).ToArrayAsync(ct);
        if (audit.Length != 1 || audit[0].Type != "Recorded" ||
            audit[0].ActorId != value.MarkedBy ||
            audit[0].OccurredAtUtc != value.RecordedAtUtc)
            throw Refused("PreSendBarrierHistoryCorrupt");
        return value;
    }

    private Task<DateTimeOffset> DbNowAsync(CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    private long RequireAllowed()
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw Refused("PreSendBarrierDisabled");
        return options.AllowedRepositoryId;
    }

    private static GitHubRemoteSandboxPreSendBarrierException Refused(
        string code) => new(code);
}
