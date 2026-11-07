using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-077. All fields necessary to rehash historical F-051 evidence are
// stored individually, with no token, raw ref, private key or secret.
// PostgreSQL UPDATE/DELETE triggers make both tables append-only.
public sealed class GitHubRemoteAppPreflightEvidenceRecord
{
    public Guid RequestId { get; set; }
    public long Generation { get; set; }
    public long RepositoryId { get; set; }
    public string PacketSha256 { get; set; } = "";
    public string CandidateSetSha256 { get; set; } = "";
    public string OriginalPolicySha256 { get; set; } = "";
    public string CurrentPolicySha256 { get; set; } = "";
    public string ExpectedBaselineSha256 { get; set; } = "";
    public string ObservedBaselineSha256 { get; set; } = "";
    public int MatchingBaselineRefs { get; set; }
    public int AbsentCreateCandidates { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset PolicyObservedAtUtc { get; set; }
    public DateTimeOffset TokenExpiresAtUtc { get; set; }
    public string EvidenceSha256 { get; set; } = "";
    public string State { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
    public bool RemoteWritesPerformed { get; set; }
    public bool ExecutionAuthorized { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class GitHubRemoteAppPreflightEventRecord
{
    public long Id { get; set; }
    public Guid RequestId { get; set; }
    public long Generation { get; set; }
    public long RepositoryId { get; set; }
    public string Type { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class PostgresGitHubRemoteAppPreflightEvidenceJournal(
    AppDbContext db, GitHubRemoteSandboxFenceOptions options)
    : IGitHubRemoteAppPreflightEvidenceJournal
{
    public async Task<GitHubRemoteAppPreflightReceipt> RecordAsync(
        GitHubRemoteAppPreflightReceipt receipt,
        DateTimeOffset policyObservedAtUtc, CancellationToken ct)
    {
        if (receipt is null) throw Refused("RemotePreflightJournalInvalid");
        var digest = GitHubRemoteAppPreflightIntegrity
            .RecomputeHistoricalEvidenceSha256(receipt, policyObservedAtUtc);
        if (digest != receipt.EvidenceSha256)
            throw Refused("RemotePreflightJournalIntegrityMismatch");
        RequireAllowed(receipt.RepositoryId);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({receipt.RepositoryId.ToString()}))", ct);

        var existing = await db.GitHubRemoteAppPreflightEvidence
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == receipt.RequestId &&
                x.Generation == receipt.Generation, ct);
        if (existing is not null)
        {
            // Never amend a historical attestation. Replays must match
            // every supplied field AND the independently recorded clock.
            if (existing.RepositoryId != receipt.RepositoryId ||
                existing.PolicyObservedAtUtc != policyObservedAtUtc ||
                FromRow(existing) != receipt)
                throw Refused("RemotePreflightJournalEpochConflict");
            await RequireAuditAsync(existing, ct);
            await tx.CommitAsync(ct);
            return receipt;
        }

        var fence = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == receipt.RepositoryId, ct);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (fence is null ||
            fence.RequestId != receipt.RequestId ||
            fence.Generation != receipt.Generation ||
            fence.State != GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled ||
            fence.DeadlineUtc <= now ||
            fence.PacketSha256 != receipt.PacketSha256 ||
            fence.CandidateSetSha256 != receipt.CandidateSetSha256 ||
            fence.PolicySha256 != receipt.OriginalPolicySha256 ||
            fence.ReviewedBy is null ||
            fence.ReviewedBy == fence.RequestedBy ||
            fence.ExecutionAuthorized || fence.RemoteWritesPerformed ||
            fence.ReadyForExecution ||
            receipt.ObservedAtUtc > now.AddSeconds(5) ||
            receipt.ObservedAtUtc < now.AddSeconds(-25) ||
            policyObservedAtUtc > now.AddSeconds(5) ||
            policyObservedAtUtc < now.AddSeconds(-25) ||
            receipt.InstallationTokenExpiresAtUtc <= now.AddMinutes(2))
            throw Refused("RemotePreflightJournalFenceChanged");

        var row = new GitHubRemoteAppPreflightEvidenceRecord
        {
            RequestId = receipt.RequestId,
            Generation = receipt.Generation,
            RepositoryId = receipt.RepositoryId,
            PacketSha256 = receipt.PacketSha256,
            CandidateSetSha256 = receipt.CandidateSetSha256,
            OriginalPolicySha256 = receipt.OriginalPolicySha256,
            CurrentPolicySha256 = receipt.CurrentPolicySha256,
            ExpectedBaselineSha256 = receipt.ExpectedBaselineSha256,
            ObservedBaselineSha256 = receipt.ObservedBaselineSha256,
            MatchingBaselineRefs = receipt.MatchingBaselineRefs,
            AbsentCreateCandidates = receipt.AbsentCreateCandidates,
            ObservedAtUtc = receipt.ObservedAtUtc,
            PolicyObservedAtUtc = policyObservedAtUtc,
            TokenExpiresAtUtc = receipt.InstallationTokenExpiresAtUtc,
            EvidenceSha256 = receipt.EvidenceSha256,
            State = receipt.State,
            RecordedAtUtc = now,
            RemoteWritesPerformed = false,
            ExecutionAuthorized = false,
            ReadyForExecution = false
        };
        db.GitHubRemoteAppPreflightEvidence.Add(row);
        db.GitHubRemoteAppPreflightEvents.Add(new()
        {
            RequestId = receipt.RequestId,
            Generation = receipt.Generation,
            RepositoryId = receipt.RepositoryId,
            Type = "Recorded",
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return receipt;
    }

    public async Task<GitHubRemoteAppPreflightReceipt?> GetAsync(
        Guid fenceRequestId, long generation, long repositoryId,
        CancellationToken ct)
    {
        RequireAllowed(repositoryId);
        if (fenceRequestId == Guid.Empty || generation <= 0)
            throw Refused("RemotePreflightJournalInvalid");

        var row = await db.GitHubRemoteAppPreflightEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == fenceRequestId &&
                x.Generation == generation &&
                x.RepositoryId == repositoryId, ct);
        if (row is null) return null;
        await RequireAuditAsync(row, ct);
        return FromRow(row);
    }

    private async Task RequireAuditAsync(
        GitHubRemoteAppPreflightEvidenceRecord row, CancellationToken ct)
    {
        var receipt = FromRow(row);
        if (row.EvidenceSha256 != GitHubRemoteAppPreflightIntegrity
                .RecomputeHistoricalEvidenceSha256(
                    receipt, row.PolicyObservedAtUtc) ||
            row.RemoteWritesPerformed || row.ExecutionAuthorized ||
            row.ReadyForExecution ||
            row.RecordedAtUtc == default)
            throw Refused("RemotePreflightJournalCorrupt");

        var events = await db.GitHubRemoteAppPreflightEvents.AsNoTracking()
            .Where(x => x.RequestId == row.RequestId &&
                x.Generation == row.Generation)
            .OrderBy(x => x.Id).Take(2).ToArrayAsync(ct);
        if (events.Length != 1 || events[0].Type != "Recorded" ||
            events[0].RepositoryId != row.RepositoryId ||
            events[0].OccurredAtUtc != row.RecordedAtUtc)
            throw Refused("RemotePreflightJournalCorrupt");
    }

    private static GitHubRemoteAppPreflightReceipt FromRow(
        GitHubRemoteAppPreflightEvidenceRecord x) =>
        new(x.RequestId, x.Generation, x.RepositoryId,
            x.PacketSha256, x.CandidateSetSha256,
            x.OriginalPolicySha256, x.CurrentPolicySha256,
            x.ExpectedBaselineSha256, x.ObservedBaselineSha256,
            x.MatchingBaselineRefs, x.AbsentCreateCandidates,
            x.ObservedAtUtc, x.TokenExpiresAtUtc,
            x.EvidenceSha256, x.State, x.RemoteWritesPerformed,
            x.ExecutionAuthorized, x.ReadyForExecution);

    private void RequireAllowed(long repositoryId)
    {
        if (!options.Enabled || repositoryId <= 0 ||
            repositoryId != options.AllowedRepositoryId ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw Refused("RemotePreflightJournalDisabled");
    }

    private static GitHubRemoteAppPreflightException Refused(string code) =>
        new(code);
}
