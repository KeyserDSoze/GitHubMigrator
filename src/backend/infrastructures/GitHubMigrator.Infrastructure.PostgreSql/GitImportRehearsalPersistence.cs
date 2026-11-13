using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-041 records completed isolated local transport rehearsals only.
// This record is not a GitHub upload receipt or an authorization token.
public sealed class GitImportRehearsalRecord
{
    public Guid Id { get; set; }
    public Guid IntentId { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid WorkpackId { get; set; }
    public string IntentEvidenceSha256 { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string ReconciliationSha256 { get; set; } = "";
    public string BundleSha256 { get; set; } = "";
    public string SafeRefPlanSha256 { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public string GitObjectFormat { get; set; } = "";
    public string VerifiedSourceRefsSha256 { get; set; } = "";
    public int CreateCandidates { get; set; }
    public int AlreadyMatching { get; set; }
    public int TargetOnlyNotMaterialized { get; set; }
    public int DestinationCreated { get; set; }
    public string CreatedRefsSha256 { get; set; } = "";
    public DateTimeOffset CompletedAtUtc { get; set; }
    public Guid RequestedBy { get; set; }
    public bool DestinationWritesPerformed { get; set; }
    public bool ExecutionAuthorized { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class PostgresGitImportRehearsalStore(
    AppDbContext db, TimeProvider clock,
    IGitHubImportIntentStore intents,
    IIncrementalGitRefReconciliationStore comparisons,
    IGitSourceBundleEvidenceStore bundles,
    IGitSourceBundleArtifactVerifier verifier,
    IIncrementalOperationPlanRepository plans,
    IGitTransferWorkpackRepository workpacks)
    : IGitImportRehearsalStore
{
    public async Task<GitImportRehearsalView> SaveAsync(
        GitImportRehearsalInput input, CancellationToken ct)
    {
        var seal = GitImportRehearsalIntegrity.Seal(input);
        var age = clock.GetUtcNow() - input.Artifact.CompletedAtUtc;
        if (age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromMinutes(2))
            throw new GitImportRehearsalException("GitRehearsalResultExpired");

        var original = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == input.PreparationId &&
                x.Id == input.PlanId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({original.SourceConnectionId.ToString()}), 732911)",
            ct);
        var intent = await intents.GetAsync(input.PreparationId,
            input.PlanId, input.IntentId, ct);
        var plan = await plans.GetAsync(input.PreparationId, input.PlanId, ct);
        var work = await workpacks.GetAsync(
            input.PreparationId, input.PlanId, input.WorkpackId, ct);
        var bundle = await bundles.GetAsync(
            input.PreparationId, input.PlanId, input.WorkpackId, ct);
        if (intent is not { IsCurrent: true, IsExpired: false,
                State: GitHubImportIntentStates.Acknowledged } ||
            plan is not { IsCurrent: true } ||
            work is not { IsCurrent: true, State: GitTransferWorkpackStates.Blocked,
                Phase: GitTransferWorkpackPhases.WriteBlocked } ||
            bundle is not { IsCurrent: true, ReadyForExecution: false } ||
            intent.WorkpackId != input.WorkpackId ||
            intent.EvidenceSha256 != input.IntentEvidenceSha256 ||
            intent.ManifestSha256 != input.ManifestSha256 ||
            intent.ReconciliationSha256 != input.ReconciliationSha256 ||
            intent.BundleSha256 != input.BundleSha256 ||
            intent.SafeRefPlanSha256 != input.SafeRefPlanSha256 ||
            intent.CreateCandidates != input.Artifact.CreateCandidates ||
            intent.AlreadyMatching != input.Artifact.AlreadyMatching ||
            intent.TargetOnlyPreserved != input.Artifact.TargetOnlyNotMaterialized ||
            intent.DivergedBlocked != 0 ||
            intent.CreateCandidates != input.Artifact.DestinationCreated ||
            work.Id != input.WorkpackId ||
            work.ReconciliationId != bundle.ReconciliationId ||
            work.ReconciliationSha256 != input.ReconciliationSha256 ||
            plan.ManifestSha256 != input.ManifestSha256 ||
            bundle.BundleSha256 != input.BundleSha256 ||
            input.Artifact.CompletedAtUtc > intent.ExpiresAtUtc)
            throw new GitImportRehearsalException("GitRehearsalProofNotCurrent");

        var disk = await verifier.VerifyAsync(bundle, ct);
        if (!disk.IsCurrent || disk.ReadyForExecution ||
            disk.BundleSha256 != input.BundleSha256 ||
            disk.BundleBytes != bundle.BundleBytes)
            throw new GitImportRehearsalException("GitRehearsalPhysicalBundleChanged");

        var comparison = await comparisons.PageAsync(input.PreparationId,
            input.PlanId, bundle.ReconciliationId, null, 1, ct)
            ?? throw new GitImportRehearsalException("GitRehearsalReconciliationMissing");
        var review = new GitHubImportReadinessView(
            intent.ImportReadinessId, input.PlanId, input.WorkpackId,
            bundle.ReconciliationId, bundle.SourceRepositoryId,
            work.TargetRepositoryName, plan.Organization,
            input.ManifestSha256, input.ReconciliationSha256,
            comparison.Reconciliation.TargetEvidenceSha256, input.BundleSha256,
            intent.ImportReadinessSha256, clock.GetUtcNow(), "Visible",
            "ReportedAllowed", false, false, 0, 0, [], true, false, false);
        var safe = await GitHubImportIntentService.BuildVerifiedPlanAsync(
            comparisons, input.PreparationId, input.PlanId,
            bundle.ReconciliationId, review, ct);
        if (!safe.HasOnlyNonDestructiveCandidates ||
            safe.CreateCandidates != input.Artifact.CreateCandidates ||
            safe.AlreadyMatching != input.Artifact.AlreadyMatching ||
            safe.TargetOnlyPreserved != input.Artifact.TargetOnlyNotMaterialized ||
            safe.EvidenceSha256 != input.SafeRefPlanSha256)
            throw new GitImportRehearsalException("GitRehearsalRefEvidenceChanged");

        var record = new GitImportRehearsalRecord {
            Id = Guid.NewGuid(), IntentId = input.IntentId,
            PreparationId = input.PreparationId, PlanId = input.PlanId,
            WorkpackId = input.WorkpackId,
            IntentEvidenceSha256 = input.IntentEvidenceSha256,
            ManifestSha256 = input.ManifestSha256,
            ReconciliationSha256 = input.ReconciliationSha256,
            BundleSha256 = input.BundleSha256,
            SafeRefPlanSha256 = input.SafeRefPlanSha256,
            EvidenceSha256 = seal,
            GitObjectFormat = input.Artifact.GitObjectFormat,
            VerifiedSourceRefsSha256 = input.Artifact.VerifiedSourceRefsSha256,
            CreateCandidates = input.Artifact.CreateCandidates,
            AlreadyMatching = input.Artifact.AlreadyMatching,
            TargetOnlyNotMaterialized = input.Artifact.TargetOnlyNotMaterialized,
            DestinationCreated = input.Artifact.DestinationCreated,
            CreatedRefsSha256 = input.Artifact.CreatedRefsSha256,
            CompletedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                input.Artifact.CompletedAtUtc.ToUnixTimeMilliseconds()),
            RequestedBy = input.RequestedBy
        };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_import_rehearsals (
                id, intent_id, preparation_id, plan_id, workpack_id,
                intent_evidence_sha256, manifest_sha256,
                reconciliation_sha256, bundle_sha256, safe_ref_plan_sha256,
                evidence_sha256, git_object_format, verified_source_refs_sha256,
                create_candidates, already_matching, target_only_not_materialized,
                destination_created, created_refs_sha256, completed_at_utc,
                requested_by, destination_writes_performed,
                execution_authorized, ready_for_execution)
            VALUES ({record.Id}, {record.IntentId}, {record.PreparationId},
                {record.PlanId}, {record.WorkpackId},
                {record.IntentEvidenceSha256}, {record.ManifestSha256},
                {record.ReconciliationSha256}, {record.BundleSha256},
                {record.SafeRefPlanSha256}, {record.EvidenceSha256},
                {record.GitObjectFormat}, {record.VerifiedSourceRefsSha256},
                {record.CreateCandidates}, {record.AlreadyMatching},
                {record.TargetOnlyNotMaterialized}, {record.DestinationCreated},
                {record.CreatedRefsSha256}, {record.CompletedAtUtc},
                {record.RequestedBy}, false, false, false)
            ON CONFLICT (intent_id) DO NOTHING
            """, ct);
        var saved = await db.GitImportRehearsals.AsNoTracking()
            .SingleAsync(x => x.IntentId == input.IntentId, ct);
        // A replay may arrive after a concurrent real Git rehearsal with
        // identical ref results but a different completedAt timestamp.
        // Preserve the first SHA sealed evidence, never overwrite it.
        if (saved.IntentEvidenceSha256 != input.IntentEvidenceSha256 ||
            saved.BundleSha256 != input.BundleSha256 ||
            saved.SafeRefPlanSha256 != input.SafeRefPlanSha256 ||
            saved.GitObjectFormat != input.Artifact.GitObjectFormat ||
            saved.VerifiedSourceRefsSha256 != input.Artifact.VerifiedSourceRefsSha256 ||
            saved.CreatedRefsSha256 != input.Artifact.CreatedRefsSha256 ||
            saved.DestinationCreated != input.Artifact.DestinationCreated ||
            saved.RequestedBy != input.RequestedBy)
            throw new GitImportRehearsalException("GitRehearsalConflictingReplay");
        await tx.CommitAsync(ct);
        return await ViewAsync(saved, ct);
    }

    public async Task<GitImportRehearsalView?> GetAsync(
        Guid preparationId, Guid planId, Guid rehearsalId, CancellationToken ct)
    {
        var row = await db.GitImportRehearsals.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == rehearsalId &&
                x.PreparationId == preparationId && x.PlanId == planId, ct);
        return row is null ? null : await ViewAsync(row, ct);
    }

    public async Task<IReadOnlyList<GitImportRehearsalView>> ListAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct)
    {
        var rows = await db.GitImportRehearsals.AsNoTracking()
            .Where(x => x.PreparationId == preparationId &&
                x.PlanId == planId && x.WorkpackId == workpackId)
            .OrderByDescending(x => x.CompletedAtUtc).ThenByDescending(x => x.Id)
            .Take(20).ToArrayAsync(ct);
        var results = new List<GitImportRehearsalView>(rows.Length);
        foreach (var row in rows) results.Add(await ViewAsync(row, ct));
        return results;
    }

    private async Task<GitImportRehearsalView> ViewAsync(
        GitImportRehearsalRecord row, CancellationToken ct)
    {
        var intent = await intents.GetAsync(
            row.PreparationId, row.PlanId, row.IntentId, ct);
        var input = new GitImportRehearsalInput(
            row.PreparationId, row.PlanId, row.WorkpackId, row.IntentId,
            row.IntentEvidenceSha256, row.ManifestSha256,
            row.ReconciliationSha256, row.BundleSha256,
            row.SafeRefPlanSha256, new(row.GitObjectFormat,
                row.VerifiedSourceRefsSha256, row.CreateCandidates,
                row.AlreadyMatching, row.TargetOnlyNotMaterialized,
                row.DestinationCreated, row.CreatedRefsSha256,
                row.CompletedAtUtc), row.RequestedBy);
        if (GitImportRehearsalIntegrity.Seal(input) != row.EvidenceSha256 ||
            row.DestinationWritesPerformed || row.ExecutionAuthorized ||
            row.ReadyForExecution || intent is null ||
            intent.EvidenceSha256 != row.IntentEvidenceSha256 ||
            intent.SafeRefPlanSha256 != row.SafeRefPlanSha256 ||
            intent.WorkpackId != row.WorkpackId ||
            intent.BundleSha256 != row.BundleSha256)
            throw new GitImportRehearsalException("GitRehearsalIntegrityFailed");
        return new(row.Id, row.IntentId, row.PreparationId, row.PlanId,
            row.WorkpackId, row.IntentEvidenceSha256, row.ManifestSha256,
            row.ReconciliationSha256, row.BundleSha256,
            row.SafeRefPlanSha256, row.EvidenceSha256,
            row.GitObjectFormat, row.VerifiedSourceRefsSha256,
            row.CreateCandidates, row.AlreadyMatching,
            row.TargetOnlyNotMaterialized, row.DestinationCreated,
            row.CreatedRefsSha256, row.CompletedAtUtc, row.RequestedBy,
            intent.IsCurrent && !intent.IsExpired,
            false, false, false);
    }
}
