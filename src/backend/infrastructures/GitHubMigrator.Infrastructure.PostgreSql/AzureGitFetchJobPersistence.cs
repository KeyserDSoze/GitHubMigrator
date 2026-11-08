using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// Source acquisition jobs contain only UUIDs and proof SHA-256s, never PATs,
// remote URIs, local filesystem paths, pack contents or GitHub credentials.
public sealed class AzureGitFetchJobRecord
{
    public Guid Id { get; set; }
    public Guid WorkpackId { get; set; }
    public Guid PlanId { get; set; }
    public Guid PreparationId { get; set; }
    public Guid ReconciliationId { get; set; }
    public string ReconciliationSha256 { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public string State { get; set; } = AzureGitFetchJobStates.Queued;
    public string Phase { get; set; } = AzureGitFetchJobPhases.Pending;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 4;
    public DateTimeOffset AvailableAtUtc { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public string? BundleSha256 { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class AzureGitFetchJobEventRecord
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public string Type { get; set; } = "";
    public string Phase { get; set; } = "";
    public int Attempt { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class PostgresAzureGitFetchJobRepository(
    AppDbContext db, TimeProvider clock,
    IGitTransferWorkpackRepository workpacks,
    IGitSourceBundleEvidenceStore bundles)
    : IAzureGitFetchJobRepository
{
    private const int MaxRunning = 2;

    public async Task<AzureGitFetchJobView> EnqueueAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, Guid actorId, CancellationToken ct)
    {
        var source = await db.GitTransferWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == workpackId && x.PlanId == planId, ct)
            ?? throw new KeyNotFoundException("TransferWorkpackNotFound");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSourceAsync(source.SourceConnectionId, ct);
        var proven = await workpacks.GetAsync(preparationId, planId, workpackId, ct)
            ?? throw new KeyNotFoundException("TransferWorkpackNotFound");
        if (!proven.IsCurrent || proven.State != GitTransferWorkpackStates.Blocked ||
            proven.Phase != GitTransferWorkpackPhases.WriteBlocked ||
            proven.ReconciliationSha256 != expectedReconciliationSha256 ||
            source.ReconciliationId != proven.ReconciliationId ||
            source.ReconciliationSha256 != proven.ReconciliationSha256)
            throw new AzureGitFetchJobException("AzureGitFetchProofNotCurrent");

        var now = clock.GetUtcNow();
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH created AS (
                INSERT INTO azure_git_fetch_jobs (
                    id, workpack_id, plan_id, preparation_id, reconciliation_id,
                    reconciliation_sha256, created_by, state, phase, attempts,
                    max_attempts, available_at_utc, lease_token, lease_until_utc,
                    created_at_utc, updated_at_utc, completed_at_utc,
                    last_error_code, bundle_sha256, ready_for_execution)
                VALUES ({id}, {workpackId}, {planId}, {preparationId},
                    {source.ReconciliationId}, {expectedReconciliationSha256},
                    {actorId}, 'Queued', 'Pending', 0, 4, {now}, NULL, NULL,
                    {now}, {now}, NULL, NULL, NULL, false)
                ON CONFLICT (workpack_id) DO NOTHING RETURNING id, phase
            )
            INSERT INTO azure_git_fetch_job_events (
                job_id, type, phase, attempt, occurred_at_utc, error_code)
            SELECT id, 'Queued', phase, 0, {now}, NULL FROM created
            """, ct);
        var saved = await db.AzureGitFetchJobs.AsNoTracking()
            .SingleAsync(x => x.WorkpackId == workpackId, ct);
        if (saved.PreparationId != preparationId || saved.PlanId != planId ||
            saved.ReconciliationId != proven.ReconciliationId ||
            saved.ReconciliationSha256 != expectedReconciliationSha256)
            throw new AzureGitFetchJobException("AzureGitFetchProofMismatch");
        await tx.CommitAsync(ct);
        return await ViewAsync(saved, ct);
    }

    public async Task<AzureGitFetchJobView?> GetAsync(
        Guid preparationId, Guid planId, Guid jobId, CancellationToken ct)
    {
        var row = await db.AzureGitFetchJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == jobId &&
                x.PreparationId == preparationId && x.PlanId == planId, ct);
        return row is null ? null : await ViewAsync(row, ct);
    }

    public async Task<IReadOnlyList<AzureGitFetchJobView>> ListAsync(
        Guid preparationId, Guid planId, CancellationToken ct)
    {
        var rows = await db.AzureGitFetchJobs.AsNoTracking()
            .Where(x => x.PreparationId == preparationId && x.PlanId == planId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Take(20).ToArrayAsync(ct);
        var result = new List<AzureGitFetchJobView>(rows.Length);
        foreach (var row in rows) result.Add(await ViewAsync(row, ct));
        return result;
    }

    public async Task<IReadOnlyList<AzureGitFetchEventView>> EventsAsync(
        Guid preparationId, Guid planId, Guid jobId, CancellationToken ct)
    {
        if (await GetAsync(preparationId, planId, jobId, ct) is null)
            throw new KeyNotFoundException("AzureGitFetchJobNotFound");
        var events = await db.AzureGitFetchJobEvents.AsNoTracking()
            .Where(x => x.JobId == jobId).OrderByDescending(x => x.Id)
            .Take(100).ToArrayAsync(ct);
        return events.OrderBy(x => x.Id)
            .Select(x => new AzureGitFetchEventView(x.Id, x.JobId, x.Type,
                x.Phase, x.Attempt, x.OccurredAtUtc, x.ErrorCode))
            .ToArray();
    }

    public async Task<AzureGitFetchLease?> ClaimAsync(
        Guid leaseToken, TimeSpan leaseDuration, CancellationToken ct)
    {
        if (leaseToken == Guid.Empty || leaseDuration <= TimeSpan.Zero ||
            leaseDuration > TimeSpan.FromMinutes(5))
            throw new AzureGitFetchJobException("AzureGitLeaseInvalid");

        var now = clock.GetUtcNow();
        var end = now.Add(leaseDuration);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Global advisory serialization makes MaxRunning safe across replicas,
        // not merely a per-process SemaphoreSlim.
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(732911, 37037)", ct);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH dead AS (
                UPDATE azure_git_fetch_jobs SET
                    state = 'Failed', last_error_code = 'AzureGitFetchAttemptsExhausted',
                    lease_token = NULL, lease_until_utc = NULL, updated_at_utc = {now}
                WHERE state = 'Running' AND lease_until_utc <= {now}
                    AND attempts >= max_attempts
                RETURNING id, phase, attempts
            )
            INSERT INTO azure_git_fetch_job_events (
                job_id, type, phase, attempt, occurred_at_utc, error_code)
            SELECT id, 'Failed', phase, attempts, {now},
                'AzureGitFetchAttemptsExhausted' FROM dead
            """, ct);

        var running = await db.AzureGitFetchJobs.AsNoTracking()
            .CountAsync(x => x.State == AzureGitFetchJobStates.Running &&
                x.LeaseUntilUtc > now, ct);
        if (running >= MaxRunning)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH next AS (
                SELECT id FROM azure_git_fetch_jobs
                WHERE ((state = 'Queued' AND available_at_utc <= {now})
                    OR (state = 'Running' AND lease_until_utc <= {now}))
                    AND attempts < max_attempts
                ORDER BY available_at_utc, created_at_utc, id
                FOR UPDATE SKIP LOCKED LIMIT 1
            ), claimed AS (
                UPDATE azure_git_fetch_jobs j
                SET state = 'Running', lease_token = {leaseToken},
                    lease_until_utc = {end}, attempts = attempts + 1,
                    updated_at_utc = {now}
                FROM next WHERE j.id = next.id
                RETURNING j.id, j.phase, j.attempts
            )
            INSERT INTO azure_git_fetch_job_events (
                job_id, type, phase, attempt, occurred_at_utc, error_code)
            SELECT id, 'Claimed', phase, attempts, {now}, NULL FROM claimed
            """, ct);

        var claimed = await db.AzureGitFetchJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.LeaseToken == leaseToken, ct);
        await tx.CommitAsync(ct);
        return claimed is null ? null : new(
            claimed.Id, claimed.PreparationId, claimed.PlanId,
            claimed.WorkpackId, claimed.ReconciliationSha256,
            leaseToken, claimed.Attempts, claimed.Phase);
    }

    public Task<bool> MirrorVerifiedAsync(AzureGitFetchLease lease, CancellationToken ct) =>
        TransitionAsync(lease, AzureGitFetchJobPhases.Pending,
            AzureGitFetchJobPhases.MirrorVerified, false, null, ct);

    public async Task<bool> BundleVerifiedAsync(
        AzureGitFetchLease lease, string bundleSha256, CancellationToken ct)
    {
        if (!AzureGitFetchJobService.ValidSha(bundleSha256))
            throw new AzureGitFetchJobException("AzureGitBundleProofInvalid");
        return await TransitionAsync(lease, AzureGitFetchJobPhases.MirrorVerified,
            AzureGitFetchJobPhases.BundleVerified, true, bundleSha256, ct);
    }

    private async Task<bool> TransitionAsync(
        AzureGitFetchLease lease, string from, string to, bool terminal,
        string? bundleSha256, CancellationToken ct)
    {
        var row = await db.AzureGitFetchJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == lease.JobId, ct);
        if (row is null || row.WorkpackId != lease.WorkpackId ||
            row.PlanId != lease.PlanId ||
            row.ReconciliationSha256 != lease.ReconciliationSha256)
            return false;

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pack = await db.GitTransferWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == row.WorkpackId, ct)
            ?? throw new AzureGitFetchJobException("AzureGitFetchProofNotCurrent");
        await LockSourceAsync(pack.SourceConnectionId, ct);
        var verified = await workpacks.GetAsync(row.PreparationId, row.PlanId,
            row.WorkpackId, ct);
        if (verified is not { IsCurrent: true, State: GitTransferWorkpackStates.Blocked,
            Phase: GitTransferWorkpackPhases.WriteBlocked } ||
            verified.ReconciliationSha256 != row.ReconciliationSha256)
            throw new AzureGitFetchJobException("AzureGitFetchProofNotCurrent");

        if (terminal)
        {
            var evidence = await bundles.GetAsync(row.PreparationId,
                row.PlanId, row.WorkpackId, ct);
            if (evidence is not { IsCurrent: true, ReadyForExecution: false } ||
                evidence.ReconciliationSha256 != lease.ReconciliationSha256 ||
                evidence.BundleSha256 != bundleSha256)
                throw new AzureGitFetchJobException("AzureGitBundleProofNotCurrent");
        }

        // The successful journal event is derived from the CAS update, not
        // appended by a worker that may have lost its lease during the Git I/O.
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH progressed AS (
                UPDATE azure_git_fetch_jobs
                SET phase = {to},
                    state = CASE WHEN {terminal} THEN 'Completed' ELSE 'Running' END,
                    completed_at_utc = CASE WHEN {terminal} THEN {now} ELSE NULL END,
                    bundle_sha256 = CASE WHEN {terminal} THEN {bundleSha256} ELSE NULL END,
                    last_error_code = NULL,
                    lease_token = CASE WHEN {terminal} THEN NULL ELSE lease_token END,
                    lease_until_utc = CASE WHEN {terminal} THEN NULL ELSE lease_until_utc END,
                    updated_at_utc = {now}
                WHERE id = {lease.JobId} AND workpack_id = {lease.WorkpackId}
                    AND reconciliation_sha256 = {lease.ReconciliationSha256}
                    AND state = 'Running' AND phase = {from}
                    AND attempts = {lease.Attempt} AND lease_token = {lease.Token}
                    AND lease_until_utc > {now}
                RETURNING id, phase, attempts
            )
            INSERT INTO azure_git_fetch_job_events (
                job_id, type, phase, attempt, occurred_at_utc, error_code)
            SELECT id, {to}, phase, attempts, {now}, NULL FROM progressed
            """, ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<bool> FailAsync(
        AzureGitFetchLease lease, string safeErrorCode,
        bool permanent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(safeErrorCode) || safeErrorCode.Length > 70 ||
            safeErrorCode.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new AzureGitFetchJobException("AzureGitUnsafeErrorCode");
        var now = clock.GetUtcNow();
        var backoff = Math.Min(300, 10 * (1 << Math.Min(lease.Attempt, 5)));
        var until = now.AddSeconds(backoff);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH changed AS (
                UPDATE azure_git_fetch_jobs SET
                    state = CASE WHEN {permanent} OR attempts >= max_attempts
                        THEN 'Failed' ELSE 'Queued' END,
                    available_at_utc = CASE WHEN {permanent} OR attempts >= max_attempts
                        THEN available_at_utc ELSE {until} END,
                    last_error_code = {safeErrorCode},
                    lease_token = NULL, lease_until_utc = NULL,
                    updated_at_utc = {now}
                WHERE id = {lease.JobId} AND workpack_id = {lease.WorkpackId}
                    AND reconciliation_sha256 = {lease.ReconciliationSha256}
                    AND state = 'Running' AND attempts = {lease.Attempt}
                    AND lease_token = {lease.Token} AND lease_until_utc > {now}
                RETURNING id, state, phase, attempts
            )
            INSERT INTO azure_git_fetch_job_events (
                job_id, type, phase, attempt, occurred_at_utc, error_code)
            SELECT id,
                CASE WHEN state = 'Failed' THEN 'Failed' ELSE 'RetryScheduled' END,
                phase, attempts, {now}, {safeErrorCode} FROM changed
            """, ct);
        return affected == 1;
    }

    private async Task<AzureGitFetchJobView> ViewAsync(
        AzureGitFetchJobRecord row, CancellationToken ct)
    {
        if (row.ReadyForExecution ||
            row.State is not ("Queued" or "Running" or "Completed" or "Failed") ||
            row.Phase is not ("Pending" or "MirrorVerified" or "BundleVerified") ||
            (row.State == "Completed") != (row.Phase == "BundleVerified") ||
            (row.State == "Completed") != (row.BundleSha256 is not null))
            throw new AzureGitFetchJobException("AzureGitFetchJobIntegrityFailed");
        var pack = await workpacks.GetAsync(
            row.PreparationId, row.PlanId, row.WorkpackId, ct)
            ?? throw new AzureGitFetchJobException("AzureGitFetchJobIntegrityFailed");
        if (pack.ReconciliationId != row.ReconciliationId ||
            pack.ReconciliationSha256 != row.ReconciliationSha256 ||
            pack.State != GitTransferWorkpackStates.Blocked)
            throw new AzureGitFetchJobException("AzureGitFetchJobIntegrityFailed");
        if (row.State == AzureGitFetchJobStates.Completed)
        {
            var evidence = await bundles.GetAsync(
                row.PreparationId, row.PlanId, row.WorkpackId, ct)
                ?? throw new AzureGitFetchJobException("AzureGitFetchJobIntegrityFailed");
            if (evidence.BundleSha256 != row.BundleSha256 ||
                evidence.ReconciliationSha256 != row.ReconciliationSha256 ||
                evidence.ReadyForExecution)
                throw new AzureGitFetchJobException("AzureGitFetchJobIntegrityFailed");
        }
        return new(row.Id, row.WorkpackId, row.PlanId, row.ReconciliationId,
            row.ReconciliationSha256, row.State, row.Phase,
            row.Attempts, row.MaxAttempts, row.AvailableAtUtc,
            row.LeaseUntilUtc, row.CreatedAtUtc, row.UpdatedAtUtc,
            row.CompletedAtUtc, row.LastErrorCode, pack.IsCurrent, false);
    }

    private Task LockSourceAsync(Guid sourceId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({sourceId.ToString()}), 732911)", ct);
}
