using System.Data;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitHubRemoteTypedRecoveryBaselineRecord
{
    public Guid Id { get; set; }
    public Guid ConfirmationId { get; set; }
    public Guid IntentId { get; set; }
    public Guid HazardId { get; set; }
    public Guid GrantId { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid ScopeId { get; set; }
    public long RepositoryId { get; set; }
    public string Organization { get; set; } = "";
    public string Repository { get; set; } = "";
    public Guid CapturedBy { get; set; }
    public string PacketSha256 { get; set; } = "";
    public string CandidateSetSha256 { get; set; } = "";
    public string BaselineSha256 { get; set; } = "";
    public DateTimeOffset BaselineObservedAtUtc { get; set; }
    public string BaselineEvidenceSha256 { get; set; } = "";
    public string BaselineRefsJson { get; set; } = "";
    public string CreateCandidatesJson { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string State { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public bool CanSend { get; set; }
    public bool MayAutomaticallyRetry { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitHubRemoteTypedRecoveryBaselineEventRecord
{
    public long Id { get; set; }
    public Guid SnapshotId { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

// F-086 is the ONLY persisted source of typed pre-send ref names,
// annotated-tag object types and create-only OIDs. Stored BEFORE F-082,
// but becomes usable for independent recovery only after F-082 is
// verified as irrevocably committed by the same captured operator.
// No HTTP mutation path, provider API or Git sender is injected.
public sealed class PostgresGitHubRemoteTypedRecoveryBaselineJournal(
    AppDbContext db, GitHubRemoteTypedRecoveryBaselineOptions options,
    PostgresGitHubRemoteSandboxDispatchConfirmationJournal confirmations)
    : IGitHubRemoteTypedRecoveryBaselineJournal
{
    public async Task<GitHubRemoteTypedRecoveryBaselineReceipt> CaptureAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, Guid independentOperator,
        GitHubCreateOnlyCandidatePacket source,
        GitHubRefObservation latestBaseline, CancellationToken ct)
    {
        var repo = RequireAllowed();
        if (confirmationId == Guid.Empty || intentId == Guid.Empty ||
            preparationId == Guid.Empty || planId == Guid.Empty ||
            scopeId == Guid.Empty || independentOperator == Guid.Empty ||
            source is null || source.Packet is null ||
            source.Packet.RepositoryId != repo ||
            source.Packet.PreparationId != preparationId ||
            source.Packet.PlanId != planId || source.Packet.ScopeId != scopeId ||
            !string.Equals(options.AllowedRepository,
                source.Packet.Organization + "/" + source.Packet.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            source.Packet.State != GitHubCreateOnlyPacketIntegrity.State ||
            source.Packet.ExecutionAuthorized || source.Packet.WriteCapabilityVerified ||
            source.Packet.RemoteWritesPerformed || source.Packet.ReadyForExecution ||
            source.Candidates is null ||
            source.Candidates.Count != source.Packet.CreateCandidates ||
            source.Candidates.Count is < 1 or >
                GitHubCandidatePolicyIntegrity.MaxOnlineCandidates)
            throw Refused("TypedRecoveryBaselineInputInvalid");
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(732911, hashtext({repo.ToString()}))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({repo.ToString()}))", ct);
        var now = await DbNowAsync(ct);
        var history = await confirmations.InspectScopedAsync(
            confirmationId, intentId, preparationId, planId, scopeId, ct);
        if (history is null || history.Status !=
                GitHubRemoteSandboxDispatchConfirmationIntegrity.Current ||
            history.Confirmation.ExpiresAtUtc <= now)
            throw Refused("TypedRecoveryBaselineConfirmationStale");
        var confirmation = history.Confirmation;
        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == confirmation.HazardId &&
                x.GrantId == confirmation.GrantId &&
                x.RepositoryId == repo, ct);
        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == confirmation.GrantId &&
                x.RepositoryId == repo, ct);
        if (hazard is null || grant is null ||
            hazard.State != GitHubRemoteTransmitHazardStates.PreparedWriteDisabled ||
            hazard.MarkedBy != null || hazard.DeadlineUtc <= now ||
            hazard.TransportAuthorized || hazard.TransportInvoked ||
            hazard.RemoteWritesPerformed || hazard.MayAutomaticallyRetry ||
            grant.State != GitHubRemoteTestGrantStates.ReviewedWriteDisabled ||
            grant.DeadlineUtc <= now ||
            grant.PreparationId != preparationId || grant.PlanId != planId ||
            grant.ScopeId != scopeId ||
            confirmation.PacketSha256 != source.Packet.EvidenceSha256 ||
            grant.PacketSha256 != source.Packet.EvidenceSha256 ||
            hazard.PacketSha256 != source.Packet.EvidenceSha256 ||
            confirmation.CandidateSetSha256 != source.Packet.CandidateSetSha256 ||
            hazard.CandidateSetSha256 != source.Packet.CandidateSetSha256 ||
            grant.CandidateSetSha256 != source.Packet.CandidateSetSha256 ||
            grant.BaselineSha256 != hazard.BaselineSha256 ||
            independentOperator == confirmation.RequestedBy ||
            independentOperator == confirmation.ConfirmedBy ||
            independentOperator == hazard.PreparedBy ||
            independentOperator == grant.RequestedBy ||
            independentOperator == grant.ReviewedBy)
            throw Refused("TypedRecoveryBaselineAuthorityChanged");

        var value = new GitHubRemoteTypedRecoveryBaselineReceipt(
            Guid.NewGuid(), confirmation.Id, intentId,
            confirmation.HazardId, confirmation.GrantId,
            preparationId, planId, scopeId, repo,
            source.Packet.Organization, source.Packet.Repository,
            independentOperator, source.Packet.EvidenceSha256,
            source.Packet.CandidateSetSha256, grant.BaselineSha256,
            latestBaseline, source.Candidates, now,
            GitHubRemoteTypedRecoveryBaselineIntegrity.Captured, "",
            false, false, false);
        value = value with {
            EvidenceSha256 =
                GitHubRemoteTypedRecoveryBaselineIntegrity.Digest(value)
        };
        if (await db.GitHubRemoteTypedRecoveryBaselines.AsNoTracking()
            .AnyAsync(x => x.ConfirmationId == confirmationId ||
                x.HazardId == confirmation.HazardId ||
                x.GrantId == confirmation.GrantId, ct))
            throw Refused("TypedRecoveryBaselineAlreadyCaptured");
        var baselineJson = JsonSerializer.Serialize(value.Baseline.Refs);
        var candidatesJson = JsonSerializer.Serialize(value.CreateCandidates);
        if (baselineJson.Length > 262_144 || candidatesJson.Length > 65_536)
            throw Refused("TypedRecoveryBaselineTooLarge");

        db.GitHubRemoteTypedRecoveryBaselines.Add(new()
        {
            Id = value.Id, ConfirmationId = value.ConfirmationId,
            IntentId = value.IntentId, HazardId = value.HazardId,
            GrantId = value.GrantId, PreparationId = preparationId,
            PlanId = planId, ScopeId = scopeId, RepositoryId = repo,
            Organization = value.Organization, Repository = value.Repository,
            CapturedBy = value.CapturedBy, PacketSha256 = value.PacketSha256,
            CandidateSetSha256 = value.CandidateSetSha256,
            BaselineSha256 = value.BaselineSha256,
            BaselineObservedAtUtc = value.Baseline.ObservedAtUtc,
            BaselineEvidenceSha256 = value.Baseline.EvidenceSha256,
            BaselineRefsJson = baselineJson,
            CreateCandidatesJson = candidatesJson,
            RecordedAtUtc = now, State = value.State,
            EvidenceSha256 = value.EvidenceSha256,
            CanSend = false, MayAutomaticallyRetry = false,
            MigrationComplete = false
        });
        db.GitHubRemoteTypedRecoveryBaselineEvents.Add(new()
        {
            SnapshotId = value.Id, Type = "Recorded",
            ActorId = independentOperator, OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return value;
    }

    public async Task<GitHubRemoteTypedRecoveryBaselineReceipt?> ReadAfterBarrierAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, CancellationToken ct)
    {
        var repo = RequireAllowed();
        if (confirmationId == Guid.Empty || intentId == Guid.Empty ||
            preparationId == Guid.Empty || planId == Guid.Empty ||
            scopeId == Guid.Empty)
            throw Refused("TypedRecoveryBaselineInputInvalid");
        var row = await db.GitHubRemoteTypedRecoveryBaselines.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConfirmationId == confirmationId &&
                x.IntentId == intentId && x.PreparationId == preparationId &&
                x.PlanId == planId && x.ScopeId == scopeId &&
                x.RepositoryId == repo, ct);
        if (row is null) return null;
        var barrier = await db.GitHubRemoteSandboxPreSendBarriers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConfirmationId == confirmationId &&
                x.HazardId == row.HazardId && x.IntentId == intentId &&
                x.RepositoryId == repo, ct);
        if (barrier is null) return null;
        if (barrier.MarkedBy != row.CapturedBy ||
            barrier.RecordedAtUtc < row.RecordedAtUtc ||
            barrier.PacketSha256 != row.PacketSha256 ||
            barrier.CandidateSetSha256 != row.CandidateSetSha256 ||
            barrier.GrantId != row.GrantId ||
            barrier.State != GitHubRemoteSandboxPreSendBarrierIntegrity.Recorded ||
            barrier.CanExecute || barrier.TransportInvoked ||
            barrier.MayAutomaticallyRetry || barrier.MigrationComplete)
            throw Refused("TypedRecoveryBaselineBarrierMismatch");
        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == row.HazardId &&
                x.RepositoryId == repo, ct);
        if (hazard is null ||
            hazard.State != GitHubRemoteTransmitHazardStates.MayHaveTransmitted ||
            hazard.MarkedBy != row.CapturedBy ||
            hazard.UpdatedAtUtc != barrier.RecordedAtUtc ||
            hazard.BaselineSha256 != row.BaselineSha256 ||
            hazard.CandidateSetSha256 != row.CandidateSetSha256 ||
            hazard.PacketSha256 != row.PacketSha256)
            throw Refused("TypedRecoveryBaselineBarrierMismatch");

        var confirmation = await db.GitHubRemoteSandboxDispatchConfirmations
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == confirmationId && x.RepositoryId == repo, ct);
        if (confirmation is null ||
            confirmation.HazardId != row.HazardId ||
            confirmation.GrantId != row.GrantId ||
            confirmation.PacketSha256 != row.PacketSha256 ||
            confirmation.CandidateSetSha256 != row.CandidateSetSha256)
            throw Refused("TypedRecoveryBaselineHistoryCorrupt");

        GitHubObservedRef[] refs;
        GitMirrorRef[] candidates;
        try
        {
            refs = JsonSerializer.Deserialize<GitHubObservedRef[]>(
                row.BaselineRefsJson) ?? [];
            candidates = JsonSerializer.Deserialize<GitMirrorRef[]>(
                row.CreateCandidatesJson) ?? [];
        }
        catch (JsonException)
        { throw Refused("TypedRecoveryBaselineHistoryCorrupt"); }
        var typed = new GitHubRefObservation(
            row.Organization, row.Repository, row.BaselineObservedAtUtc,
            row.BaselineEvidenceSha256, refs);
        var value = new GitHubRemoteTypedRecoveryBaselineReceipt(
            row.Id, row.ConfirmationId, row.IntentId,
            row.HazardId, row.GrantId, row.PreparationId, row.PlanId,
            row.ScopeId, row.RepositoryId, row.Organization,
            row.Repository, row.CapturedBy, row.PacketSha256,
            row.CandidateSetSha256, row.BaselineSha256,
            typed, candidates, row.RecordedAtUtc,
            row.State, row.EvidenceSha256, row.CanSend,
            row.MayAutomaticallyRetry, row.MigrationComplete);
        try { GitHubRemoteTypedRecoveryBaselineIntegrity.Verify(value); }
        catch (GitHubRemoteTypedRecoveryBaselineException)
        { throw Refused("TypedRecoveryBaselineHistoryCorrupt"); }
        var audit = await db.GitHubRemoteTypedRecoveryBaselineEvents.AsNoTracking()
            .Where(x => x.SnapshotId == value.Id)
            .Take(2).ToArrayAsync(ct);
        if (audit.Length != 1 || audit[0].Type != "Recorded" ||
            audit[0].ActorId != value.CapturedBy ||
            audit[0].OccurredAtUtc != value.RecordedAtUtc)
            throw Refused("TypedRecoveryBaselineHistoryCorrupt");
        return value;
    }

    public async Task<GitHubRemoteTypedRecoveryBaselineStatus?> InspectScopedAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, CancellationToken ct)
    {
        var typed = await ReadAfterBarrierAsync(
            confirmationId, intentId, preparationId, planId, scopeId, ct);
        return typed is null ? null : new(
            typed.Id, typed.ConfirmationId, typed.IntentId, typed.HazardId,
            typed.GrantId, typed.RepositoryId, typed.CapturedBy,
            typed.PacketSha256, typed.CandidateSetSha256,
            typed.BaselineSha256, typed.Baseline.EvidenceSha256,
            typed.Baseline.Refs.Count, typed.CreateCandidates.Count,
            typed.RecordedAtUtc, true, false, false, false);
    }

    private Task<DateTimeOffset> DbNowAsync(CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    private long RequireAllowed()
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw Refused("TypedRecoveryBaselineDisabled");
        return options.AllowedRepositoryId;
    }

    private static GitHubRemoteTypedRecoveryBaselineException Refused(string code)
        => new(code);
}
