using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// This is intentionally NOT an execution outbox. No credentials, Git
// arguments or writable target configuration are stored.
public sealed class GitTransferWorkpackRecord
{
    public Guid Id { get; set; }
    public Guid ReconciliationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid SourceConnectionId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string TargetRepositoryName { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string ReconciliationSha256 { get; set; } = "";
    public string TargetEvidenceSha256 { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public string State { get; set; } = GitTransferWorkpackStates.Queued;
    public string Phase { get; set; } = GitTransferWorkpackPhases.EvidencePending;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTimeOffset AvailableAtUtc { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? BlockedAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class GitTransferWorkpackEventRecord
{
    public long Id { get; set; }
    public Guid WorkpackId { get; set; }
    public string Type { get; set; } = "";
    public string Phase { get; set; } = "";
    public int Attempt { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class PostgresGitTransferWorkpackRepository(
    AppDbContext db, TimeProvider clock,
    PostgresIncrementalGitRefReconciliationStore comparisons)
    : IGitTransferWorkpackRepository
{
    public async Task<GitTransferWorkpackView> EnqueueAsync(
        Guid preparationId, Guid planId, Guid reconciliationId,
        string expectedManifestSha256, string expectedReconciliationSha256,
        Guid actorId, CancellationToken ct)
    {
        var parent = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == planId &&
                x.PreparationId == preparationId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSourceAsync(parent.SourceConnectionId, ct);
        var verified = await comparisons.PageAsync(
            preparationId, planId, reconciliationId, null, 1, ct)
            ?? throw new KeyNotFoundException("RefReconciliationNotFound");
        var result = verified.Reconciliation;
        if (!result.IsCurrent || result.ManifestSha256 != expectedManifestSha256 ||
            result.ReconciliationSha256 != expectedReconciliationSha256)
            throw new GitTransferWorkpackException("TransferEvidenceNotCurrent");

        // A successful F-032 comparison does not convey GitHub write scopes.
        // We queue only a bounded *audit workpack* which will hit a hard
        // durable write barrier, never a process/task that can push.
        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH created AS (
                INSERT INTO git_transfer_workpacks (
                    id, reconciliation_id, plan_id, source_connection_id,
                    source_repository_id, target_repository_name,
                    manifest_sha256, reconciliation_sha256, target_evidence_sha256,
                    created_by, state, phase, attempts, max_attempts,
                    available_at_utc, lease_token, lease_until_utc,
                    created_at_utc, updated_at_utc, blocked_at_utc,
                    last_error_code, ready_for_execution)
                VALUES (
                    {id}, {reconciliationId}, {planId}, {parent.SourceConnectionId},
                    {result.SourceRepositoryId}, {result.TargetRepositoryName},
                    {result.ManifestSha256}, {result.ReconciliationSha256},
                    {result.TargetEvidenceSha256}, {actorId}, 'Queued',
                    'EvidencePending', 0, 5, {now}, NULL, NULL,
                    {now}, {now}, NULL, NULL, false)
                ON CONFLICT (reconciliation_id) DO NOTHING RETURNING id, phase
            )
            INSERT INTO git_transfer_workpack_events (
                workpack_id, type, phase, attempt, occurred_at_utc, error_code)
            SELECT id, 'Queued', phase, 0, {now}, NULL FROM created
            """, ct);
        var saved = await db.GitTransferWorkpacks.AsNoTracking()
            .SingleAsync(x => x.ReconciliationId == reconciliationId, ct);
        if (saved.ManifestSha256 != expectedManifestSha256 ||
            saved.ReconciliationSha256 != expectedReconciliationSha256 ||
            saved.TargetEvidenceSha256 != result.TargetEvidenceSha256 ||
            saved.SourceConnectionId != parent.SourceConnectionId ||
            saved.PlanId != planId)
            throw new GitTransferWorkpackException("TransferWorkpackProofMismatch");
        await tx.CommitAsync(ct);
        return await ViewAsync(saved, preparationId, ct);
    }

    public async Task<GitTransferWorkpackView?> GetAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct)
    {
        var row = await db.GitTransferWorkpacks.AsNoTracking()
            .Join(db.IncrementalOperationPlans.AsNoTracking(),
                pack => pack.PlanId, plan => plan.Id, (pack, plan) => new { pack, plan })
            .Where(x => x.plan.PreparationId == preparationId &&
                x.pack.PlanId == planId && x.pack.Id == workpackId)
            .Select(x => x.pack).SingleOrDefaultAsync(ct);
        return row is null ? null : await ViewAsync(row, preparationId, ct);
    }

    public async Task<IReadOnlyList<GitTransferWorkpackView>> ListAsync(
        Guid preparationId, Guid planId, CancellationToken ct)
    {
        var rows = await db.GitTransferWorkpacks.AsNoTracking()
            .Join(db.IncrementalOperationPlans.AsNoTracking(),
                pack => pack.PlanId, plan => plan.Id, (pack, plan) => new { pack, plan })
            .Where(x => x.plan.PreparationId == preparationId &&
                x.pack.PlanId == planId)
            .OrderByDescending(x => x.pack.CreatedAtUtc).ThenByDescending(x => x.pack.Id)
            .Take(20).Select(x => x.pack).ToArrayAsync(ct);
        var views = new List<GitTransferWorkpackView>(rows.Length);
        foreach (var row in rows) views.Add(await ViewAsync(row, preparationId, ct));
        return views;
    }

    public async Task<IReadOnlyList<GitTransferWorkpackEvent>> EventsAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct)
    {
        if (await GetAsync(preparationId, planId, workpackId, ct) is null)
            throw new KeyNotFoundException("TransferWorkpackNotFound");
        var rows = await db.GitTransferWorkpackEvents.AsNoTracking()
            .Where(x => x.WorkpackId == workpackId)
            .OrderByDescending(x => x.Id).Take(100).ToArrayAsync(ct);
        return rows.OrderBy(x => x.Id)
            .Select(x => new GitTransferWorkpackEvent(
                x.Id, x.WorkpackId, x.Type, x.Phase, x.Attempt,
                x.OccurredAtUtc, x.ErrorCode)).ToArray();
    }

    public async Task<GitTransferWorkpackLease?> ClaimAsync(
        Guid leaseToken, TimeSpan leaseDuration, CancellationToken ct)
    {
        if (leaseToken == Guid.Empty || leaseDuration <= TimeSpan.Zero ||
            leaseDuration > TimeSpan.FromMinutes(2))
            throw new GitTransferWorkpackException("InvalidTransferLease");
        var now = clock.GetUtcNow();
        var until = now.Add(leaseDuration);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // An expired final attempt cannot remain a zombie Running job.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH dead AS (
                UPDATE git_transfer_workpacks SET state = 'Failed',
                    last_error_code = 'TransferLeaseAttemptsExhausted',
                    lease_token = NULL, lease_until_utc = NULL, updated_at_utc = {now}
                WHERE state = 'Running' AND lease_until_utc <= {now}
                  AND attempts >= max_attempts
                RETURNING id, phase, attempts
            )
            INSERT INTO git_transfer_workpack_events (
                workpack_id, type, phase, attempt, occurred_at_utc, error_code)
            SELECT id, 'Failed', phase, attempts, {now},
                   'TransferLeaseAttemptsExhausted' FROM dead
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH next AS (
                SELECT id FROM git_transfer_workpacks
                WHERE ((state = 'Queued' AND available_at_utc <= {now})
                    OR (state = 'Running' AND lease_until_utc <= {now}))
                  AND attempts < max_attempts
                ORDER BY available_at_utc, created_at_utc, id
                FOR UPDATE SKIP LOCKED LIMIT 1
            ), claimed AS (
                UPDATE git_transfer_workpacks w
                SET state = 'Running', lease_token = {leaseToken},
                    lease_until_utc = {until}, attempts = attempts + 1,
                    updated_at_utc = {now}
                FROM next WHERE w.id = next.id
                RETURNING w.id, w.phase, w.attempts
            )
            INSERT INTO git_transfer_workpack_events (
                workpack_id, type, phase, attempt, occurred_at_utc, error_code)
            SELECT id, 'Claimed', phase, attempts, {now}, NULL FROM claimed
            """, ct);
        var row = await db.GitTransferWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.LeaseToken == leaseToken, ct);
        await tx.CommitAsync(ct);
        return row is null ? null : new(row.Id, row.ReconciliationId,
            row.PlanId, leaseToken, row.Attempts,
            row.ReconciliationSha256, row.Phase);
    }

    public async Task<bool> CheckpointEvidenceAsync(
        GitTransferWorkpackLease lease, CancellationToken ct)
    {
        if (lease.Phase != GitTransferWorkpackPhases.EvidencePending)
            throw new GitTransferWorkpackException("InvalidTransferCheckpoint");
        return await ProgressAsync(lease, "EvidencePending", "EvidenceChecked",
            "EvidenceChecked", false, ct);
    }

    public async Task<bool> BlockAtWriteBarrierAsync(
        GitTransferWorkpackLease lease, CancellationToken ct) =>
        await ProgressAsync(lease, "EvidenceChecked", "WriteBlocked",
            "WriteBlocked", true, ct);

    private async Task<bool> ProgressAsync(GitTransferWorkpackLease lease,
        string expectedPhase, string nextPhase, string eventType,
        bool terminal, CancellationToken ct)
    {
        var row = await db.GitTransferWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == lease.Id, ct);
        if (row is null || row.ReconciliationId != lease.ReconciliationId ||
            row.PlanId != lease.PlanId || row.ReconciliationSha256 != lease.ReconciliationSha256)
            return false;
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSourceAsync(row.SourceConnectionId, ct);
        if (!await ValidCurrentProofAsync(row, ct))
            throw new GitTransferWorkpackException("TransferEvidenceStale");

        // INSERT of the journal event is derived from UPDATE RETURNING; an
        // expired/previous worker can never append a successful checkpoint.
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH changed AS (
                UPDATE git_transfer_workpacks
                SET phase = {nextPhase},
                    state = CASE WHEN {terminal} THEN 'Blocked' ELSE 'Running' END,
                    blocked_at_utc = CASE WHEN {terminal} THEN {now} ELSE NULL END,
                    last_error_code = CASE WHEN {terminal}
                        THEN 'DestinationWritesNotAuthorized' ELSE NULL END,
                    lease_token = CASE WHEN {terminal} THEN NULL ELSE lease_token END,
                    lease_until_utc = CASE WHEN {terminal} THEN NULL ELSE lease_until_utc END,
                    updated_at_utc = {now}
                WHERE id = {lease.Id} AND reconciliation_id = {lease.ReconciliationId}
                  AND reconciliation_sha256 = {lease.ReconciliationSha256}
                  AND state = 'Running' AND phase = {expectedPhase}
                  AND attempts = {lease.Attempt} AND lease_token = {lease.LeaseToken}
                  AND lease_until_utc > {now}
                RETURNING id, phase, attempts
            )
            INSERT INTO git_transfer_workpack_events (
                workpack_id, type, phase, attempt, occurred_at_utc, error_code)
            SELECT id, {eventType}, phase, attempts, {now},
                CASE WHEN {terminal} THEN 'DestinationWritesNotAuthorized' ELSE NULL END
            FROM changed
            """, ct);
        await tx.CommitAsync(ct);
        return affected == 1;
    }

    public async Task<bool> FailAsync(GitTransferWorkpackLease lease,
        string safeCode, bool permanent, CancellationToken ct)
    {
        if (safeCode.Length is < 1 or > 70 ||
            safeCode.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new GitTransferWorkpackException("UnsafeTransferErrorCode");
        var now = clock.GetUtcNow();
        var seconds = Math.Min(300, 5 * (1 << Math.Min(lease.Attempt, 6)));
        var available = now.AddSeconds(seconds);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH changed AS (
                UPDATE git_transfer_workpacks
                SET state = CASE WHEN {permanent} OR attempts >= max_attempts
                        THEN 'Failed' ELSE 'Queued' END,
                    last_error_code = {safeCode},
                    available_at_utc = CASE WHEN {permanent} OR attempts >= max_attempts
                        THEN available_at_utc ELSE {available} END,
                    lease_token = NULL, lease_until_utc = NULL,
                    updated_at_utc = {now}
                WHERE id = {lease.Id} AND state = 'Running'
                  AND attempts = {lease.Attempt} AND lease_token = {lease.LeaseToken}
                  AND lease_until_utc > {now}
                RETURNING id, phase, state, attempts
            )
            INSERT INTO git_transfer_workpack_events (
                workpack_id, type, phase, attempt, occurred_at_utc, error_code)
            SELECT id,
                CASE WHEN state = 'Failed' THEN 'Failed' ELSE 'RetryScheduled' END,
                phase, attempts, {now}, {safeCode} FROM changed
            """, ct);
        return affected == 1;
    }

    private async Task<bool> ValidCurrentProofAsync(
        GitTransferWorkpackRecord row, CancellationToken ct)
    {
        var parent = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == row.PlanId, ct);
        if (parent is null || parent.SourceConnectionId != row.SourceConnectionId ||
            parent.ManifestSha256 != row.ManifestSha256)
            return false;
        var page = await comparisons.PageAsync(
            parent.PreparationId, row.PlanId, row.ReconciliationId, null, 1, ct);
        var evidence = page?.Reconciliation;
        return evidence is { IsCurrent: true, ReadyForExecution: false } &&
            evidence.ManifestSha256 == row.ManifestSha256 &&
            evidence.ReconciliationSha256 == row.ReconciliationSha256 &&
            evidence.TargetEvidenceSha256 == row.TargetEvidenceSha256 &&
            evidence.SourceRepositoryId == row.SourceRepositoryId &&
            evidence.TargetRepositoryName == row.TargetRepositoryName;
    }

    private async Task<GitTransferWorkpackView> ViewAsync(
        GitTransferWorkpackRecord row, Guid preparationId, CancellationToken ct)
    {
        if (row.ReadyForExecution ||
            row.State is not ("Queued" or "Running" or "Blocked" or "Failed") ||
            row.Phase is not ("EvidencePending" or "EvidenceChecked" or "WriteBlocked") ||
            row.State == "Blocked" && row.Phase != "WriteBlocked" ||
            row.State != "Blocked" && row.Phase == "WriteBlocked")
            throw new GitTransferWorkpackException("TransferWorkpackIntegrityFailed");

        // A previously blocked workpack remains visible historically while
        // fresh planning/reconciliation is required before any *future* work.
        var plan = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == row.PlanId &&
                x.PreparationId == preparationId, ct)
            ?? throw new GitTransferWorkpackException("TransferWorkpackIntegrityFailed");
        if (plan.SourceConnectionId != row.SourceConnectionId ||
            plan.ManifestSha256 != row.ManifestSha256)
            throw new GitTransferWorkpackException("TransferWorkpackIntegrityFailed");

        bool current;
        try { current = await ValidCurrentProofAsync(row, ct); }
        catch (IncrementalGitRefReconciliationException)
        { throw new GitTransferWorkpackException("TransferWorkpackIntegrityFailed"); }
        return new(row.Id, row.ReconciliationId, row.PlanId, row.SourceRepositoryId,
            row.TargetRepositoryName, row.ManifestSha256, row.ReconciliationSha256,
            row.TargetEvidenceSha256, row.State, row.Phase,
            row.Attempts, row.MaxAttempts, row.AvailableAtUtc,
            row.LeaseUntilUtc, row.CreatedAtUtc, row.UpdatedAtUtc,
            row.BlockedAtUtc, row.LastErrorCode, current, false);
    }

    private Task LockSourceAsync(Guid connectionId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({connectionId.ToString()}), 732911)", ct);
}
