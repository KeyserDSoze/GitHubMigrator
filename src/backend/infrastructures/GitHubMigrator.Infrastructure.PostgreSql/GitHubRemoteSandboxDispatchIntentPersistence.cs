using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitHubRemoteSandboxDispatchIntentRecord
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid HazardId { get; set; }
    public Guid GrantId { get; set; }
    public long RepositoryId { get; set; }
    public Guid RequestedBy { get; set; }
    public string SessionEvidenceSha256 { get; set; } = "";
    public string DispatchReviewSha256 { get; set; } = "";
    public string PacketSha256 { get; set; } = "";
    public string CandidateSetSha256 { get; set; } = "";
    public string LiveRefEvidenceSha256 { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string EvidenceSha256 { get; set; } = "";
    public string State { get; set; } = "";
    public bool CanExecute { get; set; }
    public bool TransportInvoked { get; set; }
    public bool MayAutomaticallyRetry { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitHubRemoteSandboxDispatchIntentEventRecord
{
    public long Id { get; set; }
    public Guid IntentId { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

// Never a queue: no claim, lease, dequeue, sender or retry operation
// exists. A single immutable, non-executable handoff per F-078 session.
public sealed class PostgresGitHubRemoteSandboxDispatchIntentJournal(
    AppDbContext db, GitHubRemoteTransmitHazardOptions options,
    PostgresGitHubRemoteSandboxReviewSessionJournal sessions)
{
    public async Task<GitHubRemoteSandboxDispatchIntentReceipt> RecordAsync(
        Guid sessionId, Guid preparationId, Guid planId, Guid scopeId,
        Guid actorId, CancellationToken ct)
    {
        var repositoryId = RequireAllowed();
        if (sessionId == Guid.Empty || preparationId == Guid.Empty ||
            planId == Guid.Empty || scopeId == Guid.Empty ||
            actorId == Guid.Empty)
            throw Refused("SandboxDispatchIntentInputInvalid");

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        // Exactly the F-045 and F-050/F-052/F-055/F-078 lock order.
        // Recheck authority AFTER the locks, with the DB clock.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(732911, hashtext({repositoryId.ToString()}))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({repositoryId.ToString()}))", ct);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

        var inspected = await sessions.InspectScopedAsync(
            sessionId, preparationId, planId, scopeId, repositoryId, ct);
        if (inspected is null)
            throw Refused("SandboxDispatchIntentSessionNotFound");
        var session = inspected.Session;
        if (inspected.Status != GitHubRemoteSandboxReviewSessionIntegrity.Current ||
            inspected.HistoricalOnly || session.ExpiresAtUtc <= now ||
            session.RecordedAtUtc > now ||
            session.ObservedBy == actorId ||
            session.TransportInvoked || session.RemoteWritesAuthorized ||
            session.MayAutomaticallyRetry || session.MigrationComplete)
            throw Refused("SandboxDispatchIntentSessionStale");

        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == session.HazardId &&
                x.RepositoryId == repositoryId, ct);
        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == session.GrantId &&
                x.RepositoryId == repositoryId, ct);
        if (hazard is null || grant is null ||
            hazard.State != GitHubRemoteTransmitHazardStates.PreparedWriteDisabled ||
            hazard.MarkedBy is not null || hazard.DeadlineUtc <= now ||
            hazard.TransportAuthorized || hazard.TransportInvoked ||
            hazard.RemoteWritesPerformed || hazard.MayAutomaticallyRetry ||
            hazard.GrantId != session.GrantId ||
            hazard.PacketSha256 != session.PacketSha256 ||
            hazard.CandidateSetSha256 != session.CandidateSetSha256 ||
            hazard.BaselineSha256 != session.BaselineSha256 ||
            grant.State != GitHubRemoteTestGrantStates.ReviewedWriteDisabled ||
            grant.DeadlineUtc <= now ||
            grant.PreparationId != preparationId || grant.PlanId != planId ||
            grant.ScopeId != scopeId ||
            grant.PacketSha256 != session.PacketSha256 ||
            grant.CandidateSetSha256 != session.CandidateSetSha256 ||
            grant.PreflightSha256 != session.PreflightSha256 ||
            grant.BaselineSha256 != session.BaselineSha256 ||
            grant.ExecutionAuthorized || grant.RemoteWritesPerformed ||
            grant.ReadyForExecution ||
            actorId == grant.RequestedBy || actorId == grant.ReviewedBy ||
            actorId == hazard.PreparedBy)
            throw Refused("SandboxDispatchIntentAuthorityChanged");

        var previous = await db.GitHubRemoteSandboxDispatchIntents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id ||
                x.HazardId == session.HazardId, ct);
        if (previous is not null)
        {
            var original = await VerifyAuditAsync(previous, ct);
            if (original.SessionId != session.Id ||
                original.RequestedBy != actorId ||
                original.SessionEvidenceSha256 != session.EvidenceSha256 ||
                original.DispatchReviewSha256 != session.DispatchReviewSha256 ||
                original.PacketSha256 != session.PacketSha256 ||
                original.CandidateSetSha256 != session.CandidateSetSha256 ||
                original.LiveRefEvidenceSha256 !=
                    session.LiveRefEvidenceSha256)
                throw Refused("SandboxDispatchIntentConflict");
            await tx.CommitAsync(ct);
            return original;
        }

        var expiry = new[] {
            session.ExpiresAtUtc, hazard.DeadlineUtc, grant.DeadlineUtc
        }.Min();
        if (expiry <= now)
            throw Refused("SandboxDispatchIntentSessionStale");

        var value = new GitHubRemoteSandboxDispatchIntentReceipt(
            Guid.NewGuid(), session.Id, session.HazardId, session.GrantId,
            repositoryId, actorId, session.EvidenceSha256,
            session.DispatchReviewSha256, session.PacketSha256,
            session.CandidateSetSha256, session.LiveRefEvidenceSha256,
            now, expiry, "", GitHubRemoteSandboxDispatchIntentIntegrity.Recorded,
            false, false, false, false);
        value = value with {
            EvidenceSha256 = GitHubRemoteSandboxDispatchIntentIntegrity.Digest(value)
        };
        db.GitHubRemoteSandboxDispatchIntents.Add(new()
        {
            Id = value.Id, SessionId = value.SessionId,
            HazardId = value.HazardId, GrantId = value.GrantId,
            RepositoryId = value.RepositoryId,
            RequestedBy = value.RequestedBy,
            SessionEvidenceSha256 = value.SessionEvidenceSha256,
            DispatchReviewSha256 = value.DispatchReviewSha256,
            PacketSha256 = value.PacketSha256,
            CandidateSetSha256 = value.CandidateSetSha256,
            LiveRefEvidenceSha256 = value.LiveRefEvidenceSha256,
            RecordedAtUtc = value.RecordedAtUtc,
            ExpiresAtUtc = value.ExpiresAtUtc,
            EvidenceSha256 = value.EvidenceSha256, State = value.State,
            CanExecute = false, TransportInvoked = false,
            MayAutomaticallyRetry = false, MigrationComplete = false
        });
        db.GitHubRemoteSandboxDispatchIntentEvents.Add(new()
        {
            IntentId = value.Id, Type = "Recorded",
            ActorId = actorId, OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return value;
    }

    public async Task<GitHubRemoteSandboxDispatchIntentInspection?>
        InspectScopedAsync(Guid intentId, Guid preparationId, Guid planId,
            Guid scopeId, CancellationToken ct)
    {
        var repositoryId = RequireAllowed();
        if (intentId == Guid.Empty || preparationId == Guid.Empty ||
            planId == Guid.Empty || scopeId == Guid.Empty)
            throw Refused("SandboxDispatchIntentInputInvalid");
        var stored = await db.GitHubRemoteSandboxDispatchIntents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == intentId &&
                x.RepositoryId == repositoryId &&
                db.GitHubRemoteTestGrants.Any(g =>
                    g.Id == x.GrantId && g.RepositoryId == repositoryId &&
                    g.PreparationId == preparationId &&
                    g.PlanId == planId && g.ScopeId == scopeId), ct);
        if (stored is null) return null;
        var intent = await VerifyAuditAsync(stored, ct);
        var inspected = await sessions.InspectScopedAsync(
            intent.SessionId, preparationId, planId, scopeId,
            repositoryId, ct);
        if (inspected is null ||
            inspected.Session.Id != intent.SessionId ||
            inspected.Session.HazardId != intent.HazardId ||
            inspected.Session.GrantId != intent.GrantId ||
            inspected.Session.EvidenceSha256 != intent.SessionEvidenceSha256 ||
            inspected.Session.DispatchReviewSha256 != intent.DispatchReviewSha256 ||
            inspected.Session.PacketSha256 != intent.PacketSha256 ||
            inspected.Session.CandidateSetSha256 != intent.CandidateSetSha256 ||
            inspected.Session.LiveRefEvidenceSha256 !=
                intent.LiveRefEvidenceSha256)
            throw Refused("SandboxDispatchIntentHistoryCorrupt");
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var current = inspected.Status ==
            GitHubRemoteSandboxReviewSessionIntegrity.Current &&
            intent.ExpiresAtUtc > now;
        return new(intent,
            current ? GitHubRemoteSandboxDispatchIntentIntegrity.Current :
                GitHubRemoteSandboxDispatchIntentIntegrity.Historical,
            !current, false, false, false);
    }

    private async Task<GitHubRemoteSandboxDispatchIntentReceipt> VerifyAuditAsync(
        GitHubRemoteSandboxDispatchIntentRecord s, CancellationToken ct)
    {
        var value = new GitHubRemoteSandboxDispatchIntentReceipt(
            s.Id, s.SessionId, s.HazardId, s.GrantId,
            s.RepositoryId, s.RequestedBy, s.SessionEvidenceSha256,
            s.DispatchReviewSha256, s.PacketSha256,
            s.CandidateSetSha256, s.LiveRefEvidenceSha256,
            s.RecordedAtUtc, s.ExpiresAtUtc,
            s.EvidenceSha256, s.State, s.CanExecute,
            s.TransportInvoked, s.MayAutomaticallyRetry,
            s.MigrationComplete);
        try
        {
            GitHubRemoteSandboxDispatchIntentIntegrity.Verify(value);
        }
        catch (GitHubRemoteSandboxDispatchIntentException)
        {
            throw Refused("SandboxDispatchIntentHistoryCorrupt");
        }
        var audit = await db.GitHubRemoteSandboxDispatchIntentEvents.AsNoTracking()
            .Where(x => x.IntentId == value.Id).Take(2).ToArrayAsync(ct);
        if (audit.Length != 1 || audit[0].Type != "Recorded" ||
            audit[0].ActorId != value.RequestedBy ||
            audit[0].OccurredAtUtc != value.RecordedAtUtc)
            throw Refused("SandboxDispatchIntentHistoryCorrupt");
        return value;
    }

    private long RequireAllowed()
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw Refused("SandboxDispatchIntentDisabled");
        return options.AllowedRepositoryId;
    }

    private static GitHubRemoteSandboxDispatchIntentException Refused(
        string code) => new(code);
}
