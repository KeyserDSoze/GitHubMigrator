using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitSourceBundleEvidenceRecord
{
    public Guid WorkpackId { get; set; }
    public Guid PlanId { get; set; }
    public Guid ReconciliationId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string SourceEvidenceSha256 { get; set; } = "";
    public string ReconciliationSha256 { get; set; } = "";
    public string BundleFileName { get; set; } = "";
    public string BundleSha256 { get; set; } = "";
    public long BundleBytes { get; set; }
    public int RefCount { get; set; }
    public DateTimeOffset AcquiredAtUtc { get; set; }
    public bool ReadyForExecution { get; set; }
}

// The bundle is a source-only local artifact. This table contains only
// SHA-256 evidence and the bounded, generated relative filename, never a
// user-provided local path or credential. The commit rechecks all proofs.
public sealed class PostgresGitSourceBundleEvidenceStore(
    AppDbContext db, TimeProvider clock,
    PostgresIncrementalGitRefReconciliationStore reconciliations,
    PostgresVerifiedSourceGitRefReader sourceReader,
    IGitTransferWorkpackRepository workpacks)
    : IGitSourceBundleEvidenceStore
{
    public async Task<GitSourceBundleView> SaveAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, GitSourceBundleArtifact artifact,
        CancellationToken ct)
    {
        if (artifact is null || artifact.WorkpackId != workpackId ||
            artifact.BundleFileName != workpackId.ToString("N") + ".bundle" ||
            !ValidSha(artifact.BundleSha256) ||
            !ValidSha(artifact.SourceEvidenceSha256) ||
            artifact.BundleBytes is < 1 or > 536_870_912 ||
            artifact.RefCount is < 1 or > GitMirrorRefReconciler.MaxRefsPerSide)
            throw new GitSourceBundleException("BundleProofInvalid");

        var parent = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == planId &&
                x.PreparationId == preparationId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({parent.SourceConnectionId.ToString()}), 732911)", ct);
        var plan = await new PostgresIncrementalOperationPlanRepository(db, clock)
            .GetAsync(preparationId, planId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        var pack = await workpacks.GetAsync(preparationId, planId, workpackId, ct)
            ?? throw new KeyNotFoundException("TransferWorkpackNotFound");
        if (!plan.IsCurrent || !pack.IsCurrent ||
            pack.State != GitTransferWorkpackStates.Blocked ||
            pack.Phase != GitTransferWorkpackPhases.WriteBlocked ||
            pack.ManifestSha256 != plan.ManifestSha256 ||
            pack.ReconciliationSha256 != expectedReconciliationSha256)
            throw new GitSourceBundleException("BundleWorkpackNotCurrent");

        var page = await reconciliations.PageAsync(
            preparationId, planId, pack.ReconciliationId, null, 1, ct)
            ?? throw new KeyNotFoundException("RefReconciliationNotFound");
        var evidence = page.Reconciliation;
        if (!evidence.IsCurrent ||
            evidence.ReconciliationSha256 != pack.ReconciliationSha256 ||
            evidence.SourceRepositoryId != pack.SourceRepositoryId ||
            evidence.TargetEvidenceSha256 != pack.TargetEvidenceSha256)
            throw new GitSourceBundleException("BundleReconciliationNotCurrent");

        var source = await sourceReader.ReadAsync(plan, pack.SourceRepositoryId, ct);
        if (source.SourceEvidenceSha256 != evidence.SourceEvidenceSha256 ||
            source.SourceEvidenceSha256 != artifact.SourceEvidenceSha256 ||
            source.Refs.Count != artifact.RefCount)
            throw new GitSourceBundleException("BundleSourceEvidenceMismatch");

        var entry = new GitSourceBundleEvidenceRecord {
            WorkpackId = workpackId,
            PlanId = planId,
            ReconciliationId = pack.ReconciliationId,
            SourceRepositoryId = pack.SourceRepositoryId,
            SourceEvidenceSha256 = artifact.SourceEvidenceSha256,
            ReconciliationSha256 = pack.ReconciliationSha256,
            BundleFileName = artifact.BundleFileName,
            BundleSha256 = artifact.BundleSha256,
            BundleBytes = artifact.BundleBytes,
            RefCount = artifact.RefCount,
            AcquiredAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                artifact.AcquiredAtUtc.ToUnixTimeMilliseconds()),
            ReadyForExecution = false
        };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_source_bundle_evidence (
                workpack_id, plan_id, reconciliation_id, source_repository_id,
                source_evidence_sha256, reconciliation_sha256, bundle_file_name,
                bundle_sha256, bundle_bytes, ref_count, acquired_at_utc, ready_for_execution)
            VALUES ({entry.WorkpackId}, {entry.PlanId}, {entry.ReconciliationId},
                {entry.SourceRepositoryId}, {entry.SourceEvidenceSha256},
                {entry.ReconciliationSha256}, {entry.BundleFileName},
                {entry.BundleSha256}, {entry.BundleBytes}, {entry.RefCount},
                {entry.AcquiredAtUtc}, false)
            ON CONFLICT (workpack_id) DO NOTHING
            """, ct);
        var saved = await db.GitSourceBundleEvidence.AsNoTracking()
            .SingleAsync(x => x.WorkpackId == workpackId, ct);
        if (saved.PlanId != entry.PlanId ||
            saved.ReconciliationId != entry.ReconciliationId ||
            saved.SourceRepositoryId != entry.SourceRepositoryId ||
            saved.ReconciliationSha256 != entry.ReconciliationSha256 ||
            saved.SourceEvidenceSha256 != entry.SourceEvidenceSha256 ||
            saved.BundleFileName != entry.BundleFileName ||
            saved.BundleSha256 != entry.BundleSha256 ||
            saved.BundleBytes != entry.BundleBytes ||
            saved.RefCount != entry.RefCount ||
            saved.ReadyForExecution)
            throw new GitSourceBundleException("BundleProofMismatch");
        await transaction.CommitAsync(ct);
        return await ViewAsync(saved, preparationId, ct);
    }

    public async Task<GitSourceBundleView?> GetAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct)
    {
        var record = await db.GitSourceBundleEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkpackId == workpackId &&
                x.PlanId == planId, ct);
        if (record is null) return null;
        var planExists = await db.IncrementalOperationPlans.AsNoTracking()
            .AnyAsync(x => x.Id == planId && x.PreparationId == preparationId, ct);
        if (!planExists) return null;
        return await ViewAsync(record, preparationId, ct);
    }

    private async Task<GitSourceBundleView> ViewAsync(
        GitSourceBundleEvidenceRecord row, Guid preparationId, CancellationToken ct)
    {
        if (row.ReadyForExecution ||
            row.BundleFileName != row.WorkpackId.ToString("N") + ".bundle" ||
            !ValidSha(row.BundleSha256) ||
            !ValidSha(row.SourceEvidenceSha256) ||
            !ValidSha(row.ReconciliationSha256) ||
            row.BundleBytes is < 1 or > 536_870_912 ||
            row.RefCount is < 1 or > GitMirrorRefReconciler.MaxRefsPerSide)
            throw new GitSourceBundleException("BundleIntegrityFailed");
        var pack = await workpacks.GetAsync(
            preparationId, row.PlanId, row.WorkpackId, ct)
            ?? throw new GitSourceBundleException("BundleIntegrityFailed");
        if (pack.ReconciliationId != row.ReconciliationId ||
            pack.ReconciliationSha256 != row.ReconciliationSha256 ||
            pack.SourceRepositoryId != row.SourceRepositoryId ||
            pack.State != GitTransferWorkpackStates.Blocked)
            throw new GitSourceBundleException("BundleIntegrityFailed");

        var page = await reconciliations.PageAsync(
            preparationId, row.PlanId, row.ReconciliationId, null, 1, ct)
            ?? throw new GitSourceBundleException("BundleIntegrityFailed");
        if (page.Reconciliation.SourceEvidenceSha256 != row.SourceEvidenceSha256 ||
            page.Reconciliation.ReconciliationSha256 != row.ReconciliationSha256 ||
            page.Reconciliation.SourceRefCount != row.RefCount)
            throw new GitSourceBundleException("BundleIntegrityFailed");

        // IsCurrent means the source/target proof remains current, not
        // that on-disk bytes are proven unchanged. Future consumers MUST
        // re-hash bundle bytes against BundleSha256 before any import.
        var current = pack.IsCurrent && page.Reconciliation.IsCurrent;
        return new(row.WorkpackId, row.PlanId, row.ReconciliationId,
            row.SourceRepositoryId, row.SourceEvidenceSha256,
            row.ReconciliationSha256, row.BundleFileName,
            row.BundleSha256, row.BundleBytes, row.RefCount,
            row.AcquiredAtUtc, current, false);
    }

    private static bool ValidSha(string? sha) => sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
