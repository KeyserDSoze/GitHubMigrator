using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// Immutable F-042 pre-write remote ref observations. There is NO code path
// that grants write capability from a successful GET-only REST snapshot.
public sealed class GitHubRemoteRefFenceRecord
{
    public Guid Id { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid WorkpackId { get; set; }
    public Guid IntentId { get; set; }
    public Guid RehearsalId { get; set; }
    public Guid TargetObservationId { get; set; }
    public Guid DestinationConnectionId { get; set; }
    public DateTimeOffset? DestinationValidatedAtUtc { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string ReconciliationSha256 { get; set; } = "";
    public string BundleSha256 { get; set; } = "";
    public string IntentEvidenceSha256 { get; set; } = "";
    public string RehearsalEvidenceSha256 { get; set; } = "";
    public string TargetEvidenceSha256 { get; set; } = "";
    public string RemoteEvidenceSha256 { get; set; } = "";
    public string RemoteRefFingerprintSha256 { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public string Status { get; set; } = "";
    public int BaselineCount { get; set; }
    public int RemoteCount { get; set; }
    public int ChangedRefs { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public Guid ActorId { get; set; }
    public bool RemoteWritesPerformed { get; set; }
    public bool WriteCapabilityVerified { get; set; }
    public bool ExecutionAuthorized { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class PostgresGitHubRemoteRefFenceStore(
    AppDbContext db, TimeProvider clock,
    IIncrementalOperationPlanRepository plans,
    IIncrementalGitRefReconciliationStore reconciliations,
    IGitHubTargetRefEvidenceStore targetEvidence,
    IGitHubImportIntentStore intents,
    IGitImportRehearsalStore rehearsals,
    IGitHubImportReadinessStore readiness,
    IGitSourceBundleEvidenceStore bundles,
    IGitSourceBundleArtifactVerifier disk)
    : IGitHubRemoteRefFenceStore
{
    public async Task<GitHubRemoteRefFenceView> SaveAsync(
        GitHubRemoteRefFenceInput input, CancellationToken ct)
    {
        var seal = GitHubRemoteRefFenceIntegrity.Seal(input);
        var now = clock.GetUtcNow();
        var age = now - input.Comparison.ObservedAtUtc;
        if (age < TimeSpan.FromSeconds(-5) ||
            age > GitHubRemoteRefFenceIntegrity.MaximumAge)
            throw new GitHubRemoteRefFenceException("RemoteFenceObservationExpired");

        var planRow = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == input.PreparationId &&
                x.Id == input.PlanId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({planRow.SourceConnectionId.ToString()}), 732911)",
            ct);

        var plan = await plans.GetAsync(input.PreparationId, input.PlanId, ct);
        var intent = await intents.GetAsync(
            input.PreparationId, input.PlanId, input.IntentId, ct);
        var rehearsal = await rehearsals.GetAsync(
            input.PreparationId, input.PlanId, input.RehearsalId, ct);
        var review = intent is null ? null : await readiness.GetAsync(
            input.PreparationId, input.PlanId, intent.ImportReadinessId, ct);
        var reconciliation = review is null ? null :
            await reconciliations.PageAsync(
                input.PreparationId, input.PlanId, review.ReconciliationId,
                null, 1, ct);
        var baseline = await GitHubRemoteRefFenceService.LoadBaselineAsync(
            targetEvidence, input.PreparationId, input.PlanId,
            input.TargetObservationId, input.TargetEvidenceSha256, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == input.DestinationConnectionId, ct);
        var bundle = await bundles.GetAsync(
            input.PreparationId, input.PlanId, input.WorkpackId, ct);

        if (plan is not { IsCurrent: true } ||
            intent is not { IsCurrent: true, IsExpired: false,
                State: GitHubImportIntentStates.Acknowledged } ||
            rehearsal is not { IsCurrent: true,
                DestinationWritesPerformed: false,
                ExecutionAuthorized: false, ReadyForExecution: false } ||
            review is not { IsCurrent: true } ||
            reconciliation is null || !reconciliation.Reconciliation.IsCurrent ||
            bundle is not { IsCurrent: true, ReadyForExecution: false } ||
            destination is null ||
            destination.Id != plan.DestinationConnectionId ||
            destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            destination.ValidatedAtUtc != input.DestinationValidatedAtUtc ||
            !string.Equals(destination.Organization, review.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            plan.ManifestSha256 != input.ManifestSha256 ||
            review.ManifestSha256 != input.ManifestSha256 ||
            rehearsal.ManifestSha256 != input.ManifestSha256 ||
            intent.ManifestSha256 != input.ManifestSha256 ||
            rehearsal.WorkpackId != input.WorkpackId ||
            rehearsal.IntentId != input.IntentId ||
            rehearsal.EvidenceSha256 != input.RehearsalEvidenceSha256 ||
            rehearsal.IntentEvidenceSha256 != input.IntentEvidenceSha256 ||
            rehearsal.BundleSha256 != input.BundleSha256 ||
            intent.EvidenceSha256 != input.IntentEvidenceSha256 ||
            intent.BundleSha256 != input.BundleSha256 ||
            review.SourceBundleSha256 != input.BundleSha256 ||
            reconciliation.Reconciliation.ReconciliationSha256 !=
                input.ReconciliationSha256 ||
            reconciliation.Reconciliation.TargetObservationId !=
                input.TargetObservationId ||
            reconciliation.Reconciliation.TargetEvidenceSha256 !=
                input.TargetEvidenceSha256 ||
            reconciliation.Reconciliation.DivergedCount != 0 ||
            review.TargetEvidenceSha256 != input.TargetEvidenceSha256 ||
            bundle.BundleSha256 != input.BundleSha256 ||
            bundle.ReconciliationSha256 != input.ReconciliationSha256 ||
            baseline.Count != input.Comparison.BaselineCount)
            throw new GitHubRemoteRefFenceException("RemoteFenceLineageChanged");

        // PostgreSQL check is after the provider GET under same source lock
        // used by F-031 through F-041, plus a SECOND physical SHA check.
        var verified = await disk.VerifyAsync(bundle, ct);
        if (!verified.IsCurrent || verified.ReadyForExecution ||
            verified.BundleSha256 != input.BundleSha256 ||
            verified.BundleBytes != bundle.BundleBytes)
            throw new GitHubRemoteRefFenceException("RemoteFenceBundleChanged");

        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(
            input.Comparison.ObservedAtUtc.ToUnixTimeMilliseconds());
        var row = new GitHubRemoteRefFenceRecord {
            Id = Guid.NewGuid(), PreparationId = input.PreparationId,
            PlanId = input.PlanId, WorkpackId = input.WorkpackId,
            IntentId = input.IntentId, RehearsalId = input.RehearsalId,
            TargetObservationId = input.TargetObservationId,
            DestinationConnectionId = input.DestinationConnectionId,
            DestinationValidatedAtUtc = input.DestinationValidatedAtUtc,
            ManifestSha256 = input.ManifestSha256,
            ReconciliationSha256 = input.ReconciliationSha256,
            BundleSha256 = input.BundleSha256,
            IntentEvidenceSha256 = input.IntentEvidenceSha256,
            RehearsalEvidenceSha256 = input.RehearsalEvidenceSha256,
            TargetEvidenceSha256 = input.TargetEvidenceSha256,
            RemoteEvidenceSha256 = input.RemoteEvidenceSha256,
            RemoteRefFingerprintSha256 = input.Comparison.RemoteRefFingerprintSha256,
            EvidenceSha256 = seal, Status = input.Comparison.Status,
            BaselineCount = input.Comparison.BaselineCount,
            RemoteCount = input.Comparison.RemoteCount,
            ChangedRefs = input.Comparison.ChangedRefs,
            ObservedAtUtc = stamp,
            ExpiresAtUtc = stamp.Add(GitHubRemoteRefFenceIntegrity.MaximumAge),
            ActorId = input.ActorId
        };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_ref_fences (
                id, preparation_id, plan_id, workpack_id, intent_id,
                rehearsal_id, target_observation_id,
                destination_connection_id, destination_validated_at_utc,
                manifest_sha256, reconciliation_sha256, bundle_sha256,
                intent_evidence_sha256, rehearsal_evidence_sha256,
                target_evidence_sha256, remote_evidence_sha256,
                remote_ref_fingerprint_sha256, evidence_sha256,
                status, baseline_count, remote_count, changed_refs,
                observed_at_utc, expires_at_utc, actor_id,
                remote_writes_performed, write_capability_verified,
                execution_authorized, ready_for_execution)
            VALUES ({row.Id}, {row.PreparationId}, {row.PlanId},
                {row.WorkpackId}, {row.IntentId}, {row.RehearsalId},
                {row.TargetObservationId}, {row.DestinationConnectionId},
                {row.DestinationValidatedAtUtc}, {row.ManifestSha256},
                {row.ReconciliationSha256}, {row.BundleSha256},
                {row.IntentEvidenceSha256}, {row.RehearsalEvidenceSha256},
                {row.TargetEvidenceSha256}, {row.RemoteEvidenceSha256},
                {row.RemoteRefFingerprintSha256}, {row.EvidenceSha256},
                {row.Status}, {row.BaselineCount}, {row.RemoteCount},
                {row.ChangedRefs}, {row.ObservedAtUtc}, {row.ExpiresAtUtc},
                {row.ActorId}, false, false, false, false)
            ON CONFLICT (rehearsal_id, remote_evidence_sha256) DO NOTHING
            """, ct);
        var saved = await db.GitHubRemoteRefFences.AsNoTracking()
            .SingleAsync(x => x.RehearsalId == input.RehearsalId &&
                x.RemoteEvidenceSha256 == input.RemoteEvidenceSha256, ct);
        if (saved.EvidenceSha256 != seal || saved.ActorId != input.ActorId)
            throw new GitHubRemoteRefFenceException("RemoteFenceConflictingReplay");
        await tx.CommitAsync(ct);
        return await ViewAsync(saved, ct);
    }

    public async Task<GitHubRemoteRefFenceView?> GetAsync(
        Guid preparationId, Guid planId, Guid fenceId, CancellationToken ct)
    {
        var row = await db.GitHubRemoteRefFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId &&
                x.PlanId == planId && x.Id == fenceId, ct);
        return row is null ? null : await ViewAsync(row, ct);
    }

    public async Task<IReadOnlyList<GitHubRemoteRefFenceView>> ListAsync(
        Guid preparationId, Guid planId, Guid rehearsalId, CancellationToken ct)
    {
        var rows = await db.GitHubRemoteRefFences.AsNoTracking()
            .Where(x => x.PreparationId == preparationId &&
                x.PlanId == planId && x.RehearsalId == rehearsalId)
            .OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Id)
            .Take(20).ToArrayAsync(ct);
        var results = new List<GitHubRemoteRefFenceView>(rows.Length);
        foreach (var row in rows) results.Add(await ViewAsync(row, ct));
        return results;
    }

    private async Task<GitHubRemoteRefFenceView> ViewAsync(
        GitHubRemoteRefFenceRecord row, CancellationToken ct)
    {
        var input = new GitHubRemoteRefFenceInput(
            row.PreparationId, row.PlanId, row.WorkpackId,
            row.IntentId, row.RehearsalId, row.TargetObservationId,
            row.DestinationConnectionId, row.DestinationValidatedAtUtc,
            row.ManifestSha256, row.ReconciliationSha256, row.BundleSha256,
            row.IntentEvidenceSha256, row.RehearsalEvidenceSha256,
            row.TargetEvidenceSha256, row.RemoteEvidenceSha256,
            new(row.Status, row.RemoteRefFingerprintSha256,
                row.BaselineCount, row.RemoteCount, row.ChangedRefs,
                row.ObservedAtUtc), row.ActorId);
        if (GitHubRemoteRefFenceIntegrity.Seal(input) != row.EvidenceSha256 ||
            row.RemoteWritesPerformed || row.WriteCapabilityVerified ||
            row.ExecutionAuthorized || row.ReadyForExecution ||
            row.ExpiresAtUtc !=
                row.ObservedAtUtc.Add(GitHubRemoteRefFenceIntegrity.MaximumAge))
            throw new GitHubRemoteRefFenceException("RemoteFenceIntegrityFailed");

        var plan = await plans.GetAsync(row.PreparationId, row.PlanId, ct);
        var intent = await intents.GetAsync(
            row.PreparationId, row.PlanId, row.IntentId, ct);
        var rehearsal = await rehearsals.GetAsync(
            row.PreparationId, row.PlanId, row.RehearsalId, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == row.DestinationConnectionId, ct);
        if (plan is null || intent is null || rehearsal is null ||
            intent.EvidenceSha256 != row.IntentEvidenceSha256 ||
            rehearsal.EvidenceSha256 != row.RehearsalEvidenceSha256 ||
            rehearsal.BundleSha256 != row.BundleSha256 ||
            plan.ManifestSha256 != row.ManifestSha256)
            throw new GitHubRemoteRefFenceException("RemoteFenceIntegrityFailed");
        var current = row.Status == "RefSetUnchanged" &&
            row.ExpiresAtUtc > clock.GetUtcNow() &&
            plan.IsCurrent && intent.IsCurrent && !intent.IsExpired &&
            rehearsal.IsCurrent && destination is { ValidationCode: "Verified" } &&
            destination.ValidatedAtUtc == row.DestinationValidatedAtUtc &&
            !await db.GitHubRemoteRefFences.AsNoTracking()
                .AnyAsync(x => x.RehearsalId == row.RehearsalId &&
                    (x.ObservedAtUtc > row.ObservedAtUtc ||
                     (x.ObservedAtUtc == row.ObservedAtUtc &&
                      x.Id.CompareTo(row.Id) > 0)), ct);
        // "Current" is a recently observed ref snapshot, not a remote
        // atomic reservation, write-scope proof, or authorization.
        return new(row.Id, row.PreparationId, row.PlanId, row.WorkpackId,
            row.IntentId, row.RehearsalId, row.TargetObservationId,
            row.ManifestSha256, row.ReconciliationSha256, row.BundleSha256,
            row.IntentEvidenceSha256, row.RehearsalEvidenceSha256,
            row.TargetEvidenceSha256, row.RemoteEvidenceSha256,
            row.RemoteRefFingerprintSha256, row.EvidenceSha256,
            row.Status, row.BaselineCount, row.RemoteCount, row.ChangedRefs,
            row.ObservedAtUtc, row.ExpiresAtUtc, row.ActorId, current,
            false, false, false, false);
    }
}
