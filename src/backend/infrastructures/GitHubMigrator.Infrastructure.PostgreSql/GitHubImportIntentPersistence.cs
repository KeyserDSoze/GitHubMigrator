using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// Human intent is NOT an execution permit. Permanent SQL CHECK constraints
// deny all destination-write flags, irrespective of the UI or API.
public sealed class GitHubImportIntentRecord
{
    public Guid Id { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid WorkpackId { get; set; }
    public Guid ReadinessId { get; set; }
    public string ReadinessSha256 { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string ReconciliationSha256 { get; set; } = "";
    public string BundleSha256 { get; set; } = "";
    public string SafeRefPlanSha256 { get; set; } = "";
    public string PolicySha256 { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public int CreateCandidates { get; set; }
    public int AlreadyMatching { get; set; }
    public int DivergedBlocked { get; set; }
    public int TargetOnlyPreserved { get; set; }
    public string RulesetStatus { get; set; } = "";
    public int RulesetCount { get; set; }
    public int ActiveRulesets { get; set; }
    public string DefaultBranchProtection { get; set; } = "";
    public DateTimeOffset PolicyObservedAtUtc { get; set; }
    public string State { get; set; } = GitHubImportIntentStates.Pending;
    public Guid RequestedBy { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public bool WriteCapabilityVerified { get; set; }
    public bool ExecutionAuthorized { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class GitHubImportIntentEventRecord
{
    public long Id { get; set; }
    public Guid IntentId { get; set; }
    public string Kind { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public static class GitHubImportIntentIntegrity
{
    public static string Seal(GitHubImportIntentDraft draft)
    {
        if (draft.RequestedBy == Guid.Empty || draft.ImportReadinessId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(draft.ExpectedReadinessSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(draft.ManifestSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(draft.ReconciliationSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(draft.BundleSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(draft.Plan.EvidenceSha256) ||
            draft.Plan.CreateCandidates < 1 || draft.Plan.AlreadyMatching < 0 ||
            draft.Plan.DivergedBlocked != 0 ||
            draft.Plan.TargetOnlyPreserved < 0 ||
            !draft.Plan.HasOnlyNonDestructiveCandidates ||
            draft.Policy.RulesetCount is < 0 or > 100 ||
            draft.Policy.ActiveRulesets < 0 ||
            draft.Policy.ActiveRulesets > draft.Policy.RulesetCount ||
            draft.Policy.RulesetStatus is not
                ("Observed" or "Unavailable" or "Forbidden" or "Incomplete") ||
            draft.Policy.DefaultBranchProtection is not
                ("Unverified" or "ProtectionVisible" or "NotVisibleOrMissing" or
                 "Forbidden" or "Unauthorized" or "RateLimited") ||
            draft.Policy.ObservedAtUtc == default)
            throw new GitHubImportIntentException("ImportIntentIntegrityInvalid");
        return Hash(new object?[] {
            "github-import-intent-v1", draft.PreparationId.ToString("D"),
            draft.PlanId.ToString("D"), draft.WorkpackId.ToString("D"),
            draft.ImportReadinessId.ToString("D"),
            draft.ExpectedReadinessSha256, draft.ManifestSha256,
            draft.ReconciliationSha256, draft.BundleSha256,
            draft.Plan.EvidenceSha256, draft.Plan.CreateCandidates,
            draft.Plan.AlreadyMatching, draft.Plan.DivergedBlocked,
            draft.Plan.TargetOnlyPreserved, draft.Policy.RulesetStatus,
            draft.Policy.RulesetCount, draft.Policy.ActiveRulesets,
            draft.Policy.DefaultBranchProtection,
            draft.Policy.ObservedAtUtc.ToUnixTimeMilliseconds(),
            draft.RequestedBy.ToString("D")
        });
    }

    public static string PolicyHash(GitHubImportPolicyObservation policy) =>
        Hash(new object?[] { "github-import-policy-v1", policy.RulesetStatus,
            policy.RulesetCount, policy.ActiveRulesets,
            policy.DefaultBranchProtection,
            policy.ObservedAtUtc.ToUnixTimeMilliseconds() });

    private static string Hash(object?[] value) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}

public sealed class PostgresGitHubImportIntentStore(
    AppDbContext db, TimeProvider clock,
    IGitHubImportReadinessStore readiness,
    IGitTransferWorkpackRepository workpacks,
    IGitSourceBundleEvidenceStore bundles,
    IGitSourceBundleArtifactVerifier disk,
    IIncrementalGitRefReconciliationStore reconciliations,
    IIncrementalOperationPlanRepository plans)
    : IGitHubImportIntentStore
{
    public async Task<GitHubImportIntentView> CreateAsync(
        GitHubImportIntentDraft draft, CancellationToken ct)
    {
        var hash = GitHubImportIntentIntegrity.Seal(draft);
        var planRow = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == draft.PlanId &&
                x.PreparationId == draft.PreparationId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        var now = clock.GetUtcNow();
        var age = now - draft.Policy.ObservedAtUtc;
        if (age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromMinutes(2))
            throw new GitHubImportIntentException("ImportIntentPolicyStale");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSourceAsync(planRow.SourceConnectionId, ct);
        await VerifyCurrentAsync(draft, ct);

        var row = new GitHubImportIntentRecord {
            Id = Guid.NewGuid(), PreparationId = draft.PreparationId,
            PlanId = draft.PlanId, WorkpackId = draft.WorkpackId,
            ReadinessId = draft.ImportReadinessId,
            ReadinessSha256 = draft.ExpectedReadinessSha256,
            ManifestSha256 = draft.ManifestSha256,
            ReconciliationSha256 = draft.ReconciliationSha256,
            BundleSha256 = draft.BundleSha256,
            SafeRefPlanSha256 = draft.Plan.EvidenceSha256,
            PolicySha256 = GitHubImportIntentIntegrity.PolicyHash(draft.Policy),
            EvidenceSha256 = hash,
            CreateCandidates = draft.Plan.CreateCandidates,
            AlreadyMatching = draft.Plan.AlreadyMatching,
            DivergedBlocked = draft.Plan.DivergedBlocked,
            TargetOnlyPreserved = draft.Plan.TargetOnlyPreserved,
            RulesetStatus = draft.Policy.RulesetStatus,
            RulesetCount = draft.Policy.RulesetCount,
            ActiveRulesets = draft.Policy.ActiveRulesets,
            DefaultBranchProtection = draft.Policy.DefaultBranchProtection,
            PolicyObservedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                draft.Policy.ObservedAtUtc.ToUnixTimeMilliseconds()),
            State = GitHubImportIntentStates.Pending,
            RequestedBy = draft.RequestedBy, CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(GitHubImportIntentService.IntentTtl)
        };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH made AS (
                INSERT INTO github_import_intents (
                    id, preparation_id, plan_id, workpack_id, readiness_id,
                    readiness_sha256, manifest_sha256, reconciliation_sha256,
                    bundle_sha256, safe_ref_plan_sha256, policy_sha256,
                    evidence_sha256, create_candidates, already_matching,
                    diverged_blocked, target_only_preserved, ruleset_status,
                    ruleset_count, active_rulesets, default_branch_protection,
                    policy_observed_at_utc, state, requested_by, reviewed_by,
                    created_at_utc, expires_at_utc, reviewed_at_utc,
                    write_capability_verified, execution_authorized, ready_for_execution)
                VALUES ({row.Id}, {row.PreparationId}, {row.PlanId},
                    {row.WorkpackId}, {row.ReadinessId}, {row.ReadinessSha256},
                    {row.ManifestSha256}, {row.ReconciliationSha256},
                    {row.BundleSha256}, {row.SafeRefPlanSha256}, {row.PolicySha256},
                    {row.EvidenceSha256}, {row.CreateCandidates},
                    {row.AlreadyMatching}, {row.DivergedBlocked},
                    {row.TargetOnlyPreserved}, {row.RulesetStatus},
                    {row.RulesetCount}, {row.ActiveRulesets},
                    {row.DefaultBranchProtection}, {row.PolicyObservedAtUtc},
                    'Pending', {row.RequestedBy}, NULL, {row.CreatedAtUtc},
                    {row.ExpiresAtUtc}, NULL, false, false, false)
                ON CONFLICT (readiness_id) DO NOTHING
                RETURNING id, requested_by, created_at_utc
            )
            INSERT INTO github_import_intent_events (
                intent_id, kind, actor_id, occurred_at_utc)
            SELECT id, 'Requested', requested_by, created_at_utc FROM made
            """, ct);
        var saved = await db.GitHubImportIntents.AsNoTracking()
            .SingleAsync(x => x.ReadinessId == row.ReadinessId, ct);
        if (saved.ReadinessSha256 != draft.ExpectedReadinessSha256 ||
            saved.RequestedBy != draft.RequestedBy ||
            saved.ManifestSha256 != draft.ManifestSha256 ||
            saved.ReconciliationSha256 != draft.ReconciliationSha256 ||
            saved.BundleSha256 != draft.BundleSha256 ||
            saved.SafeRefPlanSha256 != draft.Plan.EvidenceSha256 ||
            saved.CreateCandidates != draft.Plan.CreateCandidates ||
            saved.AlreadyMatching != draft.Plan.AlreadyMatching ||
            saved.DivergedBlocked != draft.Plan.DivergedBlocked ||
            saved.TargetOnlyPreserved != draft.Plan.TargetOnlyPreserved ||
            saved.RulesetStatus != draft.Policy.RulesetStatus ||
            saved.RulesetCount != draft.Policy.RulesetCount ||
            saved.ActiveRulesets != draft.Policy.ActiveRulesets ||
            saved.DefaultBranchProtection != draft.Policy.DefaultBranchProtection ||
            saved.ExpiresAtUtc <= now)
            throw new GitHubImportIntentException("ImportIntentConflictingReplay");
        // The originally observed policy time is immutable. A concurrent
        // idempotent HTTP retry may see an IDENTICAL policy seconds later;
        // preserving the original SHA is safe, whereas a changed ruleset
        // disposition or a different requester still fails closed.
        _ = hash;
        await tx.CommitAsync(ct);
        return await ViewAsync(saved, ct);
    }

    public async Task<GitHubImportIntentView> DecideAsync(
        Guid preparationId, Guid planId, Guid intentId, Guid actorId,
        string expectedReadinessSha256, string decision, CancellationToken ct)
    {
        var candidate = await db.GitHubImportIntents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == intentId &&
                x.PreparationId == preparationId && x.PlanId == planId, ct)
            ?? throw new KeyNotFoundException("ImportIntentNotFound");
        var planRow = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleAsync(x => x.Id == planId && x.PreparationId == preparationId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSourceAsync(planRow.SourceConnectionId, ct);
        var row = await db.GitHubImportIntents.AsNoTracking()
            .SingleAsync(x => x.Id == intentId, ct);
        var now = clock.GetUtcNow();
        if (row.State != GitHubImportIntentStates.Pending ||
            row.RequestedBy == actorId || row.ExpiresAtUtc <= now ||
            row.ReadinessSha256 != expectedReadinessSha256)
            throw new GitHubImportIntentException("ImportIntentDecisionNotAllowed");

        var draft = AsDraft(row);
        await VerifyCurrentAsync(draft, ct);
        // Acknowledgment is an intent review, NOT proof of effective
        // Contents:write, branch rules or execution authority.
        if (decision == "Acknowledge" &&
            row.RulesetStatus != "Observed")
            throw new GitHubImportIntentException("ImportIntentPolicyNotObserved");

        var next = decision == "Acknowledge"
            ? GitHubImportIntentStates.Acknowledged : GitHubImportIntentStates.Rejected;
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH acted AS (
                UPDATE github_import_intents
                SET state = {next}, reviewed_by = {actorId},
                    reviewed_at_utc = {now}
                WHERE id = {intentId} AND state = 'Pending'
                    AND requested_by <> {actorId}
                    AND readiness_sha256 = {expectedReadinessSha256}
                    AND expires_at_utc > {now}
                RETURNING id, reviewed_by, reviewed_at_utc
            )
            INSERT INTO github_import_intent_events (
                intent_id, kind, actor_id, occurred_at_utc)
            SELECT id, {next}, reviewed_by, reviewed_at_utc FROM acted
            """, ct);
        if (changed != 1)
            throw new GitHubImportIntentException("ImportIntentDecisionLostRace");
        await tx.CommitAsync(ct);
        return await GetAsync(preparationId, planId, intentId, ct)
            ?? throw new GitHubImportIntentException("ImportIntentIntegrityFailed");
    }

    public async Task<GitHubImportIntentView?> GetAsync(
        Guid preparationId, Guid planId, Guid intentId, CancellationToken ct)
    {
        var row = await db.GitHubImportIntents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == intentId &&
                x.PlanId == planId && x.PreparationId == preparationId, ct);
        return row is null ? null : await ViewAsync(row, ct);
    }

    public async Task<IReadOnlyList<GitHubImportIntentView>> ListAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct)
    {
        var rows = await db.GitHubImportIntents.AsNoTracking()
            .Where(x => x.PreparationId == preparationId &&
                x.PlanId == planId && x.WorkpackId == workpackId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Take(20).ToArrayAsync(ct);
        var result = new List<GitHubImportIntentView>(rows.Length);
        foreach (var row in rows) result.Add(await ViewAsync(row, ct));
        return result;
    }

    public async Task<IReadOnlyList<GitHubImportIntentEvent>> EventsAsync(
        Guid preparationId, Guid planId, Guid intentId, CancellationToken ct)
    {
        if (await GetAsync(preparationId, planId, intentId, ct) is null)
            throw new KeyNotFoundException("ImportIntentNotFound");
        return await db.GitHubImportIntentEvents.AsNoTracking()
            .Where(x => x.IntentId == intentId)
            .OrderBy(x => x.Id)
            .Take(100)
            .Select(x => new GitHubImportIntentEvent(
                x.Id, x.IntentId, x.Kind, x.ActorId, x.OccurredAtUtc))
            .ToArrayAsync(ct);
    }

    private async Task VerifyCurrentAsync(
        GitHubImportIntentDraft draft, CancellationToken ct)
    {
        var review = await readiness.GetAsync(
            draft.PreparationId, draft.PlanId, draft.ImportReadinessId, ct);
        var plan = await plans.GetAsync(draft.PreparationId, draft.PlanId, ct);
        var pack = await workpacks.GetAsync(
            draft.PreparationId, draft.PlanId, draft.WorkpackId, ct);
        var bundle = await bundles.GetAsync(
            draft.PreparationId, draft.PlanId, draft.WorkpackId, ct);
        if (review is not { IsCurrent: true, RepositoryStatus: "Visible",
                ReportedPush: "ReportedAllowed", Archived: false, Disabled: false } ||
            plan is not { IsCurrent: true } ||
            pack is not { IsCurrent: true, State: GitTransferWorkpackStates.Blocked,
                Phase: GitTransferWorkpackPhases.WriteBlocked } ||
            bundle is not { IsCurrent: true, ReadyForExecution: false } ||
            review.WorkpackId != draft.WorkpackId ||
            review.EvidenceSha256 != draft.ExpectedReadinessSha256 ||
            review.ManifestSha256 != draft.ManifestSha256 ||
            review.ReconciliationSha256 != draft.ReconciliationSha256 ||
            review.SourceBundleSha256 != draft.BundleSha256 ||
            plan.ManifestSha256 != draft.ManifestSha256 ||
            pack.ManifestSha256 != draft.ManifestSha256 ||
            pack.ReconciliationSha256 != draft.ReconciliationSha256 ||
            bundle.BundleSha256 != draft.BundleSha256)
            throw new GitHubImportIntentException("ImportIntentProofNotCurrent");
        var physicallyVerified = await disk.VerifyAsync(bundle, ct);
        if (!physicallyVerified.IsCurrent ||
            physicallyVerified.BundleSha256 != draft.BundleSha256 ||
            physicallyVerified.ReadyForExecution)
            throw new GitHubImportIntentException("ImportIntentBundleNotVerified");

        var current = await GitHubImportIntentService.BuildVerifiedPlanAsync(
            reconciliations, draft.PreparationId, draft.PlanId,
            review.ReconciliationId, review, ct);
        if (current != draft.Plan || !current.HasOnlyNonDestructiveCandidates ||
            current.CreateCandidates < 1)
            throw new GitHubImportIntentException("ImportIntentSafeRefPlanChanged");
    }

    private static GitHubImportIntentDraft AsDraft(GitHubImportIntentRecord row) =>
        new(row.PreparationId, row.PlanId, row.WorkpackId, row.ReadinessId,
            row.ReadinessSha256, row.ManifestSha256, row.ReconciliationSha256,
            row.BundleSha256, new(row.SafeRefPlanSha256, row.CreateCandidates,
                row.AlreadyMatching, row.DivergedBlocked, row.TargetOnlyPreserved,
                row.DivergedBlocked == 0),
            new(row.RulesetStatus, row.RulesetCount, row.ActiveRulesets,
                row.DefaultBranchProtection, row.PolicyObservedAtUtc),
            row.RequestedBy);

    private async Task<GitHubImportIntentView> ViewAsync(
        GitHubImportIntentRecord row, CancellationToken ct)
    {
        var draft = AsDraft(row);
        if (row.EvidenceSha256 != GitHubImportIntentIntegrity.Seal(draft) ||
            row.PolicySha256 != GitHubImportIntentIntegrity.PolicyHash(draft.Policy) ||
            row.WriteCapabilityVerified || row.ExecutionAuthorized || row.ReadyForExecution ||
            row.State is not ("Pending" or "Acknowledged" or "Rejected") ||
            (row.ReviewedBy is null) != (row.State == "Pending") ||
            (row.ReviewedAtUtc is null) != (row.State == "Pending") ||
            row.ReviewedBy == row.RequestedBy ||
            row.ExpiresAtUtc != row.CreatedAtUtc.Add(GitHubImportIntentService.IntentTtl))
            throw new GitHubImportIntentException("ImportIntentIntegrityFailed");
        var review = await readiness.GetAsync(
            row.PreparationId, row.PlanId, row.ReadinessId, ct)
            ?? throw new GitHubImportIntentException("ImportIntentIntegrityFailed");
        if (review.WorkpackId != row.WorkpackId ||
            review.EvidenceSha256 != row.ReadinessSha256 ||
            review.ManifestSha256 != row.ManifestSha256 ||
            review.SourceBundleSha256 != row.BundleSha256 ||
            review.ReconciliationSha256 != row.ReconciliationSha256)
            throw new GitHubImportIntentException("ImportIntentIntegrityFailed");

        var now = clock.GetUtcNow();
        return new(row.Id, row.PreparationId, row.PlanId, row.WorkpackId,
            row.ReadinessId, row.ReadinessSha256, row.ManifestSha256,
            row.ReconciliationSha256, row.BundleSha256,
            row.SafeRefPlanSha256, row.PolicySha256,
            row.CreateCandidates, row.AlreadyMatching, row.DivergedBlocked,
            row.TargetOnlyPreserved, row.RulesetStatus, row.RulesetCount,
            row.ActiveRulesets, row.DefaultBranchProtection, row.State,
            row.RequestedBy, row.ReviewedBy, row.CreatedAtUtc, row.ExpiresAtUtc,
            row.ReviewedAtUtc, review.IsCurrent,
            row.ExpiresAtUtc <= now, false, false, false);
    }

    private Task LockSourceAsync(Guid sourceId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({sourceId.ToString()}), 732911)", ct);
}
