using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// An immutable issuance ATTTESTATION, not a retained OAuth token and not a
// GitHub write authorization. Only inert digests and repository identity.
public sealed class GitHubAppWriteScopeRecord
{
    public Guid Id { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid FenceId { get; set; }
    public Guid WorkpackId { get; set; }
    public Guid IntentId { get; set; }
    public Guid RehearsalId { get; set; }
    public Guid DestinationConnectionId { get; set; }
    public string Organization { get; set; } = "";
    public string Repository { get; set; } = "";
    public long InstallationId { get; set; }
    public long RepositoryId { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string BundleSha256 { get; set; } = "";
    public string IntentEvidenceSha256 { get; set; } = "";
    public string RehearsalEvidenceSha256 { get; set; } = "";
    public string FenceEvidenceSha256 { get; set; } = "";
    public string RemoteRefFingerprintSha256 { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset TokenExpiresAtUtc { get; set; }
    public Guid ActorId { get; set; }
    public bool ScopedContentsWriteReported { get; set; }
    public bool WriteCapabilityVerified { get; set; }
    public bool ExecutionAuthorized { get; set; }
    public bool RemoteWritesPerformed { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class PostgresGitHubAppWriteScopeStore(
    AppDbContext db, TimeProvider clock,
    GitHubAppWriteCredentialOptions config,
    IGitHubRemoteRefFenceStore fences,
    IGitHubImportIntentStore intents,
    IGitHubImportReadinessStore readiness,
    IGitSourceBundleEvidenceStore bundles,
    IGitSourceBundleArtifactVerifier disk,
    IIncrementalOperationPlanRepository plans)
    : IGitHubAppWriteScopeStore
{
    public async Task<GitHubAppWriteScopeView> SaveAsync(
        GitHubAppWriteScopeInput input, CancellationToken ct)
    {
        var hash = GitHubAppWriteScopeIntegrity.Seal(input);
        var now = clock.GetUtcNow();
        var age = now - input.Observation.ObservedAtUtc;
        if (age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromSeconds(20) ||
            !config.Enabled || input.Observation.InstallationId != config.InstallationId ||
            !string.Equals(input.Organization + "/" + input.Repository,
                config.AllowedRepository, StringComparison.OrdinalIgnoreCase))
            throw new GitHubAppWriteScopeException("AppScopeObservationStaleOrUnconfigured");

        var parent = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == input.PreparationId &&
                x.Id == input.PlanId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({parent.SourceConnectionId.ToString()}), 732911)",
            ct);
        var fence = await fences.GetAsync(
            input.PreparationId, input.PlanId, input.FenceId, ct);
        var intent = await intents.GetAsync(
            input.PreparationId, input.PlanId, input.IntentId, ct);
        var review = intent is null ? null : await readiness.GetAsync(
            input.PreparationId, input.PlanId, intent.ImportReadinessId, ct);
        var plan = await plans.GetAsync(input.PreparationId, input.PlanId, ct);
        var bundle = await bundles.GetAsync(
            input.PreparationId, input.PlanId, input.WorkpackId, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == input.DestinationConnectionId, ct);
        if (fence is not { IsCurrent: true, Status: "RefSetUnchanged",
                WriteCapabilityVerified: false, ExecutionAuthorized: false,
                RemoteWritesPerformed: false, ReadyForExecution: false } ||
            intent is not { IsCurrent: true, IsExpired: false,
                State: GitHubImportIntentStates.Acknowledged } ||
            review is not { IsCurrent: true } ||
            plan is not { IsCurrent: true } ||
            bundle is not { IsCurrent: true, ReadyForExecution: false } ||
            destination is null ||
            destination.Id != plan.DestinationConnectionId ||
            destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            !string.Equals(destination.Organization, input.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            destination.SecretReference == config.PrivateKeySecretReference ||
            input.ActorId != intent.ReviewedBy ||
            input.ActorId == intent.RequestedBy ||
            plan.ManifestSha256 != input.ManifestSha256 ||
            review.ManifestSha256 != input.ManifestSha256 ||
            fence.ManifestSha256 != input.ManifestSha256 ||
            fence.WorkpackId != input.WorkpackId ||
            fence.IntentId != input.IntentId ||
            fence.RehearsalId != input.RehearsalId ||
            fence.EvidenceSha256 != input.FenceEvidenceSha256 ||
            fence.RemoteRefFingerprintSha256 != input.RemoteRefFingerprintSha256 ||
            fence.BundleSha256 != input.BundleSha256 ||
            fence.RehearsalEvidenceSha256 != input.RehearsalEvidenceSha256 ||
            intent.EvidenceSha256 != input.IntentEvidenceSha256 ||
            intent.BundleSha256 != input.BundleSha256 ||
            review.SourceBundleSha256 != input.BundleSha256 ||
            bundle.BundleSha256 != input.BundleSha256 ||
            !string.Equals(review.Organization, input.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(review.TargetRepositoryName, input.Repository,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubAppWriteScopeException("AppScopeLineageChanged");

        var physical = await disk.VerifyAsync(bundle, ct);
        if (!physical.IsCurrent || physical.ReadyForExecution ||
            physical.BundleSha256 != input.BundleSha256 ||
            physical.BundleBytes != bundle.BundleBytes)
            throw new GitHubAppWriteScopeException("AppScopeBundleChanged");

        var record = new GitHubAppWriteScopeRecord {
            Id = Guid.NewGuid(),
            PreparationId = input.PreparationId, PlanId = input.PlanId,
            FenceId = input.FenceId, WorkpackId = input.WorkpackId,
            IntentId = input.IntentId, RehearsalId = input.RehearsalId,
            DestinationConnectionId = input.DestinationConnectionId,
            Organization = input.Organization, Repository = input.Repository,
            InstallationId = input.Observation.InstallationId,
            RepositoryId = input.Observation.RepositoryId,
            ManifestSha256 = input.ManifestSha256,
            BundleSha256 = input.BundleSha256,
            IntentEvidenceSha256 = input.IntentEvidenceSha256,
            RehearsalEvidenceSha256 = input.RehearsalEvidenceSha256,
            FenceEvidenceSha256 = input.FenceEvidenceSha256,
            RemoteRefFingerprintSha256 = input.RemoteRefFingerprintSha256,
            EvidenceSha256 = hash, Status = input.Observation.Status,
            ObservedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                input.Observation.ObservedAtUtc.ToUnixTimeMilliseconds()),
            TokenExpiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                input.Observation.TokenExpiresAtUtc.ToUnixTimeMilliseconds()),
            ActorId = input.ActorId, ScopedContentsWriteReported = true
        };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_app_write_scopes (
                id, preparation_id, plan_id, fence_id, workpack_id,
                intent_id, rehearsal_id, destination_connection_id,
                organization, repository, installation_id, repository_id,
                manifest_sha256, bundle_sha256,
                intent_evidence_sha256, rehearsal_evidence_sha256,
                fence_evidence_sha256, remote_ref_fingerprint_sha256,
                evidence_sha256, status, observed_at_utc, token_expires_at_utc,
                actor_id, scoped_contents_write_reported,
                write_capability_verified, execution_authorized,
                remote_writes_performed, ready_for_execution)
            VALUES ({record.Id}, {record.PreparationId}, {record.PlanId},
                {record.FenceId}, {record.WorkpackId}, {record.IntentId},
                {record.RehearsalId}, {record.DestinationConnectionId},
                {record.Organization}, {record.Repository},
                {record.InstallationId}, {record.RepositoryId},
                {record.ManifestSha256}, {record.BundleSha256},
                {record.IntentEvidenceSha256}, {record.RehearsalEvidenceSha256},
                {record.FenceEvidenceSha256}, {record.RemoteRefFingerprintSha256},
                {record.EvidenceSha256}, {record.Status},
                {record.ObservedAtUtc}, {record.TokenExpiresAtUtc},
                {record.ActorId}, true, false, false, false, false)
            ON CONFLICT (fence_id) DO NOTHING
            """, ct);
        var stored = await db.GitHubAppWriteScopes.AsNoTracking()
            .SingleAsync(x => x.FenceId == input.FenceId, ct);
        // Minted tokens differ on concurrent requests and NEVER persist.
        // Matching repository, exact digests and actor replay the first
        // attestation. A changed scope or other approver must fail closed.
        if (stored.ActorId != input.ActorId ||
            stored.RepositoryId != input.Observation.RepositoryId ||
            stored.InstallationId != input.Observation.InstallationId ||
            stored.FenceEvidenceSha256 != input.FenceEvidenceSha256 ||
            stored.RemoteRefFingerprintSha256 != input.RemoteRefFingerprintSha256 ||
            !string.Equals(stored.Organization, input.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(stored.Repository, input.Repository,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubAppWriteScopeException("AppScopeConflictingReplay");
        await tx.CommitAsync(ct);
        return await ViewAsync(stored, ct);
    }

    public async Task<GitHubAppWriteScopeView?> GetAsync(
        Guid preparationId, Guid planId, Guid scopeId, CancellationToken ct)
    {
        var row = await db.GitHubAppWriteScopes.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId &&
                x.PlanId == planId && x.Id == scopeId, ct);
        return row is null ? null : await ViewAsync(row, ct);
    }

    public async Task<IReadOnlyList<GitHubAppWriteScopeView>> ListAsync(
        Guid preparationId, Guid planId, Guid fenceId, CancellationToken ct)
    {
        var rows = await db.GitHubAppWriteScopes.AsNoTracking()
            .Where(x => x.PreparationId == preparationId &&
                x.PlanId == planId && x.FenceId == fenceId)
            .OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Id)
            .Take(20).ToArrayAsync(ct);
        var result = new List<GitHubAppWriteScopeView>(rows.Length);
        foreach (var row in rows) result.Add(await ViewAsync(row, ct));
        return result;
    }

    private async Task<GitHubAppWriteScopeView> ViewAsync(
        GitHubAppWriteScopeRecord row, CancellationToken ct)
    {
        var input = new GitHubAppWriteScopeInput(row.PreparationId,
            row.PlanId, row.FenceId, row.WorkpackId, row.IntentId,
            row.RehearsalId, row.DestinationConnectionId,
            row.Organization, row.Repository, row.ManifestSha256,
            row.BundleSha256, row.IntentEvidenceSha256,
            row.RehearsalEvidenceSha256, row.FenceEvidenceSha256,
            row.RemoteRefFingerprintSha256,
            new(row.InstallationId, row.RepositoryId, row.Status,
                row.ObservedAtUtc, row.TokenExpiresAtUtc), row.ActorId);
        if (GitHubAppWriteScopeIntegrity.Seal(input) != row.EvidenceSha256 ||
            !row.ScopedContentsWriteReported || row.WriteCapabilityVerified ||
            row.ExecutionAuthorized || row.RemoteWritesPerformed ||
            row.ReadyForExecution)
            throw new GitHubAppWriteScopeException("AppScopeIntegrityFailed");
        var fence = await fences.GetAsync(row.PreparationId,
            row.PlanId, row.FenceId, ct);
        var intent = await intents.GetAsync(row.PreparationId,
            row.PlanId, row.IntentId, ct);
        if (fence is null || intent is null ||
            fence.EvidenceSha256 != row.FenceEvidenceSha256 ||
            fence.RemoteRefFingerprintSha256 != row.RemoteRefFingerprintSha256 ||
            intent.EvidenceSha256 != row.IntentEvidenceSha256)
            throw new GitHubAppWriteScopeException("AppScopeIntegrityFailed");
        var current = config.Enabled &&
            string.Equals(row.Organization + "/" + row.Repository,
                config.AllowedRepository, StringComparison.OrdinalIgnoreCase) &&
            row.InstallationId == config.InstallationId &&
            fence.IsCurrent && intent.IsCurrent && !intent.IsExpired &&
            row.TokenExpiresAtUtc > clock.GetUtcNow();
        // IsCurrent means the *historic issuance evidence* is fresh;
        // the minted token is already discarded, NEVER reusable.
        return new(row.Id, row.PreparationId, row.PlanId, row.FenceId,
            row.WorkpackId, row.IntentId, row.RehearsalId,
            row.Organization, row.Repository, row.RepositoryId,
            row.InstallationId, row.ManifestSha256, row.BundleSha256,
            row.IntentEvidenceSha256, row.RehearsalEvidenceSha256,
            row.FenceEvidenceSha256, row.RemoteRefFingerprintSha256,
            row.EvidenceSha256, row.Status,
            row.ObservedAtUtc, row.TokenExpiresAtUtc, row.ActorId,
            current, true, false, false, false, false);
    }
}
