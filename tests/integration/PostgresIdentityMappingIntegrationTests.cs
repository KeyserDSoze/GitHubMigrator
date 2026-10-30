using Xunit;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Integration.Tests;

// Runs against a REAL ephemeral PostgreSQL 17 GitHub Actions service. This class
// deliberately requires the test connection: failure to configure is NOT a skip.
public sealed class PostgresDraftFixture : IAsyncLifetime
{
    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("GITHUBMIGRATOR_TEST_POSTGRES")
        ?? throw new InvalidOperationException("GITHUBMIGRATOR_TEST_POSTGRES is required for DB integration tests.");

    public AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

public sealed class PostgresIdentityMappingIntegrationTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Empty_draft_can_be_inserted_and_fetched_through_EF_jsonb_mapping()
    {
        var snapshot = Guid.NewGuid();
        var target = Guid.NewGuid();
        var draft = NewDraft(snapshot, target, 1, [new("aad.first", "github-first")]);
        await using (var first = fixture.NewContext())
        {
            var repository = new PostgresIdentityMappingDraftRepository(first);
            Assert.Null(await repository.GetAsync(snapshot, target, CancellationToken.None));
            Assert.True(await repository.SaveAsync(draft, 0, CancellationToken.None));
        }
        await using (var second = fixture.NewContext())
        {
            var saved = await new PostgresIdentityMappingDraftRepository(second)
                .GetAsync(snapshot, target, CancellationToken.None);
            Assert.NotNull(saved);
            Assert.Equal(1, saved.Revision);
            Assert.Equal("real-organization", saved.TargetOrganization);
            Assert.Equal("github-first", Assert.Single(saved.Mappings).GitHubLogin);
        }
    }

    [Fact]
    public async Task Two_administrators_cannot_replace_each_other_using_stale_revision()
    {
        var snapshot = Guid.NewGuid();
        var target = Guid.NewGuid();
        var original = NewDraft(snapshot, target, 1, [new("aad.first", "github-a")]);
        await using (var first = fixture.NewContext())
            Assert.True(await new PostgresIdentityMappingDraftRepository(first).SaveAsync(
                original, 0, CancellationToken.None));

        await using var adminA = fixture.NewContext();
        await using var adminB = fixture.NewContext();
        var a = new PostgresIdentityMappingDraftRepository(adminA);
        var b = new PostgresIdentityMappingDraftRepository(adminB);
        Assert.Equal(1, (await a.GetAsync(snapshot, target, CancellationToken.None))!.Revision);
        Assert.Equal(1, (await b.GetAsync(snapshot, target, CancellationToken.None))!.Revision);

        Assert.True(await b.SaveAsync(NewDraft(snapshot, target, 2, [new("aad.first", "github-b")]),
            1, CancellationToken.None));
        Assert.False(await a.SaveAsync(NewDraft(snapshot, target, 2, [new("aad.first", "github-overwrite")]),
            1, CancellationToken.None));
        Assert.False(await a.SaveAsync(NewDraft(snapshot, target, 1, [new("aad.first", "github-overwrite")]),
            0, CancellationToken.None));

        await using var verifyDb = fixture.NewContext();
        var verified = await new PostgresIdentityMappingDraftRepository(verifyDb)
            .GetAsync(snapshot, target, CancellationToken.None);
        Assert.Equal(2, verified!.Revision);
        Assert.Equal("github-b", Assert.Single(verified.Mappings).GitHubLogin);
    }

    [Fact]
    public async Task Independent_source_snapshots_and_target_connections_do_not_share_state()
    {
        var snapshot = Guid.NewGuid();
        var otherSnapshot = Guid.NewGuid();
        var target = Guid.NewGuid();
        var otherTarget = Guid.NewGuid();
        await using var db = fixture.NewContext();
        var repo = new PostgresIdentityMappingDraftRepository(db);
        Assert.True(await repo.SaveAsync(NewDraft(snapshot, target, 1, [new("aad.first", "one")]),
            0, CancellationToken.None));
        Assert.True(await repo.SaveAsync(NewDraft(otherSnapshot, target, 1, [new("aad.first", "two")]),
            0, CancellationToken.None));
        Assert.True(await repo.SaveAsync(NewDraft(snapshot, otherTarget, 1, [new("aad.first", "three")]),
            0, CancellationToken.None));
        Assert.Equal("one", Assert.Single((await repo.GetAsync(snapshot, target, CancellationToken.None))!.Mappings).GitHubLogin);
        Assert.Equal("two", Assert.Single((await repo.GetAsync(otherSnapshot, target, CancellationToken.None))!.Mappings).GitHubLogin);
        Assert.Equal("three", Assert.Single((await repo.GetAsync(snapshot, otherTarget, CancellationToken.None))!.Mappings).GitHubLogin);
    }

    [Fact]
    public async Task Jsonb_roundtrip_preserves_unicode_and_quotes_without_sql_interpolation()
    {
        var snapshot = Guid.NewGuid();
        var target = Guid.NewGuid();
        var draft = NewDraft(snapshot, target, 1,
            [new("vssgp;nome-\"ospite\"-à", "validated-login"), new("aad.second", "another-login")]);
        await using var db = fixture.NewContext();
        var repo = new PostgresIdentityMappingDraftRepository(db);
        Assert.True(await repo.SaveAsync(draft, 0, CancellationToken.None));
        var saved = await repo.GetAsync(snapshot, target, CancellationToken.None);
        Assert.Contains(saved!.Mappings, x => x.SourceIdentityId == "vssgp;nome-\"ospite\"-à");
        Assert.Equal(2, saved.Mappings.Count);
    }

    private static IdentityMappingDraft NewDraft(Guid snapshot, Guid target, int revision,
        IReadOnlyList<IdentityMapping> maps) =>
        new(snapshot, target, "real-organization", revision, DateTimeOffset.UtcNow, maps);
}
