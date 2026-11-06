using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// No shared queue truncation: this test can coexist with independently
// enqueued work from other parallel PostgreSQL integration fixtures.
public sealed class PostgresMigrationJobSourceScopedClaimTests(
    PostgresDraftFixture fixture) : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Claiming_one_source_does_not_steal_or_mutate_another_source_job()
    {
        await using var db = fixture.NewContext();
        var repository = new PostgresMigrationJobRepository(db, TimeProvider.System);
        var sourceA = Guid.NewGuid();
        var sourceB = Guid.NewGuid();
        var first = await repository.EnqueueDiscoveryAsync(
            sourceA, Guid.NewGuid(), Guid.NewGuid(), default);
        var second = await repository.EnqueueDiscoveryAsync(
            sourceB, Guid.NewGuid(), Guid.NewGuid(), default);

        var leaseB = await repository.ClaimForSourceAsync(
            sourceB, Guid.NewGuid(), TimeSpan.FromMinutes(3), default);
        Assert.NotNull(leaseB);
        Assert.Equal(second.Id, leaseB.Id);
        Assert.Equal(sourceB, leaseB.SourceConnectionId);
        Assert.Equal(MigrationJobStates.Queued,
            (await repository.GetAsync(first.Id, default))!.State);

        var leaseA = await repository.ClaimForSourceAsync(
            sourceA, Guid.NewGuid(), TimeSpan.FromMinutes(3), default);
        Assert.NotNull(leaseA);
        Assert.Equal(first.Id, leaseA.Id);
        Assert.Equal(sourceA, leaseA.SourceConnectionId);
        Assert.NotEqual(leaseA.LeaseToken, leaseB.LeaseToken);
        Assert.Single((await repository.EventsAsync(first.Id, default))
            .Where(x => x.EventType == "Claimed"));
        Assert.Single((await repository.EventsAsync(second.Id, default))
            .Where(x => x.EventType == "Claimed"));

        Assert.True(await repository.FailAsync(
            leaseA, "TestSourceScopeCleanup", permanent: true, default));
        Assert.True(await repository.FailAsync(
            leaseB, "TestSourceScopeCleanup", permanent: true, default));
    }

    [Fact]
    public async Task Missing_or_empty_source_is_never_a_global_fallback()
    {
        await using var db = fixture.NewContext();
        var repository = new PostgresMigrationJobRepository(db, TimeProvider.System);
        await Assert.ThrowsAsync<MigrationJobInputException>(() =>
            repository.ClaimForSourceAsync(Guid.Empty, Guid.NewGuid(),
                TimeSpan.FromMinutes(3), default));
        Assert.Null(await repository.ClaimForSourceAsync(
            Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(3), default));
    }
}
