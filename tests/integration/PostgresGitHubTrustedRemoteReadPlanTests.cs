using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresGitHubTrustedRemoteReadPlanTests(
    PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o =>
                o.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider();
        await GitHubTransferAttemptSchema.InitializeAsync(services);
        await GitHubRemoteSandboxFenceSchema.InitializeAsync(services);
        await GitHubRemoteTestGrantSchema.InitializeAsync(services);
        await GitHubRemoteReadbackSchema.InitializeAsync(services);
        await GitHubRemoteTransmitHazardSchema.InitializeAsync(services);
        await GitHubRemoteObjectReconciliationSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Cannot_use_a_fake_source_snapshot_or_plan_not_in_F029_database()
    {
        var fixtureData = new RemoteObjectReconciliationFixture();
        await using var db = fixture.NewContext();
        await fixtureData.SeedAsync(db);
        var evidence = fixtureData.Evidence;
        var command = Command(evidence);

        // F-058's synthetic hash proof had no persisted F-029 plan.
        // F-059 must never accept that alone as a real source snapshot.
        var ex = await Assert.ThrowsAsync<GitHubTrustedRemoteReadException>(
            () => Reader(db, fixtureData).ReadAsync(command, default));
        Assert.Equal("TrustedRemoteReadSourcePlanUnavailable", ex.Code);
        Assert.Equal(0, await db.GitHubRemoteObjectReconciliations.CountAsync(
            x => x.HazardId == evidence.HazardId));
    }

    [Fact]
    public async Task F050_released_and_forged_candidate_hashes_fail_before_Git()
    {
        var fixtureData = new RemoteObjectReconciliationFixture();
        await using var db = fixture.NewContext();
        await fixtureData.SeedAsync(db);
        var evidence = fixtureData.Evidence;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_sandbox_fences
            SET state='Released'
            WHERE repository_id={evidence.RepositoryId}
            """);
        var ex = await Assert.ThrowsAsync<GitHubTrustedRemoteReadException>(
            () => Reader(db, fixtureData).ReadAsync(
                Command(evidence), default));
        Assert.Equal("TrustedRemoteReadBarrierChanged", ex.Code);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_sandbox_fences
            SET state='ReviewedWriteDisabled'
            WHERE repository_id={evidence.RepositoryId}
            """);
        var forged = Command(evidence) with {
            CreateCandidates = [
                new GitMirrorRef("refs/heads/feature", new string('f', 40)),
                new GitMirrorRef("refs/tags/v2", new string('d', 40))
            ]
        };
        ex = await Assert.ThrowsAsync<GitHubTrustedRemoteReadException>(
            () => Reader(db, fixtureData).ReadAsync(forged, default));
        Assert.Equal("TrustedRemoteReadStoredHashesMismatch", ex.Code);
        Assert.Equal(0, await db.GitHubRemoteObjectReconciliations.CountAsync(
            x => x.HazardId == evidence.HazardId));
    }

    private static GitHubTrustedRemoteReadCommand Command(
        GitHubRemoteObjectReconciliationEvidence e) =>
        new(e.GrantId, e.ReadbackId, e.HazardId, e.RepositoryId,
            e.ObserverId, e.OriginalBaseline, e.CreateCandidates,
            e.MirrorRequest.TokenExpiresAtUtc);

    private static PostgresGitHubTrustedRemoteReadPlanReader Reader(
        AppDbContext db, RemoteObjectReconciliationFixture f)
    {
        var clock = TimeProvider.System;
        return new(db, clock,
            new PostgresIncrementalOperationPlanRepository(db, clock),
            new PostgresVerifiedSourceGitRefReader(db, null!),
            f.Options);
    }
}
