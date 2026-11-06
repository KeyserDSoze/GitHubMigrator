using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-039 immutable review rows; these MUST NEVER become execution grants.
public sealed class GitHubImportReadinessRecord
{
    public Guid Id { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid WorkpackId { get; set; }
    public Guid ReconciliationId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string TargetRepositoryName { get; set; } = "";
    public string Organization { get; set; } = "";
    public Guid DestinationConnectionId { get; set; }
    public DateTimeOffset? DestinationValidatedAtUtc { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string ReconciliationSha256 { get; set; } = "";
    public string TargetEvidenceSha256 { get; set; } = "";
    public string SourceBundleSha256 { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string RepositoryStatus { get; set; } = "";
    public string ReportedPush { get; set; } = "";
    public bool Archived { get; set; }
    public bool Disabled { get; set; }
    public int DivergedRefs { get; set; }
    public int TargetOnlyRefs { get; set; }
    public Guid CreatedBy { get; set; }
    public bool WriteCapabilityVerified { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class PostgresGitHubImportReadinessStore(
    AppDbContext db, TimeProvider clock,
    IGitTransferWorkpackRepository workpacks,
    IGitSourceBundleEvidenceStore bundles,
    IGitSourceBundleArtifactVerifier disk,
    IIncrementalGitRefReconciliationStore reconciliations,
    IIncrementalOperationPlanRepository plans)
    : IGitHubImportReadinessStore
{
    public async Task<GitHubImportReadinessView> SaveAsync(
        GitHubImportReadinessInput input, CancellationToken ct)
    {
        var seal = GitHubImportReadinessIntegrity.Seal(input);
        var observed = input.Observation;
        var age = clock.GetUtcNow() - observed.ObservedAtUtc;
        if (age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromMinutes(5))
            throw new GitHubImportReadinessException("ImportReadinessObservationExpired");

        var parent = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == input.PlanId &&
                x.PreparationId == input.PreparationId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({parent.SourceConnectionId.ToString()}), 732911)", ct);

        var plan = await plans.GetAsync(input.PreparationId, input.PlanId, ct);
        var pack = await workpacks.GetAsync(
            input.PreparationId, input.PlanId, input.WorkpackId, ct);
        var page = await reconciliations.PageAsync(
            input.PreparationId, input.PlanId, input.ReconciliationId, null, 1, ct);
        var proof = page?.Reconciliation;
        var bundle = await bundles.GetAsync(
            input.PreparationId, input.PlanId, input.WorkpackId, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == input.DestinationConnectionId, ct);
        if (plan is not { IsCurrent: true } ||
            pack is not { IsCurrent: true, State: GitTransferWorkpackStates.Blocked,
                Phase: GitTransferWorkpackPhases.WriteBlocked } ||
            proof is not { IsCurrent: true } ||
            bundle is not { IsCurrent: true, ReadyForExecution: false } ||
            destination is null ||
            destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            destination.ValidatedAtUtc != input.DestinationValidatedAtUtc ||
            destination.Id != plan.DestinationConnectionId ||
            !string.Equals(destination.Organization, input.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            plan.ManifestSha256 != input.ManifestSha256 ||
            pack.ManifestSha256 != input.ManifestSha256 ||
            pack.ReconciliationId != input.ReconciliationId ||
            proof.Id != input.ReconciliationId ||
            proof.ReconciliationSha256 != input.ReconciliationSha256 ||
            pack.ReconciliationSha256 != input.ReconciliationSha256 ||
            proof.TargetEvidenceSha256 != input.TargetEvidenceSha256 ||
            pack.TargetEvidenceSha256 != input.TargetEvidenceSha256 ||
            proof.SourceRepositoryId != input.SourceRepositoryId ||
            pack.SourceRepositoryId != input.SourceRepositoryId ||
            proof.TargetRepositoryName != input.TargetRepositoryName ||
            pack.TargetRepositoryName != input.TargetRepositoryName ||
            proof.DivergedCount != input.DivergedRefs ||
            proof.TargetOnlyCount != input.TargetOnlyRefs ||
            bundle.SourceEvidenceSha256 != proof.SourceEvidenceSha256 ||
            bundle.ReconciliationSha256 != input.ReconciliationSha256 ||
            bundle.BundleSha256 != input.SourceBundleSha256)
            throw new GitHubImportReadinessException("ImportReadinessContextChanged");

        // Re-hash on disk AGAIN after the GitHub GET, under the source lock.
        // A stale or synthetic DB-only bundle must never generate this proof.
        var check = await disk.VerifyAsync(bundle, ct);
        if (!check.IsCurrent || check.BundleSha256 != input.SourceBundleSha256 ||
            check.BundleBytes != bundle.BundleBytes || check.ReadyForExecution)
            throw new GitHubImportReadinessException("ImportReadinessDiskMismatch");

        var row = new GitHubImportReadinessRecord
        {
            Id = Guid.NewGuid(), PreparationId = input.PreparationId,
            PlanId = input.PlanId, WorkpackId = input.WorkpackId,
            ReconciliationId = input.ReconciliationId,
            SourceRepositoryId = input.SourceRepositoryId,
            TargetRepositoryName = input.TargetRepositoryName,
            Organization = input.Organization,
            DestinationConnectionId = input.DestinationConnectionId,
            DestinationValidatedAtUtc = input.DestinationValidatedAtUtc,
            ManifestSha256 = input.ManifestSha256,
            ReconciliationSha256 = input.ReconciliationSha256,
            TargetEvidenceSha256 = input.TargetEvidenceSha256,
            SourceBundleSha256 = input.SourceBundleSha256,
            EvidenceSha256 = seal, ObservedAtUtc = input.Observation.ObservedAtUtc,
            RepositoryStatus = observed.Status, ReportedPush = observed.ReportedPush,
            Archived = observed.Archived, Disabled = observed.Disabled,
            DivergedRefs = input.DivergedRefs, TargetOnlyRefs = input.TargetOnlyRefs,
            CreatedBy = input.ActorId, WriteCapabilityVerified = false,
            ReadyForExecution = false
        };

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_import_readiness (
                id, preparation_id, plan_id, workpack_id, reconciliation_id,
                source_repository_id, target_repository_name, organization,
                destination_connection_id, destination_validated_at_utc,
                manifest_sha256, reconciliation_sha256, target_evidence_sha256,
                source_bundle_sha256, evidence_sha256, observed_at_utc,
                repository_status, reported_push, archived, disabled,
                diverged_refs, target_only_refs, created_by,
                write_capability_verified, ready_for_execution)
            VALUES (
                {row.Id}, {row.PreparationId}, {row.PlanId}, {row.WorkpackId},
                {row.ReconciliationId}, {row.SourceRepositoryId},
                {row.TargetRepositoryName}, {row.Organization},
                {row.DestinationConnectionId}, {row.DestinationValidatedAtUtc},
                {row.ManifestSha256}, {row.ReconciliationSha256},
                {row.TargetEvidenceSha256}, {row.SourceBundleSha256},
                {row.EvidenceSha256}, {row.ObservedAtUtc},
                {row.RepositoryStatus}, {row.ReportedPush},
                {row.Archived}, {row.Disabled}, {row.DivergedRefs},
                {row.TargetOnlyRefs}, {row.CreatedBy}, false, false)
            ON CONFLICT (workpack_id, evidence_sha256) DO NOTHING
            """, ct);
        var saved = await db.GitHubImportReadiness.AsNoTracking()
            .SingleAsync(x => x.WorkpackId == input.WorkpackId &&
                x.EvidenceSha256 == seal, ct);
        _ = VerifyRow(saved);
        await tx.CommitAsync(ct);
        return await ViewAsync(saved, ct);
    }

    public async Task<GitHubImportReadinessView?> GetAsync(
        Guid preparationId, Guid planId, Guid id, CancellationToken ct)
    {
        var row = await db.GitHubImportReadiness.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id &&
                x.PreparationId == preparationId && x.PlanId == planId, ct);
        return row is null ? null : await ViewAsync(row, ct);
    }

    public async Task<IReadOnlyList<GitHubImportReadinessView>> ListAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct)
    {
        var rows = await db.GitHubImportReadiness.AsNoTracking()
            .Where(x => x.PreparationId == preparationId &&
                x.PlanId == planId && x.WorkpackId == workpackId)
            .OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Id)
            .Take(20).ToArrayAsync(ct);
        var results = new List<GitHubImportReadinessView>(rows.Length);
        foreach (var row in rows) results.Add(await ViewAsync(row, ct));
        return results;
    }

    private static GitHubImportReadinessInput VerifyRow(GitHubImportReadinessRecord row)
    {
        if (row.ReadyForExecution || row.WriteCapabilityVerified)
            throw new GitHubImportReadinessException("ImportReadinessIntegrityFailed");
        var observation = new GitHubImportRepositoryObservation(
            row.RepositoryStatus, row.ReportedPush, row.Archived, row.Disabled,
            row.ObservedAtUtc);
        var input = new GitHubImportReadinessInput(
            row.PreparationId, row.PlanId, row.WorkpackId,
            row.ReconciliationId, row.SourceRepositoryId,
            row.TargetRepositoryName, row.Organization,
            row.DestinationConnectionId, row.DestinationValidatedAtUtc,
            row.ManifestSha256, row.ReconciliationSha256,
            row.TargetEvidenceSha256, row.SourceBundleSha256,
            row.DivergedRefs, row.TargetOnlyRefs, observation, row.CreatedBy);
        try
        {
            if (GitHubImportReadinessIntegrity.Seal(input) != row.EvidenceSha256)
                throw new GitHubImportReadinessException("ImportReadinessIntegrityFailed");
        }
        catch (GitHubImportReadinessException)
        { throw new GitHubImportReadinessException("ImportReadinessIntegrityFailed"); }
        return input;
    }

    private async Task<GitHubImportReadinessView> ViewAsync(
        GitHubImportReadinessRecord row, CancellationToken ct)
    {
        var input = VerifyRow(row);
        var plan = await plans.GetAsync(row.PreparationId, row.PlanId, ct);
        var pack = await workpacks.GetAsync(
            row.PreparationId, row.PlanId, row.WorkpackId, ct);
        var page = await reconciliations.PageAsync(row.PreparationId,
            row.PlanId, row.ReconciliationId, null, 1, ct);
        var bundle = await bundles.GetAsync(
            row.PreparationId, row.PlanId, row.WorkpackId, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == row.DestinationConnectionId, ct);
        if (plan is null || pack is null || page is null || bundle is null ||
            pack.ReconciliationId != row.ReconciliationId ||
            page.Reconciliation.ReconciliationSha256 != row.ReconciliationSha256 ||
            page.Reconciliation.TargetEvidenceSha256 != row.TargetEvidenceSha256 ||
            page.Reconciliation.SourceRepositoryId != row.SourceRepositoryId ||
            page.Reconciliation.TargetRepositoryName != row.TargetRepositoryName ||
            bundle.BundleSha256 != row.SourceBundleSha256 ||
            plan.ManifestSha256 != row.ManifestSha256)
            throw new GitHubImportReadinessException("ImportReadinessIntegrityFailed");

        var newer = await db.GitHubImportReadiness.AsNoTracking()
            .Where(x => x.WorkpackId == row.WorkpackId)
            .OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Id)
            .Select(x => x.Id).FirstAsync(ct);
        var age = clock.GetUtcNow() - row.ObservedAtUtc;
        var current = plan.IsCurrent && pack.IsCurrent &&
            page.Reconciliation.IsCurrent && bundle.IsCurrent &&
            destination is { ValidationCode: "Verified" } &&
            destination.ValidatedAtUtc == row.DestinationValidatedAtUtc &&
            newer == row.Id && age >= TimeSpan.Zero &&
            age <= TimeSpan.FromMinutes(5);
        // IsCurrent is DB planning/provider freshness ONLY. It is not
        // re-verification of physical bundle bytes on this read-only GET.
        return new(row.Id, row.PlanId, row.WorkpackId, row.ReconciliationId,
            row.SourceRepositoryId, row.TargetRepositoryName, row.Organization,
            row.ManifestSha256, row.ReconciliationSha256,
            row.TargetEvidenceSha256, row.SourceBundleSha256,
            row.EvidenceSha256, row.ObservedAtUtc, row.RepositoryStatus,
            row.ReportedPush, row.Archived, row.Disabled,
            row.DivergedRefs, row.TargetOnlyRefs,
            GitHubImportReadinessIntegrity.Blockers(input),
            current, false, false);
    }
}
