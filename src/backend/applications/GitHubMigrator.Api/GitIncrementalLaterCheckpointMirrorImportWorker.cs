using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.Extensions.DependencyInjection;

// #120: explicitly invoked, default-OFF local F-060v2 -> F-061v2
// coordinator. No background service, HTTP route, remote Git or F-095.
// Every SQL operation uses its OWN short-lived scoped DbContext; heartbeat
// never shares an EF transaction with local git fetch/fsck subprocesses.
public sealed record GitIncrementalLaterCheckpointMirrorImportWorkerOptions(
    bool Enabled, TimeSpan LeaseDuration, TimeSpan HeartbeatInterval);

public sealed class GitIncrementalLaterCheckpointMirrorImportWorker(
    IServiceScopeFactory scopes,
    GitIncrementalLaterCheckpointMirrorImportWorkerOptions options,
    IGitIncrementalLaterCheckpointMirrorImporter importer)
{
    private static GitSourceSnapshotMirrorException Refused(string code) =>
        new(code);

    private sealed class ActiveOwner(
        GitIncrementalLaterCheckpointMirrorLeaseClaim initial)
    {
        private readonly object gate = new();
        private GitIncrementalLaterCheckpointMirrorLeaseClaim current = initial;
        private Exception? failure;

        public GitIncrementalLaterCheckpointMirrorLeaseClaim Current
        {
            get { lock (gate) return current; }
        }

        public Exception? Failure
        {
            get { lock (gate) return failure; }
        }

        public void Renewed(GitIncrementalLaterCheckpointMirrorLeaseClaim next)
        {
            lock (gate) current = next;
        }

        public void Fail(Exception error)
        {
            lock (gate) failure ??= error;
        }
    }

    public async Task<GitIncrementalLaterCheckpointMirrorVerifiedReceipt>
        RunAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid bundleActorId, Guid workerId, CancellationToken ct)
    {
        // A worker must be explicitly enabled separately from the
        // F-035v2, v2 read, v2 lease, physical import and Verified gates.
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorImportWorkerDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || targetObservationId == Guid.Empty ||
            workpackId == Guid.Empty || workerId == Guid.Empty ||
            bundleActorId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            !GitSourceBundleStorageCheck.ValidSha(expectedTargetReceiptSha256) ||
            options.LeaseDuration <
                PostgresGitIncrementalLaterCheckpointMirrorLeaseStore.MinimumLease ||
            options.LeaseDuration >
                PostgresGitIncrementalLaterCheckpointMirrorLeaseStore.MaximumLease ||
            options.HeartbeatInterval < TimeSpan.FromMilliseconds(10) ||
            options.HeartbeatInterval >= options.LeaseDuration / 3)
            throw Refused("LaterCheckpointMirrorImportWorkerInvalidScope");

        GitIncrementalLaterCheckpointMirrorLeaseClaim acquired;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<
                PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>();
            acquired = await store.ClaimAsync(preparationId, planId,
                checkpointId, targetObservationId, workpackId,
                sourceRepositoryId, expectedTargetReceiptSha256,
                bundleActorId, workerId, options.LeaseDuration, ct);
        }

        var owner = new ActiveOwner(acquired);
        try
        {
            GitIncrementalLaterCheckpointMirrorStagingPlan stage;
            GitIncrementalLaterCheckpointBundleReceipt durable;
            using (var workCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // The pulse must be alive BEFORE the physical SQL reads
                // and DURING long Git export/fetch/fsck. It cancels work
                // immediately when ownership or currentness is lost.
                var pulse = PulseAsync(owner, workCts);
                try
                {
                    await using (var scope = scopes.CreateAsyncScope())
                    {
                        var reader = scope.ServiceProvider.GetRequiredService<
                            PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight>();
                        var history = scope.ServiceProvider.GetRequiredService<
                            PostgresGitIncrementalLaterCheckpointBundleHistory>();
                        stage = await reader.ReadAsync(preparationId,
                            planId, checkpointId, targetObservationId,
                            workpackId, sourceRepositoryId,
                            expectedTargetReceiptSha256, bundleActorId,
                            workCts.Token);
                        durable = await history.ReadAsync(preparationId,
                            planId, checkpointId, targetObservationId,
                            workpackId, sourceRepositoryId,
                            expectedTargetReceiptSha256, bundleActorId,
                            workCts.Token)
                            ?? throw Refused(
                                "LaterCheckpointMirrorImportWorkerBundleMissing");
                    }

                    GitIncrementalLaterCheckpointMirrorStagingIntegrity
                        .VerifyOfflineCandidate(stage, durable);
                    RequireStage(owner.Current, stage);
                    workCts.Token.ThrowIfCancellationRequested();

                    // F-060v2 local-only import uses trusted filesystem
                    // roots from the DI factory and imports only the
                    // independently sealed F-035v2 UUID.bundle bytes.
                    // No PostgreSQL transaction is open during Git.
                    var imported = await importer.MaterializeLaterCheckpointAsync(
                        stage, durable, workCts.Token);
                    GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity
                        .Verify(imported, stage, durable);
                    RequireStage(owner.Current, stage);
                    workCts.Token.ThrowIfCancellationRequested();
                }
                finally
                {
                    // Freeze the latest database-backed heartbeat handle
                    // before the final independent physical re-audit.
                    // A new bounded Renew CAS below gives its own fresh
                    // DB-clock deadline; if it fails, no Verified is saved.
                    await workCts.CancelAsync();
                    await pulse;
                }
            }

            if (owner.Failure is not null)
                throw Refused("LaterCheckpointMirrorImportWorkerLeaseLost");
            ct.ThrowIfCancellationRequested();

            // A fresh scoped DbContext ensures none of the heartbeat's
            // tracked entities, transaction or last expiry is reused.
            GitIncrementalLaterCheckpointMirrorLeaseClaim finalLease;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<
                    PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>();
                finalLease = await store.RenewAsync(owner.Current,
                    options.LeaseDuration, ct);
            }

            // CompleteAsync DOUBLE-audits the actual promoted bare Git
            // mirror under trusted roots and then rechecks live
            // F-091/F-097b/F-031/F-038v2/F-035v2 under source-first SQL
            // locks before one atomic Verified receipt + event + CAS.
            await using (var scope = scopes.CreateAsyncScope())
            {
                var journal = scope.ServiceProvider.GetRequiredService<
                    PostgresGitIncrementalLaterCheckpointMirrorVerifiedJournal>();
                return await journal.CompleteAsync(finalLease,
                    preparationId, planId, targetObservationId,
                    sourceRepositoryId, expectedTargetReceiptSha256, ct);
            }
        }
        catch
        {
            // If physical work fails (including cancellation/heartbeat
            // loss), mark ONLY the exact still-owned generation terminal.
            // A lost lease may be fenced by someone else and is never
            // automatically reclaimed or its files overwritten.
            try
            {
                using var cleanup = new CancellationTokenSource(
                    TimeSpan.FromSeconds(15));
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<
                    PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>();
                await store.QuarantineAsync(owner.Current,
                    "ImportWorkerFailed", cleanup.Token);
            }
            catch (Exception) { /* fail closed: expiry will quarantine */ }
            throw;
        }
    }

    private async Task PulseAsync(
        ActiveOwner owner, CancellationTokenSource workCts)
    {
        using var timer = new PeriodicTimer(options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(workCts.Token))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var store = scope.ServiceProvider.GetRequiredService<
                        PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>();
                    var updated = await store.RenewAsync(owner.Current,
                        options.LeaseDuration, workCts.Token);
                    owner.Renewed(updated);
                }
                catch (OperationCanceledException)
                    when (workCts.IsCancellationRequested)
                { break; }
                catch (Exception error)
                {
                    owner.Fail(error);
                    await workCts.CancelAsync();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
            when (workCts.IsCancellationRequested)
        { }
    }

    private static void RequireStage(
        GitIncrementalLaterCheckpointMirrorLeaseClaim lease,
        GitIncrementalLaterCheckpointMirrorStagingPlan stage)
    {
        if (lease.MirrorId != stage.PhysicalMirrorId ||
            lease.SourceSnapshotId != stage.SourceSnapshotId ||
            lease.CheckpointId != stage.CheckpointId ||
            lease.WorkpackId != stage.WorkpackId ||
            lease.StagingEvidenceSha256 != stage.EvidenceSha256 ||
            lease.Generation is < 1 or > 2000 ||
            lease.LeaseToken == Guid.Empty ||
            lease.WorkerId == Guid.Empty ||
            lease.CanMaterialize || lease.CanPush ||
            lease.CanRetry || lease.CanDispatch || lease.MigrationComplete)
            throw Refused("LaterCheckpointMirrorImportWorkerScopeChanged");
    }
}
