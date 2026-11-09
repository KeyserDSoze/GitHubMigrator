using System.Data;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-097b: internal, off-by-default, source-only immutable proof ledger.
// Ref/OID inputs are loaded ONLY from the completed F-014 manifest selected
// by an independently verified F-091 checkpoint. Never from a request body.
public sealed record GitHubSourceCheckpointGitRefLedgerOptions(bool Enabled);

public sealed class GitHubSourceCheckpointGitRefLedgerException(string code)
    : Exception("Durable source ref/OID proof refused")
{
    public string Code { get; } = code;
}

public sealed class PostgresGitHubSourceCheckpointGitRefLedger(
    AppDbContext db, TimeProvider clock,
    GitHubSourceCheckpointGitRefLedgerOptions options,
    IIncrementalOperationPlanRepository plans,
    IGitHubSourceSyncCheckpointJournal checkpoints,
    PostgresVerifiedSourceGitRefReader sourceRefs)
{
    private const int MaxChainDepth = 64;

    private static GitHubSourceCheckpointGitRefLedgerException Refused(
        string code) => new(code);

    private void RequireEnabled()
    {
        if (!options.Enabled)
            throw Refused("SourceGitRefLedgerDisabled");
    }

    private static void Validate(Guid preparationId, Guid planId,
        Guid checkpointId, string repo)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || repo is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(repo))
            throw Refused("SourceGitRefLedgerInvalidScope");
    }

    public async Task<GitHubSourceCheckpointGitRefProof> CaptureAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        string sourceRepositoryId, Guid actorId, CancellationToken ct)
    {
        RequireEnabled();
        Validate(preparationId, planId, checkpointId, sourceRepositoryId);
        if (actorId == Guid.Empty)
            throw Refused("SourceGitRefLedgerInvalidScope");

        var initial = await RequirePlanAsync(preparationId, planId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        // Same source-first lock order as staging and the F-090 journal.
        // Cross-replica retries serialize on the source before proving
        // epoch and predecessor, then the unique DB key enforces identity.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({initial.SourceConnectionId.ToString()}), 732911)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({planId.ToString()}), 732990)", ct);
        var plan = await RequirePlanAsync(preparationId, planId, ct);
        if (plan.SourceConnectionId != initial.SourceConnectionId ||
            plan.ManifestSha256 != initial.ManifestSha256)
            throw Refused("SourceGitRefLedgerPlanChanged");

        var proof = await ComposeAsync(plan, checkpointId, sourceRepositoryId, 0, ct);
        var existing = await db.GitHubSourceCheckpointGitRefLedgers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        if (existing is not null)
        {
            if (existing.RecordedBy != actorId)
                throw Refused("SourceGitRefLedgerReplayConflict");
            var verified = await VerifyStoredAsync(existing, proof, ct);
            await tx.CommitAsync(ct);
            return verified;
        }

        var at = DateTimeOffset.FromUnixTimeMilliseconds(
            clock.GetUtcNow().ToUnixTimeMilliseconds());
        var record = new GitHubSourceCheckpointGitRefLedgerRecord
        {
            Id = Guid.NewGuid(), CheckpointId = checkpointId,
            PreparationId = plan.PreparationId, PlanId = plan.Id,
            SnapshotId = proof.SnapshotId,
            SourceConnectionId = plan.SourceConnectionId,
            SourceRepositoryId = sourceRepositoryId,
            SourceManifestRootSha256 = proof.SourceManifestRootSha256,
            ProofJson = JsonSerializer.Serialize(proof),
            EvidenceSha256 = proof.EvidenceSha256,
            RecordedAtUtc = at, RecordedBy = actorId,
            CanPush = false, CanRetry = false,
            CanDispatch = false, MigrationComplete = false
        };
        db.GitHubSourceCheckpointGitRefLedgers.Add(record);
        await db.SaveChangesAsync(ct);
        db.GitHubSourceCheckpointGitRefLedgerEvents.Add(new()
        {
            LedgerId = record.Id, Type = "Recorded",
            ActorId = actorId, EvidenceSha256 = record.EvidenceSha256,
            OccurredAtUtc = at
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return proof;
    }

    public async Task<GitHubSourceCheckpointGitRefProof?> GetVerifiedAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        string sourceRepositoryId, CancellationToken ct)
    {
        RequireEnabled();
        Validate(preparationId, planId, checkpointId, sourceRepositoryId);
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, ct);
        var plan = await RequirePlanAsync(preparationId, planId, ct);
        var row = await db.GitHubSourceCheckpointGitRefLedgers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId &&
                x.PreparationId == preparationId && x.PlanId == planId, ct);
        if (row is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }
        var expected = await ComposeAsync(plan, checkpointId, sourceRepositoryId, 0, ct);
        var result = await VerifyStoredAsync(row, expected, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<IncrementalOperationPlan> RequirePlanAsync(
        Guid preparationId, Guid planId, CancellationToken ct) =>
        await plans.GetAsync(preparationId, planId, ct)
        ?? throw Refused("SourceGitRefLedgerPlanNotFound");

    private async Task<GitHubSourceCheckpointGitRefProof> ComposeAsync(
        IncrementalOperationPlan plan, Guid checkpointId,
        string repo, int depth, CancellationToken ct)
    {
        if (depth >= MaxChainDepth)
            throw Refused("SourceGitRefLedgerChainTooLong");
        if (!plan.Steps.Any(x => x.Kind == "ReviewTargetName" &&
            x.SourceRepositoryId == repo))
            throw Refused("SourceGitRefLedgerRepositoryNotReviewed");

        var checkpoint = await checkpoints.GetVerifiedAsync(
            plan.PreparationId, plan.Id, checkpointId, ct)
            ?? throw Refused("SourceGitRefLedgerCheckpointNotFound");
        if (checkpoint.Proof.SourceConnectionId != plan.SourceConnectionId ||
            checkpoint.Proof.PlanId != plan.Id ||
            checkpoint.Proof.PreparationId != plan.PreparationId ||
            checkpoint.Proof.ManifestSha256 != plan.ManifestSha256 ||
            checkpoint.Proof.BaselineSnapshotId != plan.SnapshotId ||
            checkpoint.EvidenceVersion != 2)
            throw Refused("SourceGitRefLedgerCheckpointUnavailable");

        GitHubSourceCheckpointGitRefProof? prior = null;
        if (checkpoint.Proof.PreviousCheckpointId is Guid previousId)
        {
            var row = await db.GitHubSourceCheckpointGitRefLedgers
                .AsNoTracking().SingleOrDefaultAsync(x =>
                    x.CheckpointId == previousId &&
                    x.SourceRepositoryId == repo &&
                    x.PlanId == plan.Id, ct)
                ?? throw Refused("SourceGitRefLedgerPredecessorMissing");
            var previous = await ComposeAsync(plan, previousId, repo, depth + 1, ct);
            prior = await VerifyStoredAsync(row, previous, ct);
        }

        try
        {
            var source = await sourceRefs.ReadSealedSnapshotAsync(
                plan, checkpoint.Proof.SnapshotId, repo, ct);
            return GitHubSourceCheckpointGitRefProofIntegrity.Seal(
                checkpoint, source, prior);
        }
        catch (Exception ex) when (ex is
            IncrementalGitRefReconciliationException or
            DiscoveryRefManifestException or
            StagingSyncInputException or
            GitHubSourceCheckpointGitRefProofException)
        {
            throw Refused("SourceGitRefLedgerSourceEvidenceInvalid");
        }
    }

    private async Task<GitHubSourceCheckpointGitRefProof> VerifyStoredAsync(
        GitHubSourceCheckpointGitRefLedgerRecord row,
        GitHubSourceCheckpointGitRefProof expected, CancellationToken ct)
    {
        GitHubSourceCheckpointGitRefProof proof;
        try
        {
            proof = JsonSerializer.Deserialize<GitHubSourceCheckpointGitRefProof>(
                row.ProofJson) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw Refused("SourceGitRefLedgerHistoryCorrupt");
        }

        if (row.Id == Guid.Empty ||
            row.CheckpointId != expected.CheckpointId ||
            row.PlanId != expected.PlanId ||
            row.PreparationId != expected.PreparationId ||
            row.SnapshotId != expected.SnapshotId ||
            row.SourceConnectionId != expected.SourceConnectionId ||
            row.SourceRepositoryId != expected.SourceRepositoryId ||
            row.SourceManifestRootSha256 != expected.SourceManifestRootSha256 ||
            row.EvidenceSha256 != expected.EvidenceSha256 ||
            row.CanPush || row.CanRetry || row.CanDispatch ||
            row.MigrationComplete || row.RecordedBy == Guid.Empty ||
            row.RecordedAtUtc == default ||
            !proof.Refs.SequenceEqual(expected.Refs) ||
            proof with { Refs = expected.Refs } != expected)
            throw Refused("SourceGitRefLedgerHistoryCorrupt");

        var events = await db.GitHubSourceCheckpointGitRefLedgerEvents.AsNoTracking()
            .Where(x => x.LedgerId == row.Id).Take(2).ToArrayAsync(ct);
        if (events.Length != 1 || events[0].Type != "Recorded" ||
            events[0].ActorId != row.RecordedBy ||
            events[0].OccurredAtUtc != row.RecordedAtUtc ||
            events[0].EvidenceSha256 != row.EvidenceSha256)
            throw Refused("SourceGitRefLedgerHistoryCorrupt");
        return proof;
    }
}
