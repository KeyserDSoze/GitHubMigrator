using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-097c.3: opt-in, read-only cross-table F-097 source checkpoint
// to F-061 verified physical mirror provenance join. No execution outbox,
// filesystem path, credential, remote Git URL or F-095 INSERT.
public sealed record GitIncrementalCheckpointMirrorPreflightOptions(bool Enabled);

public sealed class PostgresGitIncrementalCheckpointMirrorPreflight(
    AppDbContext db, TimeProvider clock,
    GitIncrementalCheckpointMirrorPreflightOptions options,
    PostgresGitIncrementalCheckpointAuditPreflight refPreflight,
    PostgresGitHubSourceCheckpointGitRefLedger fullRefLedger,
    IIncrementalOperationPlanRepository plans)
{
    private static GitIncrementalAncestryLedgerException Refused(
        string code) => new(code);

    public async Task<GitIncrementalCheckpointMirrorReadPlan> ReadAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, string sourceRepositoryId,
        string expectedTargetEvidenceSha256, Guid workpackId,
        CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("GitCheckpointMirrorPreflightDisabled");
        if (workpackId == Guid.Empty)
            throw Refused("GitCheckpointMirrorWorkpackRequired");

        GitIncrementalCheckpointAuditInputs trusted;
        try
        {
            trusted = await refPreflight.ReadAsync(
                preparationId, planId, checkpointId, targetObservationId,
                sourceRepositoryId, expectedTargetEvidenceSha256, ct);
        }
        catch (GitIncrementalCheckpointPreflightException)
        { throw Refused("GitCheckpointMirrorSourceTargetUnverified"); }

        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw Refused("GitCheckpointMirrorPlanNotFound");
        GitHubSourceCheckpointGitRefProof? full;
        try
        {
            full = await fullRefLedger.GetVerifiedAsync(
                preparationId, planId, checkpointId, sourceRepositoryId, ct);
        }
        catch (GitHubSourceCheckpointGitRefLedgerException)
        { throw Refused("GitCheckpointMirrorSourceProofCorrupt"); }
        if (full is null ||
            trusted.EvidenceVersion != 1 ||
            trusted.SourceSnapshotId != full.SnapshotId ||
            trusted.SourceFullRefProofSha256 != full.EvidenceSha256 ||
            trusted.SourceConnectionId != full.SourceConnectionId ||
            !trusted.ExpectedSourceRefs.SequenceEqual(full.Refs) ||
            !plan.IsCurrent || plan.ReadyForExecution)
            throw Refused("GitCheckpointMirrorSourceProofMismatch");

        VerifiedSourceGitRefs source;
        try
        {
            source = VerifiedSourceGitRefIntegrity.Seal(
                trusted.SourceSnapshotId, trusted.SourceConnectionId,
                trusted.SourceRepositoryId, full.SourceManifestRootSha256,
                trusted.ExpectedSourceRefs);
        }
        catch (IncrementalGitRefReconciliationException)
        { throw Refused("GitCheckpointMirrorSourceProofMismatch"); }
        if (source.SourceEvidenceSha256 != full.SourceEvidenceSha256)
            throw Refused("GitCheckpointMirrorSourceProofMismatch");

        (GitHubTargetRefEvidenceView View, GitHubRefObservation Observation)? target;
        try
        {
            target = await new PostgresGitHubTargetRefEvidenceStore(db, clock)
                .ReadVerifiedSnapshotAsync(preparationId, planId,
                    targetObservationId, ct);
        }
        catch (GitHubTargetRefEvidenceException)
        { throw Refused("GitCheckpointMirrorTargetCorrupt"); }
        if (target is null || !target.Value.View.IsCurrent ||
            target.Value.View.EvidenceSha256 != trusted.TargetEvidenceSha256 ||
            !target.Value.Observation.AsMirrorRefs()
                .SequenceEqual(trusted.ExpectedTargetRefs))
            throw Refused("GitCheckpointMirrorTargetMismatch");

        var mirror = await ReadSourceMirrorAsync(plan, source,
            target.Value, workpackId, requireFresh: true, ct);
        if (mirror.SnapshotId != trusted.SourceSnapshotId ||
            mirror.SourceEvidenceSha256 != full.SourceEvidenceSha256)
            throw Refused("GitCheckpointMirrorSnapshotMismatch");

        // This read-side recheck is NOT a source-locked F-095 writer
        // admission; F-095 must redo the complete physical fsck and
        // evidence checks inside its own source-lock transaction.
        try
        {
            var rechecked = await refPreflight.ReadAsync(
                preparationId, planId, checkpointId, targetObservationId,
                sourceRepositoryId, expectedTargetEvidenceSha256, ct);
            if (rechecked.EvidenceVersion != trusted.EvidenceVersion ||
                rechecked.SourceSnapshotId != trusted.SourceSnapshotId ||
                rechecked.SourceFullRefProofSha256 !=
                    trusted.SourceFullRefProofSha256 ||
                rechecked.SourceCheckpointAttestationSha256 !=
                    trusted.SourceCheckpointAttestationSha256 ||
                rechecked.TargetEvidenceSha256 != trusted.TargetEvidenceSha256 ||
                !rechecked.ExpectedSourceRefs.SequenceEqual(
                    trusted.ExpectedSourceRefs) ||
                !rechecked.ExpectedTargetRefs.SequenceEqual(
                    trusted.ExpectedTargetRefs))
                throw Refused("GitCheckpointMirrorSourceTargetChanged");
        }
        catch (GitIncrementalCheckpointPreflightException)
        { throw Refused("GitCheckpointMirrorSourceTargetUnverified"); }

        return new(1, trusted, mirror, false, false, false, false);
    }

    private async Task<GitIncrementalSourceMirrorBinding> ReadSourceMirrorAsync(
        IncrementalOperationPlan plan, VerifiedSourceGitRefs source,
        (GitHubTargetRefEvidenceView View, GitHubRefObservation Observation) target,
        Guid workpackId, bool requireFresh, CancellationToken ct)
    {
        if (workpackId == Guid.Empty)
            throw Refused("GitAncestryLedgerWorkpackRequired");
        var mirrorId = GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
            source.SnapshotId, workpackId);
        var row = await db.GitSourceMirrorLifecycles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.MirrorId == mirrorId, ct)
            ?? throw Refused("GitAncestryLedgerMirrorNotVerified");
        var bundle = await db.GitSourceBundleEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkpackId == workpackId &&
                x.PlanId == plan.Id, ct)
            ?? throw Refused("GitAncestryLedgerBundleNotFound");
        var workpack = await db.GitTransferWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == workpackId &&
                x.PlanId == plan.Id, ct)
            ?? throw Refused("GitAncestryLedgerWorkpackInvalid");
        var reconciliation = await db.IncrementalGitRefReconciliations
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == bundle.ReconciliationId && x.PlanId == plan.Id, ct)
            ?? throw Refused("GitAncestryLedgerReconciliationInvalid");
        GitMirrorRefReconciliation compared;
        try
        {
            compared = GitMirrorRefReconciler.Compare(new(
                plan.Id, plan.ManifestSha256,
                source.SourceEvidenceSha256, target.View.EvidenceSha256,
                source.Refs, target.Observation.AsMirrorRefs()));
        }
        catch (GitMirrorRefReconciliationException)
        { throw Refused("GitAncestryLedgerReconciliationInvalid"); }
        GitMirrorRefComparison[] storedComparisons;
        try
        {
            storedComparisons = JsonSerializer.Deserialize<
                GitMirrorRefComparison[]>(reconciliation.ItemsJson)
                ?? throw new JsonException();
        }
        catch (JsonException)
        { throw Refused("GitAncestryLedgerReconciliationInvalid"); }

        var lastEvent = await db.GitSourceMirrorLifecycleEvents.AsNoTracking()
            .Where(x => x.MirrorId == mirrorId)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (row.State != GitSourceMirrorLifecycleStates.Verified ||
            row.SourceSnapshotId != source.SnapshotId ||
            row.PlanId != plan.Id || row.WorkpackId != workpackId ||
            row.LeaseGeneration < 1 || row.LeasedBy is not null ||
            row.LeaseToken is not null || row.LeaseExpiresAtUtc is not null ||
            row.FailureCode is not null ||
            row.ObjectSetSha256 is null || row.ObjectCount is null ||
            row.MirrorProofSha256 is null || row.VerifiedAtUtc is null ||
            row.ReconciliationId != bundle.ReconciliationId ||
            reconciliation.SnapshotId != source.SnapshotId ||
            reconciliation.SourceRepositoryId != source.SourceRepositoryId ||
            reconciliation.TargetRepositoryName != target.View.TargetRepositoryName ||
            reconciliation.TargetObservationId != target.View.Id ||
            reconciliation.ManifestSha256 != plan.ManifestSha256 ||
            reconciliation.SourceEvidenceSha256 != source.SourceEvidenceSha256 ||
            reconciliation.SourceManifestRootSha256 !=
                source.RefManifestRootSha256 ||
            reconciliation.TargetEvidenceSha256 != target.View.EvidenceSha256 ||
            reconciliation.ReconciliationSha256 != compared.ReconciliationSha256 ||
            reconciliation.SourceRefCount != compared.SourceRefCount ||
            reconciliation.TargetRefCount != compared.TargetRefCount ||
            reconciliation.MatchingCount != compared.MatchingCount ||
            reconciliation.CreateCandidateCount != compared.CreateCandidateCount ||
            reconciliation.DivergedCount != compared.DivergedCount ||
            reconciliation.TargetOnlyCount != compared.TargetOnlyCount ||
            !storedComparisons.SequenceEqual(compared.Items) ||
            reconciliation.ReadyForExecution ||
            workpack.ReconciliationId != reconciliation.Id ||
            workpack.SourceConnectionId != plan.SourceConnectionId ||
            workpack.SourceRepositoryId != source.SourceRepositoryId ||
            workpack.TargetRepositoryName != target.View.TargetRepositoryName ||
            workpack.ManifestSha256 != plan.ManifestSha256 ||
            workpack.TargetEvidenceSha256 != target.View.EvidenceSha256 ||
            workpack.ReconciliationSha256 != reconciliation.ReconciliationSha256 ||
            workpack.State != GitTransferWorkpackStates.Blocked ||
            workpack.Phase != GitTransferWorkpackPhases.WriteBlocked ||
            workpack.BlockedAtUtc is null ||
            workpack.LastErrorCode != "DestinationWritesNotAuthorized" ||
            workpack.ReadyForExecution ||
            row.BundleSha256 != bundle.BundleSha256 ||
            row.SourceEvidenceSha256 != bundle.SourceEvidenceSha256 ||
            row.ReconciliationId == Guid.Empty ||
            bundle.SourceRepositoryId != source.SourceRepositoryId ||
            bundle.ReconciliationSha256 != reconciliation.ReconciliationSha256 ||
            bundle.SourceEvidenceSha256 != source.SourceEvidenceSha256 ||
            bundle.BundleFileName != workpackId.ToString("N") + ".bundle" ||
            bundle.BundleBytes is < 1 or > 536_870_912 ||
            bundle.ReadyForExecution ||
            lastEvent is null || lastEvent.Type != "Verified" ||
            lastEvent.Generation != row.LeaseGeneration ||
            lastEvent.WorkerId == Guid.Empty ||
            lastEvent.OccurredAtUtc != row.UpdatedAtUtc)
            throw Refused("GitAncestryLedgerMirrorNotVerified");

        var bridge = new GitIncrementalSourceMirrorBinding(
            row.MirrorId, row.SourceSnapshotId, row.WorkpackId,
            row.PlanId, row.ReconciliationId, bundle.SourceRepositoryId,
            row.SourceEvidenceSha256, row.BundleSha256,
            bundle.RefCount, row.RefsSha256,
            row.ObjectSetSha256, row.ObjectCount.Value,
            row.MirrorProofSha256, row.VerifiedAtUtc.Value,
            row.State, row.RemotePushInvoked, row.RemoteWritesAuthorized,
            row.MayAutomaticallyRetry, row.MigrationComplete);
        GitIncrementalSourceMirrorBindingIntegrity.VerifySnapshotContext(
            plan, source, bridge, source.SnapshotId,
            clock.GetUtcNow(), requireFresh);
        return bridge;
    }

}
