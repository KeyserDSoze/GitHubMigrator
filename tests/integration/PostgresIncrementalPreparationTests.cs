using System.Security.Cryptography;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-026 uses the real PostgreSQL 17 queue, WITHOUT mocking its advisory locks,
// row claims, replay, lease expiration, transactions, and unique constraints.
public sealed class PostgresIncrementalPreparationTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task F067_immutable_events_separate_queue_wait_from_final_attempt_work()
    {
        await using var db = fixture.NewContext();
        // Stay behind the real PostgreSQL clock so the SQL 24h window
        // is deterministic; simulate durations with the F-026 clock.
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddMinutes(-12));
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        await ConnectionsAsync(db, source, destination);
        var scan = Snapshot(source, clock.GetUtcNow().AddMinutes(-5),
            [new("repo-1", "refs/heads/main", "sha")]);
        await StageAsync(db, clock, scan);
        var queue = new PostgresIncrementalPreparationRepository(db, clock);
        var first = await queue.EnqueueAsync(scan.Id, source,
            destination, Guid.NewGuid(), default);

        clock.Advance(TimeSpan.FromSeconds(20));
        var firstLease = await queue.ClaimAsync(Guid.NewGuid(),
            TimeSpan.FromMinutes(5), default, source);
        Assert.NotNull(firstLease);
        Assert.Equal(first.Id, firstLease.Id);
        clock.Advance(TimeSpan.FromSeconds(25));
        Assert.True(await queue.PrepareAsync(firstLease, default));

        // New run, same source, another destination. On retry the
        // correct queue origin is RetryScheduled(attempt 1), never the
        // original Queued event or obsolete Claimed(attempt 1).
        var second = await queue.EnqueueAsync(scan.Id, source,
            Guid.NewGuid(), Guid.NewGuid(), default);
        clock.Advance(TimeSpan.FromSeconds(8));
        var failedAttempt = await queue.ClaimAsync(Guid.NewGuid(),
            TimeSpan.FromMinutes(5), default, source);
        Assert.NotNull(failedAttempt);
        Assert.Equal(second.Id, failedAttempt.Id);
        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.True(await queue.FailAsync(failedAttempt,
            "TransientDatabaseFailure", false, default));
        clock.Advance(TimeSpan.FromSeconds(11));
        var nextAttempt = await queue.ClaimAsync(Guid.NewGuid(),
            TimeSpan.FromMinutes(5), default, source);
        Assert.NotNull(nextAttempt);
        Assert.Equal(second.Id, nextAttempt.Id);
        Assert.Equal(2, nextAttempt.Attempt);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(await queue.PrepareAsync(nextAttempt, default));

        // A row falsely marked Prepared without a corresponding
        // claimed-and-sealed event chain MUST reduce coverage, not
        // invent a fake zero-duration or sample from another attempt.
        var malformed = await queue.EnqueueAsync(scan.Id, source,
            Guid.NewGuid(), Guid.NewGuid(), default);
        var fakePreparedAt = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparations SET state='Prepared',
                attempts=1, prepared_at_utc={fakePreparedAt},
                updated_at_utc={fakePreparedAt}, lease_token=NULL,
                lease_until_utc=NULL
            WHERE id={malformed.Id}
            """);

        // A seemingly complete event chain with >5 minutes of lease
        // occupancy is also invalid: F-026 has a hard five-minute
        // lease, so skewed or synthetic timestamps must not skew P95.
        var impossible = await queue.EnqueueAsync(scan.Id, source,
            Guid.NewGuid(), Guid.NewGuid(), default);
        var impossibleClaim = clock.GetUtcNow().AddSeconds(1);
        var impossibleFinish = impossibleClaim.AddMinutes(7);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparations SET state='Prepared',
                attempts=1, prepared_at_utc={impossibleFinish},
                updated_at_utc={impossibleFinish},
                lease_token=NULL, lease_until_utc=NULL
            WHERE id={impossible.Id}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO incremental_preparation_events (
                preparation_id, event_type, attempt,
                occurred_at_utc, error_code)
            VALUES ({impossible.Id}, 'Claimed', 1,
                        {impossibleClaim}, NULL),
                   ({impossible.Id}, 'Prepared', 1,
                        {impossibleFinish}, NULL)
            """);

        var reader = new PostgresIncrementalPreparationPhaseHealthReader(
            db, new(true));
        var before = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        var phase = await reader.ReadAsync(source, default);
        var after = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        Assert.InRange(phase.ObservedAtUtc, before, after);
        Assert.Equal(4, phase.PreparedLast24Hours);
        Assert.Equal(2, phase.ValidPhaseSamples);
        Assert.Equal(2, phase.MissingOrInvalidSamples);
        Assert.True(phase.TimingEvidenceIncomplete);
        Assert.Equal(1, phase.RetriedPreparedLast24Hours);
        Assert.InRange(phase.AverageQueueWaitSeconds!.Value, 15.49, 15.51);
        Assert.InRange(phase.AverageClaimToPreparedSeconds!.Value,
            19.99, 20.01);
        Assert.InRange(phase.P95ClaimToPreparedSeconds!.Value, 24.49, 24.51);
        Assert.InRange(phase.MaxClaimToPreparedSeconds!.Value, 24.99, 25.01);
        Assert.Equal(phase.PreparedLast24Hours,
            phase.ValidPhaseSamples + phase.MissingOrInvalidSamples);

        // Health is strictly read-only. No extra lifecycle events,
        // retries, review approvals or destination transfers.
        Assert.Equal(["Queued", "Claimed", "RetryScheduled",
                "Claimed", "Prepared"],
            (await queue.EventsAsync(second.Id, default))
                .Select(x => x.EventType).ToArray());
        var malformedEvents = await queue.EventsAsync(malformed.Id, default);
        Assert.Single(malformedEvents);
        Assert.Equal("Queued", malformedEvents[0].EventType);

        var json = System.Text.Json.JsonSerializer.Serialize(phase);
        foreach (var forbidden in new[] {
            "PreparationId", "SnapshotId", "SourceConnectionId",
            "DestinationConnectionId", "LeaseToken", "ErrorCode",
            "WorkerId", "RepositoryName"
        })
            Assert.DoesNotContain(forbidden, json,
                StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(second.Id.ToString("D"),
            json, StringComparison.OrdinalIgnoreCase);

        // No source existence oracle and no invented durations.
        var empty = await reader.ReadAsync(Guid.NewGuid(), default);
        Assert.Equal(0, empty.PreparedLast24Hours);
        Assert.Equal(0, empty.ValidPhaseSamples);
        Assert.Equal(0, empty.MissingOrInvalidSamples);
        Assert.Null(empty.AverageQueueWaitSeconds);
        Assert.Null(empty.P95ClaimToPreparedSeconds);
        Assert.Null(empty.MaxClaimToPreparedSeconds);
        Assert.False(empty.TimingEvidenceIncomplete);

        var disabled = new PostgresIncrementalPreparationPhaseHealthReader(
            db, new(false));
        Assert.Equal("IncrementalQueueHealthDisabled",
            (await Assert.ThrowsAsync<IncrementalPreparationException>(
                () => disabled.ReadAsync(source, default))).Code);
        Assert.Equal("IncrementalQueueHealthSourceInvalid",
            (await Assert.ThrowsAsync<IncrementalPreparationException>(
                () => reader.ReadAsync(Guid.Empty, default))).Code);
    }

    [Fact]
    public async Task F066_database_queue_health_is_source_scoped_classified_and_read_only()
    {
        await using var db = fixture.NewContext();
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualClock(now);
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await ConnectionsAsync(db, source, target);
        var scan = Snapshot(source, now.AddMinutes(-8),
            [new("repo-1", "refs/heads/main", "sha")]);
        await StageAsync(db, clock, scan);
        var queue = new PostgresIncrementalPreparationRepository(db, clock);
        var rows = new List<IncrementalPreparationView>();
        for (var i = 0; i < 8; i++)
            rows.Add(await queue.EnqueueAsync(scan.Id, source,
                Guid.NewGuid(), Guid.NewGuid(), default));

        // Each row is a different destination for the same *source*.
        // The query does not perform a cross-tenant fleet-wide scan.
        var later = now.AddMinutes(10);
        var validLease = now.AddMinutes(3);
        var expiredLease = now.AddMinutes(-3);
        var historical = now.AddMinutes(-1);
        var created = now.AddMinutes(-8);
        var evidence = new string('a', 64);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparations SET
                state='Queued', attempts=1,
                available_at_utc={later},
                last_error_code='PreparationDeadlineExceeded'
            WHERE id={rows[1].Id}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparations SET
                state='Running', attempts=1, lease_token={Guid.NewGuid()},
                lease_until_utc={validLease}
            WHERE id={rows[2].Id}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparations SET
                state='Running', attempts=2, lease_token={Guid.NewGuid()},
                lease_until_utc={expiredLease}
            WHERE id={rows[3].Id}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparations SET
                state='Prepared', attempts=1, lease_token=NULL,
                lease_until_utc=NULL, prepared_at_utc={historical},
                created_at_utc={created}, item_count=7,
                evidence_sha256={evidence}, updated_at_utc={historical}
            WHERE id={rows[4].Id}
            """);
        var failures = new[] {
            "IncrementalJournalMismatch",
            "LeaseExpiredAfterMaxAttempts",
            "IncrementalPreparationFailed"
        };
        for (var i = 0; i < failures.Length; i++)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE incremental_preparations SET state='Failed',
                    attempts=5, last_error_code={failures[i]},
                    lease_token=NULL, lease_until_utc=NULL,
                    updated_at_utc={historical}
                WHERE id={rows[i + 5].Id}
                """);
        }

        var enabled = new PostgresIncrementalPreparationQueueHealthReader(
            db, new(true));
        var before = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        var result = await enabled.ReadAsync(source, default);
        var after = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync();

        Assert.InRange(result.ObservedAtUtc, before, after);
        Assert.Equal(8, result.TotalRuns);
        Assert.Equal(1, result.QueuedReady);
        Assert.Equal(1, result.QueuedDeferred);
        Assert.Equal(1, result.RunningCurrent);
        Assert.Equal(1, result.RunningExpired);
        Assert.Equal(1, result.Prepared);
        Assert.Equal(3, result.Failed);
        Assert.Equal(1, result.RetryingQueued);
        Assert.Equal(1, result.PreparedLast24Hours);
        Assert.Equal(7, result.PreparedReviewItemsLast24Hours);
        Assert.Equal(3, result.FailedLast24Hours);
        Assert.Equal(1, result.FailedSourceEvidence);
        Assert.Equal(1, result.FailedTimeoutOrLease);
        Assert.Equal(1, result.FailedOther);
        Assert.InRange(result.AverageTurnaroundSecondsLast24Hours!.Value,
            350, 500);
        Assert.True(result.OldestReadyWaitSeconds > 0);
        Assert.True(result.AttentionRequired);
        Assert.Equal(result.TotalRuns,
            result.QueuedReady + result.QueuedDeferred +
            result.RunningCurrent + result.RunningExpired +
            result.Prepared + result.Failed);

        // No lease token, preparation ID, snapshots or raw error codes
        // are serialized even for an authorized superadmin.
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        foreach (var forbidden in new[] {
            "SourceConnectionId", "DestinationConnectionId",
            "SnapshotId", "EnqueuedBy", "LeaseToken",
            "LastErrorCode", "IncrementalJournalMismatch"
        })
            Assert.DoesNotContain(forbidden, json,
                StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rows[0].Id.ToString("D"),
            json, StringComparison.OrdinalIgnoreCase);

        // An empty source has zero counts and no invented latency.
        var unknown = await enabled.ReadAsync(Guid.NewGuid(), default);
        Assert.Equal(0, unknown.TotalRuns);
        Assert.Null(unknown.OldestReadyWaitSeconds);
        Assert.Null(unknown.AverageTurnaroundSecondsLast24Hours);
        Assert.False(unknown.AttentionRequired);

        // Gate checks precede any SQL query or source inspection.
        var disabled = new PostgresIncrementalPreparationQueueHealthReader(
            db, new(false));
        Assert.Equal("IncrementalQueueHealthDisabled",
            (await Assert.ThrowsAsync<IncrementalPreparationException>(
                () => disabled.ReadAsync(source, default))).Code);
        Assert.Equal("IncrementalQueueHealthSourceInvalid",
            (await Assert.ThrowsAsync<IncrementalPreparationException>(
                () => enabled.ReadAsync(Guid.Empty, default))).Code);
    }

    [Fact]
    public async Task Six_hundred_changes_become_bounded_review_only_items_not_destination_writes()
    {
        await using var db = fixture.NewContext();
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualClock(now);
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await ConnectionsAsync(db, source, target);
        var scan = Snapshot(source, now.AddMinutes(-7),
            Enumerable.Range(0, 601).Select(i =>
                new RepositoryRef("repo-1", $"refs/heads/{i:0000}", $"sha-{i:0000}")).ToArray(),
            includeSecurity: true);
        await StageAsync(db, clock, scan);
        var service = NewService(db, clock);
        var run = await service.EnqueueAsync(scan.Id, target, Guid.NewGuid(), default);
        var again = await service.EnqueueAsync(scan.Id, target, Guid.NewGuid(), default);
        Assert.Equal(run.Id, again.Id);
        Assert.Equal(IncrementalPreparationStates.Queued, run.State);
        Assert.Equal("PreparationNotReady",
            (await Assert.ThrowsAsync<IncrementalPreparationException>(() =>
                service.ListItemsAsync(run.Id, null, null, 100, default))).Code);
        var processor = new IncrementalPreparationProcessor(
            new PostgresIncrementalPreparationRepository(db, clock));
        Assert.True(await processor.RunOnceAsync(default, source));
        Assert.False(await processor.RunOnceAsync(default, source));
        var ready = await service.GetAsync(run.Id, default);
        Assert.NotNull(ready);
        Assert.Equal(IncrementalPreparationStates.Prepared, ready.State);
        Assert.Equal(605, ready.ItemCount); // project + repo + 601 refs + group + raw ACE
        Assert.Equal(2, ready.SensitiveItemCount);
        Assert.Equal(64, ready.EvidenceSha256!.Length);
        Assert.All(ready.EvidenceSha256, c => Assert.True(Uri.IsHexDigit(c)));
        Assert.Equal(1, ready.Attempts);
        Assert.Equal(605, await db.IncrementalPreparationItems.AsNoTracking()
            .CountAsync(x => x.PreparationId == run.Id));

        var first = await service.ListItemsAsync(run.Id, null, null, 100, default);
        Assert.NotNull(first);
        Assert.Equal(100, first.Items.Count);
        Assert.NotNull(first.NextAfterType);
        Assert.NotNull(first.NextAfterId);
        Assert.All(first.Items, item =>
        {
            Assert.Equal("NeedsReview", item.ReviewState);
            Assert.Equal("Added", item.ChangeKind);
            Assert.Null(item.PreviousFingerprint);
            Assert.NotNull(item.CurrentFingerprint);
            Assert.Equal(1, item.Revision);
        });
        var second = await service.ListItemsAsync(
            run.Id, first.NextAfterType, first.NextAfterId, 100, default);
        Assert.Equal(100, second!.Items.Count);
        Assert.DoesNotContain(second.Items, x => first.Items.Any(y =>
            x.AssetType == y.AssetType && x.SourceId == y.SourceId));
        var all = await db.IncrementalPreparationItems.AsNoTracking()
            .Where(x => x.PreparationId == run.Id).ToArrayAsync();
        Assert.Equal(2, all.Count(x => x.ReviewCategory == "Security"));
        Assert.DoesNotContain(all, x => x.ReviewState != "NeedsReview");
        Assert.Contains(all, x => x.AssetType == "acl" && x.ReviewCategory == "Security");

        var events = await service.EventsAsync(run.Id, default);
        Assert.Equal(["Queued", "Claimed", "Prepared"],
            events.Select(x => x.EventType).ToArray());
        Assert.Equal(run.Id, (await service.EnqueueAsync(
            scan.Id, target, Guid.NewGuid(), default)).Id);
        Assert.Equal(3, (await service.EventsAsync(run.Id, default)).Count);
    }

    [Fact]
    public async Task Transient_retry_backoff_is_fenced_and_exactly_one_retry_can_prepare()
    {
        await using var db = fixture.NewContext();
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualClock(now);
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await ConnectionsAsync(db, source, target);
        var scan = Snapshot(source, now.AddMinutes(-4), [new("repo-1", "refs/heads/main", "sha")]);
        await StageAsync(db, clock, scan);
        var run = await NewService(db, clock).EnqueueAsync(scan.Id, target, Guid.NewGuid(), default);
        var queue = new PostgresIncrementalPreparationRepository(db, clock);
        var first = await queue.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5), default, source);
        Assert.NotNull(first);
        Assert.Equal(run.Id, first.Id);
        // The real stale-token protection returns a typed error with no writes.
        Assert.Equal("IncrementalLeaseLost", (await Assert.ThrowsAsync<IncrementalPreparationException>(
            () => queue.PrepareAsync(first with { LeaseToken = Guid.NewGuid() }, default))).Code);
        Assert.True(await queue.FailAsync(first, "TransientDatabaseFailure", false, default));
        Assert.Null(await queue.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5), default, source));
        clock.Advance(TimeSpan.FromSeconds(11));
        var second = await queue.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5), default, source);
        Assert.NotNull(second);
        Assert.Equal(2, second.Attempt);
        Assert.Equal("IncrementalLeaseLost",
            (await Assert.ThrowsAsync<IncrementalPreparationException>(() =>
                queue.PrepareAsync(first, default))).Code);
        Assert.True(await queue.PrepareAsync(second, default));
        var ready = await queue.GetAsync(run.Id, default);
        Assert.Equal("Prepared", ready!.State);
        Assert.Equal(3, ready.ItemCount);
        Assert.Equal(["Queued","Claimed","RetryScheduled","Claimed","Prepared"],
            (await queue.EventsAsync(run.Id, default)).Select(x => x.EventType).ToArray());
        Assert.False(await queue.FailAsync(second, "LateFailure", true, default));
    }

    [Fact]
    public async Task Expired_worker_reclaim_never_allows_an_old_lease_to_publish()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var source = Guid.NewGuid(); var destination = Guid.NewGuid();
        await ConnectionsAsync(db, source, destination);
        var scan = Snapshot(source, clock.GetUtcNow().AddMinutes(-3),
            [new("repo-1", "refs/heads/main", "sha")]);
        await StageAsync(db, clock, scan);
        var queue = new PostgresIncrementalPreparationRepository(db, clock);
        var run = await queue.EnqueueAsync(scan.Id, source, destination, Guid.NewGuid(), default);
        var one = await queue.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5), default, source);
        Assert.NotNull(one);
        Assert.Equal(run.Id, one.Id);
        clock.Advance(TimeSpan.FromMinutes(6));
        var two = await queue.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5), default, source);
        Assert.NotNull(two);
        Assert.Equal(one.Id, two.Id);
        Assert.NotEqual(one.LeaseToken, two.LeaseToken);
        Assert.Equal("IncrementalLeaseLost",
            (await Assert.ThrowsAsync<IncrementalPreparationException>(() =>
                queue.PrepareAsync(one, default))).Code);
        Assert.Empty(await db.IncrementalPreparationItems.AsNoTracking()
            .Where(x => x.PreparationId == run.Id).ToArrayAsync());
        Assert.True(await queue.PrepareAsync(two, default));
        Assert.Equal("Prepared", (await queue.GetAsync(run.Id, default))!.State);
    }

    [Fact]
    public async Task Newer_source_snapshot_invalidates_queued_old_plan_without_partial_items()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var source = Guid.NewGuid(); var destination = Guid.NewGuid();
        await ConnectionsAsync(db, source, destination);
        var a = Snapshot(source, clock.GetUtcNow().AddMinutes(-6),
            [new("repo-1", "refs/heads/main", "first")]);
        var b = Snapshot(source, clock.GetUtcNow().AddMinutes(-4),
            [new("repo-1", "refs/heads/main", "second")]);
        await StageAsync(db, clock, a);
        var queue = new PostgresIncrementalPreparationRepository(db, clock);
        var run = await queue.EnqueueAsync(a.Id, source, destination, Guid.NewGuid(), default);
        await StageAsync(db, clock, b);
        var lease = await queue.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5), default, source);
        Assert.NotNull(lease);
        Assert.Equal(run.Id, lease.Id);
        Assert.Equal("IncrementalSnapshotSuperseded",
            (await Assert.ThrowsAsync<IncrementalPreparationException>(() =>
                queue.PrepareAsync(lease, default))).Code);
        Assert.True(await queue.FailAsync(lease, "IncrementalSnapshotSuperseded", true, default));
        Assert.Equal("Failed", (await queue.GetAsync(run.Id, default))!.State);
        Assert.Empty(await db.IncrementalPreparationItems.AsNoTracking()
            .Where(x => x.PreparationId == run.Id).ToArrayAsync());
        Assert.Equal("IncrementalSnapshotSuperseded",
            (await Assert.ThrowsAsync<IncrementalPreparationException>(() =>
                queue.EnqueueAsync(a.Id, source, destination, Guid.NewGuid(), default))).Code);
        var newer = await queue.EnqueueAsync(b.Id, source, destination, Guid.NewGuid(), default);
        Assert.NotEqual(run.Id, newer.Id);
    }

    [Fact]
    public async Task Damaged_journal_fails_closed_and_records_permanent_safe_error()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var source = Guid.NewGuid(); var destination = Guid.NewGuid();
        await ConnectionsAsync(db, source, destination);
        var scan = Snapshot(source, clock.GetUtcNow(),
            [new("repo-1", "refs/heads/main", "sha")]);
        await StageAsync(db, clock, scan);
        var queue = new PostgresIncrementalPreparationRepository(db, clock);
        var run = await queue.EnqueueAsync(scan.Id, source, destination, Guid.NewGuid(), default);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM staging_asset_changes
            WHERE snapshot_id = {scan.Id} AND asset_type = {"ref"}
            """);
        Assert.True(await new IncrementalPreparationProcessor(queue).RunOnceAsync(default, source));
        var failed = await queue.GetAsync(run.Id, default);
        Assert.Equal("Failed", failed!.State);
        Assert.Equal("IncrementalJournalMismatch", failed.LastErrorCode);
        Assert.Null(failed.EvidenceSha256);
        Assert.Equal(0, failed.ItemCount);
        Assert.Empty(await db.IncrementalPreparationItems.AsNoTracking()
            .Where(x => x.PreparationId == run.Id).ToArrayAsync());
        Assert.Equal(["Queued", "Claimed", "Failed"],
            (await queue.EventsAsync(run.Id, default)).Select(x => x.EventType).ToArray());
    }

    [Fact]
    public async Task Zero_delta_has_valid_hash_and_preparation_remains_review_only()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var source = Guid.NewGuid(); var destination = Guid.NewGuid();
        await ConnectionsAsync(db, source, destination);
        var first = Snapshot(source, clock.GetUtcNow().AddMinutes(-4),
            [new("repo-1", "refs/heads/main", "same")]);
        var second = Snapshot(source, clock.GetUtcNow().AddMinutes(-3),
            [new("repo-1", "refs/heads/main", "same")]);
        await StageAsync(db, clock, first);
        await StageAsync(db, clock, second);
        var queue = new PostgresIncrementalPreparationRepository(db, clock);
        var run = await queue.EnqueueAsync(second.Id, source, destination, Guid.NewGuid(), default);
        Assert.True(await new IncrementalPreparationProcessor(queue).RunOnceAsync(default, source));
        var prepared = await queue.GetAsync(run.Id, default);
        Assert.Equal(0, prepared!.ItemCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())), prepared.EvidenceSha256);
        Assert.Empty((await queue.ListItemsAsync(run.Id, null, null, 50, default))!.Items);
    }

    [Fact]
    public async Task Invalid_destination_and_invalid_paging_are_rejected_before_reading_items()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var source = Guid.NewGuid(); var destination = Guid.NewGuid();
        await ConnectionsAsync(db, source, destination, destinationVerified: false);
        var scan = Snapshot(source, clock.GetUtcNow().AddMinutes(-3),
            [new("repo-1", "refs/heads/main", "sha")]);
        await StageAsync(db, clock, scan);
        var service = NewService(db, clock);
        Assert.Equal("UnverifiedPreparationDestination",
            (await Assert.ThrowsAsync<IncrementalPreparationException>(() =>
                service.EnqueueAsync(scan.Id, destination, Guid.NewGuid(), default))).Code);
        Assert.Equal("InvalidPreparationPage",
            (await Assert.ThrowsAsync<IncrementalPreparationException>(() =>
                service.ListItemsAsync(Guid.NewGuid(), "ref", null, 100, default))).Code);
        Assert.Equal("InvalidPreparationPage",
            (await Assert.ThrowsAsync<IncrementalPreparationException>(() =>
                service.ListItemsAsync(Guid.NewGuid(), null, null, 101, default))).Code);
    }

    [Fact]
    public async Task Two_API_replicas_enqueue_one_pair_and_one_claim_without_duplicate_audit()
    {
        var source = Guid.NewGuid(); var destination = Guid.NewGuid();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using (var setup = fixture.NewContext())
        {
            var scan = Snapshot(source, clock.GetUtcNow().AddMinutes(-2),
                [new("repo-1", "refs/heads/main", "sha")]);
            await StageAsync(setup, clock, scan);
            await using var a = fixture.NewContext();
            await using var b = fixture.NewContext();
            var first = new PostgresIncrementalPreparationRepository(a, clock);
            var second = new PostgresIncrementalPreparationRepository(b, clock);
            var pair = await Task.WhenAll(
                first.EnqueueAsync(scan.Id, source, destination, Guid.NewGuid(), default),
                second.EnqueueAsync(scan.Id, source, destination, Guid.NewGuid(), default));
            Assert.Equal(pair[0].Id, pair[1].Id);
            var leases = await Task.WhenAll(
                first.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5), default, source),
                second.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5), default, source));
            Assert.Single(leases, x => x is not null);
            var claimed = leases.Single(x => x is not null)!;
            var owner = leases[0] is null ? second : first;
            Assert.True(await owner.PrepareAsync(claimed, default));
            Assert.Equal(["Queued","Claimed","Prepared"],
                (await first.EventsAsync(pair[0].Id, default))
                    .Select(x => x.EventType).ToArray());
        }
    }

    private static IncrementalPreparationService NewService(AppDbContext db, TimeProvider clock) =>
        new(new PostgresIncrementalPreparationRepository(db, clock),
            new PostgresStagingRepository(db, clock), new PostgresConnectionRepository(db));

    private static async Task ConnectionsAsync(
        AppDbContext db, Guid source, Guid destination, bool destinationVerified = true)
    {
        var now = DateTimeOffset.UtcNow;
        var repo = new PostgresConnectionRepository(db);
        await repo.SaveAsync(new(source, "Source", "azure-devops-services",
            ConnectionDirection.Source, "az-org", "not-a-credential", now, now,
            "Verified", "source", "read_only"), default);
        await repo.SaveAsync(new(destination, "GitHub", "github-enterprise-cloud",
            ConnectionDirection.Destination, "gh-org", "not-a-credential", now, now,
            destinationVerified ? "Verified" : "Unverified",
            "target", "read_only"), default);
    }

    private static DiscoveryInventory Snapshot(Guid source, DateTimeOffset at,
        IReadOnlyList<RepositoryRef> refs, bool includeSecurity = false)
    {
        var id = Guid.NewGuid();
        return new DiscoveryInventory {
            Id = id, ConnectionId = source,
            SourceProvider = "azure-devops-services", Organization = "az-org",
            CollectedAtUtc = at,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = at,
                Projects = [new SourceProject("proj-1", "Project",
                    [new SourceRepository("repo-1", "proj-1", "Repo", VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            Refs = refs,
            Groups = includeSecurity ? [new DiscoveryGroup("vssgp.group", "Operators", null)] : [],
            AclEvidence = includeSecurity
                ? [new RepositoryAclEvidence("repo-1", "repoV2/proj-1/repo-1",
                    "vssgp.group", 2, 0, false, "repository")] : [],
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
    }

    private static async Task StageAsync(AppDbContext db, TimeProvider clock,
        DiscoveryInventory scan)
    {
        var staging = new PostgresStagingRepository(db, clock);
        var normalized = StagingAssetNormalizer.Normalize(scan);
        await staging.ApplyAsync(scan, normalized.Assets, normalized.CompleteTypes, default);
    }

    private sealed class ManualClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
        public void Advance(TimeSpan span) => value = value.Add(span);
    }
}
