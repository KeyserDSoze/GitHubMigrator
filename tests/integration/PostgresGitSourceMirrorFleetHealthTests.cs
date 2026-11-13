using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL state transitions, with real local SHA-1 Git source
// verification. No GitHub request or simulated migration success.
public sealed class PostgresGitSourceMirrorFleetHealthTests(
    PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider();
        await GitSourceMirrorLifecycleSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Aggregates_all_states_using_one_database_instant_without_leaking_handles()
    {
        using var git = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var active = git.CreatePlan();
        var expired = git.CopyForAnotherWorkpack(active);
        var verified = git.CopyForAnotherWorkpack(active);
        var quarantine = git.CopyForAnotherWorkpack(active);

        await using var db = fixture.NewContext();
        var lifecycle = new PostgresGitSourceMirrorLifecycleJournal(db, new(true));
        var reader = new PostgresGitSourceMirrorFleetHealthReader(db, new(true));
        var old = await reader.ReadAsync(default);

        var activeLease = await lifecycle.AcquireAsync(active, Guid.NewGuid(), default);
        var expiredLease = await lifecycle.AcquireAsync(expired, Guid.NewGuid(), default);
        var verifiedLease = await lifecycle.AcquireAsync(verified, Guid.NewGuid(), default);
        var quarantineLease = await lifecycle.AcquireAsync(quarantine, Guid.NewGuid(), default);

        // In the production DB this column is updated by the current
        // owner on heartbeat. Move these two local test deadlines only.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE git_source_mirror_lifecycles
            SET lease_expires_at_utc=clock_timestamp() + interval '2 minutes'
            WHERE mirror_id={active.SourceMirrorId}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE git_source_mirror_lifecycles
            SET lease_expires_at_utc=clock_timestamp() - interval '3 minutes'
            WHERE mirror_id={expired.SourceMirrorId}
            """);

        var physical = await git.Materializer().MaterializeAsync(verified, default);
        await lifecycle.CompleteAsync(verified, verifiedLease, physical, default);
        await lifecycle.QuarantineAsync(
            quarantine, quarantineLease, "SourceMirrorGitClosureInvalid", default);

        var eventsBefore = await lifecycle.EventsAsync(
            quarantine.SourceMirrorId, default);
        var nowBefore = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        var health = await reader.ReadAsync(default);
        var nowAfter = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        var eventsAfter = await lifecycle.EventsAsync(
            quarantine.SourceMirrorId, default);

        Assert.InRange(health.ObservedAtUtc, nowBefore, nowAfter);
        Assert.True(health.TotalMirrors >= old.TotalMirrors + 4);
        Assert.True(health.ActiveLeases >= 1);
        Assert.True(health.ExpiredLeases >= 1);
        Assert.True(health.ExpiringSoonLeases >= 1);
        Assert.True(health.VerifiedMirrors >= 1);
        Assert.True(health.RecoveryRequiredMirrors >= 1);
        Assert.Equal(health.TotalMirrors,
            health.ActiveLeases + health.ExpiredLeases +
            health.VerifiedMirrors + health.RecoveryRequiredMirrors);
        Assert.True(health.AttentionRequired);
        Assert.NotNull(health.OldestExpiredAtUtc);
        Assert.True(health.OldestExpiredAtUtc <= health.ObservedAtUtc);
        Assert.Equal(eventsBefore, eventsAfter);

        var json = JsonSerializer.Serialize(health);
        foreach (var secretField in new[] {
            "WorkerId", "LeasedBy", "LeaseToken", "BundleSha256",
            "SourceSnapshotId", "WorkpackId", "ObjectSetSha256",
            "RefsSha256", "MirrorId", "FailureCode", "Path", "Token"
        })
            Assert.DoesNotContain(secretField, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(active.SourceMirrorId.ToString("D"),
            json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(quarantineLease.LeaseToken.ToString("D"),
            json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Feature_off_denies_health_reads_without_touching_database()
    {
        await using var db = fixture.NewContext();
        var reader = new PostgresGitSourceMirrorFleetHealthReader(db, new(false));
        var ex = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
            () => reader.ReadAsync(default));
        Assert.Equal("SourceMirrorLifecycleDisabled", ex.Code);
    }
}
