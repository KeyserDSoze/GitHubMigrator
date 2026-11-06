using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitSandboxDispatchRecord
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public Guid SandboxId { get; set; }
    public long RepositoryId { get; set; }
    public string PacketSha256 { get; set; } = "";
    public string PayloadSha256 { get; set; } = "";
    // Source/target refs and F-044 bundle metadata: no filesystem paths,
    // credentials, GitHub URL or executable arguments. The payload is sealed.
    public string PayloadJson { get; set; } = "";
    public string State { get; set; } = "";
    public Guid? ClaimToken { get; set; }
    public DateTimeOffset? ClaimUntilUtc { get; set; }
    public string? OutcomeSha256 { get; set; }
    public string? OutcomeCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public bool RemoteWritesPerformed { get; set; }
    public bool ExecutionAuthorized { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class GitSandboxDispatchEventRecord
{
    public long Id { get; set; }
    public Guid DispatchId { get; set; }
    public string Type { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}

// Database scope is the system of record for admission across API replicas.
// After the first claim, *no* transaction ever sets a job back to Queued.
public sealed class PostgresGitSandboxDispatchStore(
    AppDbContext db, TimeProvider clock, GitSandboxDispatchOptions options)
    : IGitSandboxDispatchStore
{
    public async Task<GitSandboxDispatchJob> EnqueueAsync(
        Guid attemptId, LocalGitCreateOnlyTransferRequest request,
        CancellationToken ct)
    {
        AssertAllowed(request.SandboxId);
        if (attemptId == Guid.Empty)
            throw new GitSandboxDispatchException("SandboxDispatchRequestInvalid");
        var (json, sha) = GitSandboxDispatchIntegrity.Prepare(request);
        var now = clock.GetUtcNow();
        var id = Guid.NewGuid();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The same global numeric target repo lock as F-045 and F-047.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(732911, hashtext({request.Packet.RepositoryId.ToString()}))", ct);
        var attempt = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attemptId &&
                x.ScopeId == request.Packet.ScopeId &&
                x.PreparationId == request.Packet.PreparationId &&
                x.PlanId == request.Packet.PlanId &&
                x.RepositoryId == request.Packet.RepositoryId &&
                x.PacketSha256 == request.Packet.EvidenceSha256 &&
                x.CandidateSetSha256 == request.Packet.CandidateSetSha256 &&
                x.State == GitHubTransferAttemptStates.BlockedAtWriteBarrier &&
                x.ReviewedBy != null && x.ReviewedBy != x.RequestedBy &&
                !x.TransportAttempted && !x.ExecutionAuthorized &&
                !x.RemoteWritesPerformed && !x.ReadyForExecution, ct);
        if (attempt is null)
            throw new GitSandboxDispatchException("SandboxDispatchAttemptNotReviewed");

        var existing = await db.GitSandboxDispatches.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AttemptId == attemptId, ct);
        if (existing is not null)
        {
            if (existing.SandboxId != request.SandboxId ||
                existing.PayloadSha256 != sha ||
                existing.PacketSha256 != request.Packet.EvidenceSha256 ||
                existing.RepositoryId != request.Packet.RepositoryId)
                throw new GitSandboxDispatchException("SandboxDispatchConflictingReplay");
            await tx.CommitAsync(ct);
            return Map(existing);
        }

        // Block either a second active job on the same global GitHub ID
        // or an attempt to reuse a local sandbox for a different source.
        if (await db.GitSandboxDispatches.AsNoTracking().AnyAsync(x =>
            (x.RepositoryId == request.Packet.RepositoryId ||
             x.SandboxId == request.SandboxId) &&
            (x.State == GitSandboxDispatchStates.Queued ||
             x.State == GitSandboxDispatchStates.MayHaveTransmitted ||
             x.State == GitSandboxDispatchStates.InspectionRequired ||
             x.State == GitSandboxDispatchStates.Inspecting), ct))
            throw new GitSandboxDispatchException("SandboxDispatchActiveTargetBusy");

        int changed;
        try
        {
        changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH inserted AS (
                INSERT INTO git_sandbox_dispatches (
                    id, attempt_id, sandbox_id, repository_id, packet_sha256,
                    payload_sha256, payload_json, state,
                    claim_token, claim_until_utc, outcome_sha256, outcome_code,
                    created_at_utc, updated_at_utc,
                    remote_writes_performed, execution_authorized,
                    ready_for_execution)
                VALUES ({id}, {attemptId}, {request.SandboxId},
                    {request.Packet.RepositoryId},
                    {request.Packet.EvidenceSha256}, {sha}, {json}, 'Queued',
                    NULL, NULL, NULL, NULL, {now}, {now},
                    false, false, false)
                RETURNING id
            )
            INSERT INTO git_sandbox_dispatch_events (
                dispatch_id, type, occurred_at_utc)
            SELECT id, 'Queued', {now} FROM inserted
            """, ct);
        }
        catch (PostgresException ex) when (ex.SqlState ==
            PostgresErrorCodes.UniqueViolation)
        {
            // A competing process can race the sandbox UUID while locking
            // a *different* numeric GitHub repo. The partial unique indexes
            // remain the final concurrency guarantee.
            throw new GitSandboxDispatchException("SandboxDispatchActiveTargetBusy");
        }
        if (changed != 1)
            throw new GitSandboxDispatchException("SandboxDispatchAdmissionFailed");
        await tx.CommitAsync(ct);
        return Map(await db.GitSandboxDispatches.AsNoTracking()
            .SingleAsync(x => x.Id == id, ct));
    }

    public async Task<GitSandboxDispatchClaim?> ClaimNextAsync(CancellationToken ct)
    {
        if (!options.Enabled) return null;
        var allowed = options.AllowedSandboxIds.ToArray();
        if (allowed.Length == 0) return null;
        var row = await db.GitSandboxDispatches.AsNoTracking()
            .Where(x => allowed.Contains(x.SandboxId) &&
                x.State == GitSandboxDispatchStates.Queued)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var now = clock.GetUtcNow();
        var token = Guid.NewGuid();
        var until = now.Add(GitSandboxDispatchIntegrity.ClaimWindow);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH claimed AS (
                UPDATE git_sandbox_dispatches d
                SET state = 'MayHaveTransmitted',
                    claim_token = {token}, claim_until_utc = {until},
                    updated_at_utc = {now}
                WHERE d.id = {row.Id} AND d.sandbox_id = {row.SandboxId}
                  AND d.state = 'Queued'
                  AND d.remote_writes_performed = false
                  AND d.execution_authorized = false
                  AND d.ready_for_execution = false
                  AND EXISTS (
                    SELECT 1 FROM github_transfer_attempts a
                    WHERE a.id = d.attempt_id
                      AND a.repository_id = d.repository_id
                      AND a.packet_sha256 = d.packet_sha256
                      AND a.state = 'BlockedAtWriteBarrier'
                      AND a.reviewed_by IS NOT NULL
                      AND a.reviewed_by <> a.requested_by
                      AND a.transport_attempted = false
                      AND a.execution_authorized = false
                      AND a.remote_writes_performed = false
                      AND a.ready_for_execution = false
                  )
                RETURNING id
            )
            INSERT INTO git_sandbox_dispatch_events
                (dispatch_id, type, occurred_at_utc)
            SELECT id, 'PotentialSend', {now} FROM claimed
            """, ct);
        await tx.CommitAsync(ct);
        if (changed != 1) return null;
        return OpenClaim(await db.GitSandboxDispatches.AsNoTracking()
            .SingleAsync(x => x.Id == row.Id, ct), token);
    }

    public async Task<GitSandboxDispatchClaim?> ClaimInspectionAsync(
        CancellationToken ct)
    {
        if (!options.Enabled) return null;
        var allowed = options.AllowedSandboxIds.ToArray();
        if (allowed.Length == 0) return null;
        var now = clock.GetUtcNow();
        var row = await db.GitSandboxDispatches.AsNoTracking()
            .Where(x => allowed.Contains(x.SandboxId) &&
                (x.State == GitSandboxDispatchStates.InspectionRequired ||
                ((x.State == GitSandboxDispatchStates.MayHaveTransmitted ||
                  x.State == GitSandboxDispatchStates.Inspecting) &&
                 x.ClaimUntilUtc < now)))
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var token = Guid.NewGuid();
        var until = now.Add(GitSandboxDispatchIntegrity.ClaimWindow);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH claimed AS (
                UPDATE git_sandbox_dispatches
                SET state = 'Inspecting',
                    claim_token = {token}, claim_until_utc = {until},
                    updated_at_utc = {now}
                WHERE id = {row.Id}
                  AND sandbox_id = {row.SandboxId}
                  AND (state = 'InspectionRequired' OR
                       (state IN ('MayHaveTransmitted','Inspecting') AND
                        claim_until_utc < {now}))
                  AND remote_writes_performed = false
                  AND execution_authorized = false
                  AND ready_for_execution = false
                RETURNING id
            )
            INSERT INTO git_sandbox_dispatch_events
                (dispatch_id, type, occurred_at_utc)
            SELECT id, 'InspectionStarted', {now} FROM claimed
            """, ct);
        await tx.CommitAsync(ct);
        if (changed != 1) return null;
        return OpenClaim(await db.GitSandboxDispatches.AsNoTracking()
            .SingleAsync(x => x.Id == row.Id, ct), token);
    }

    public async Task<bool> CompleteAsync(
        GitSandboxDispatchClaim claim, string state,
        string outcomeSha256, string outcomeCode, CancellationToken ct)
    {
        if (claim is null || !GitSourceBundleStorageCheck.ValidSha(outcomeSha256) ||
            outcomeCode is not ("LocalGitVerified" or
                "LocalRefsAndObjectsObserved" or "LocalOutcomeDiverged" or
                "LocalInspectionFailed") ||
            !(state == GitSandboxDispatchStates.Verified &&
                  outcomeCode == "LocalGitVerified" ||
              state == GitSandboxDispatchStates.ReconciledMatch &&
                  outcomeCode == "LocalRefsAndObjectsObserved" ||
              state == GitSandboxDispatchStates.NeedsOperator &&
                  outcomeCode is ("LocalOutcomeDiverged" or
                      "LocalInspectionFailed")))
            throw new GitSandboxDispatchException("SandboxDispatchOutcomeInvalid");

        var oldState = state == GitSandboxDispatchStates.Verified
            ? GitSandboxDispatchStates.MayHaveTransmitted :
                GitSandboxDispatchStates.Inspecting;
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH completed AS (
                UPDATE git_sandbox_dispatches
                SET state = {state}, outcome_sha256 = {outcomeSha256},
                    outcome_code = {outcomeCode}, claim_token = NULL,
                    claim_until_utc = NULL, updated_at_utc = {now}
                WHERE id = {claim.Job.Id} AND state = {oldState}
                  AND claim_token = {claim.ClaimToken}
                  AND claim_until_utc > {now}
                  AND payload_sha256 = {claim.Job.PayloadSha256}
                  AND remote_writes_performed = false
                  AND execution_authorized = false
                  AND ready_for_execution = false
                RETURNING id
            )
            INSERT INTO git_sandbox_dispatch_events
                (dispatch_id, type, occurred_at_utc)
            SELECT id, {state}, {now} FROM completed
            """, ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<bool> RequireInspectionAsync(
        GitSandboxDispatchClaim claim, string reason, CancellationToken ct)
    {
        if (claim is null || reason != "SandboxOutcomeRequiresInspection")
            throw new GitSandboxDispatchException("SandboxDispatchOutcomeInvalid");
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH uncertain AS (
                UPDATE git_sandbox_dispatches
                SET state = 'InspectionRequired',
                    claim_token = NULL, claim_until_utc = NULL,
                    outcome_code = {reason}, updated_at_utc = {now}
                WHERE id = {claim.Job.Id} AND state = 'MayHaveTransmitted'
                  AND claim_token = {claim.ClaimToken}
                  AND payload_sha256 = {claim.Job.PayloadSha256}
                  AND remote_writes_performed = false
                  AND execution_authorized = false
                  AND ready_for_execution = false
                RETURNING id
            )
            INSERT INTO git_sandbox_dispatch_events
                (dispatch_id, type, occurred_at_utc)
            SELECT id, 'InspectionRequired', {now} FROM uncertain
            """, ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<GitSandboxDispatchJob?> GetAsync(
        Guid attemptId, CancellationToken ct)
    {
        var row = await db.GitSandboxDispatches.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AttemptId == attemptId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<GitSandboxDispatchEvent>> EventsAsync(
        Guid attemptId, CancellationToken ct)
    {
        var row = await db.GitSandboxDispatches.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AttemptId == attemptId, ct)
            ?? throw new KeyNotFoundException("SandboxDispatchNotFound");
        return (await db.GitSandboxDispatchEvents.AsNoTracking()
            .Where(x => x.DispatchId == row.Id)
            .OrderBy(x => x.Id).Take(64).ToArrayAsync(ct))
            .Select(x => new GitSandboxDispatchEvent(x.Id, x.DispatchId,
                x.Type, x.OccurredAtUtc)).ToArray();
    }

    private void AssertAllowed(Guid sandboxId)
    {
        if (!options.Enabled)
            throw new GitSandboxDispatchException("SandboxDispatchDisabled");
        if (sandboxId == Guid.Empty ||
            !options.AllowedSandboxIds.Contains(sandboxId))
            throw new GitSandboxDispatchException("SandboxNotAllowlisted");
    }

    private GitSandboxDispatchClaim OpenClaim(
        GitSandboxDispatchRecord row, Guid token)
    {
        AssertAllowed(row.SandboxId);
        var request = GitSandboxDispatchIntegrity.Open(
            row.PayloadJson, row.PayloadSha256);
        if (request.SandboxId != row.SandboxId ||
            request.Packet.RepositoryId != row.RepositoryId ||
            request.Packet.EvidenceSha256 != row.PacketSha256)
            throw new GitSandboxDispatchException("SandboxDispatchPayloadChanged");
        return new(Map(row), request, token);
    }

    private static GitSandboxDispatchJob Map(GitSandboxDispatchRecord x) =>
        new(x.Id, x.AttemptId, x.SandboxId, x.RepositoryId,
            x.PacketSha256, x.PayloadSha256, x.State, x.ClaimToken,
            x.ClaimUntilUtc, x.OutcomeSha256, x.OutcomeCode,
            x.CreatedAtUtc, x.UpdatedAtUtc,
            x.RemoteWritesPerformed, x.ExecutionAuthorized,
            x.ReadyForExecution);
}
