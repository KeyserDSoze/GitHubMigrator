using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresMigrationPlanDraftIntegrationTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Draft_jsonb_persists_selections_and_revision_across_contexts()
    {
        var snapshot = Guid.NewGuid();
        var target = Guid.NewGuid();
        var selection = new PlanSelectionInput("repo-1", "renamed-repo", HistoryStrategy.GitMirror);
        await using (var context = fixture.NewContext())
        {
            var repo = new PostgresMigrationPlanDraftRepository(context);
            Assert.Null(await repo.GetAsync(snapshot, target, default));
            Assert.True(await repo.SaveAsync(NewDraft(snapshot, target, 1, [selection]), 0, default));
        }
        await using (var context = fixture.NewContext())
        {
            var stored = await new PostgresMigrationPlanDraftRepository(context)
                .GetAsync(snapshot, target, default);
            Assert.NotNull(stored);
            Assert.Equal(1, stored.Revision);
            Assert.Equal("gh-org", stored.TargetOrganization);
            Assert.Equal("renamed-repo", Assert.Single(stored.Repositories).TargetRepositoryName);
        }
    }

    [Fact]
    public async Task Stale_tabs_cannot_update_the_same_plan_revision()
    {
        var snapshot = Guid.NewGuid();
        var target = Guid.NewGuid();
        await using (var context = fixture.NewContext())
        {
            var repo = new PostgresMigrationPlanDraftRepository(context);
            Assert.True(await repo.SaveAsync(NewDraft(snapshot, target, 1,
                [new("repo-1", "original", HistoryStrategy.GitMirror)]), 0, default));
        }
        await using var adminA = fixture.NewContext();
        await using var adminB = fixture.NewContext();
        var first = new PostgresMigrationPlanDraftRepository(adminA);
        var second = new PostgresMigrationPlanDraftRepository(adminB);
        Assert.Equal(1, (await first.GetAsync(snapshot, target, default))!.Revision);
        Assert.Equal(1, (await second.GetAsync(snapshot, target, default))!.Revision);
        Assert.True(await second.SaveAsync(NewDraft(snapshot, target, 2,
            [new("repo-1", "from-b", HistoryStrategy.GitMirror)]), 1, default));
        Assert.False(await first.SaveAsync(NewDraft(snapshot, target, 2,
            [new("repo-1", "stale-from-a", HistoryStrategy.GitMirror)]), 1, default));
        Assert.False(await first.SaveAsync(NewDraft(snapshot, target, 1, []), 0, default));
        await using var verify = fixture.NewContext();
        var latest = await new PostgresMigrationPlanDraftRepository(verify).GetAsync(snapshot, target, default);
        Assert.Equal(2, latest!.Revision);
        Assert.Equal("from-b", Assert.Single(latest.Repositories).TargetRepositoryName);
    }

    [Fact]
    public async Task Two_destinations_and_snapshots_do_not_leak_selection_state()
    {
        var snapshot = Guid.NewGuid();
        var secondSnapshot = Guid.NewGuid();
        var target = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        await using var db = fixture.NewContext();
        var repo = new PostgresMigrationPlanDraftRepository(db);
        Assert.True(await repo.SaveAsync(NewDraft(snapshot, target, 1,
            [new("repo", "one", HistoryStrategy.GitMirror)]), 0, default));
        Assert.True(await repo.SaveAsync(NewDraft(snapshot, secondTarget, 1,
            [new("repo", "two", HistoryStrategy.GitMirror)]), 0, default));
        Assert.True(await repo.SaveAsync(NewDraft(secondSnapshot, target, 1,
            [new("repo", "three", HistoryStrategy.GitMirror)]), 0, default));
        Assert.Equal("one", (await repo.GetAsync(snapshot, target, default))!.Repositories.Single().TargetRepositoryName);
        Assert.Equal("two", (await repo.GetAsync(snapshot, secondTarget, default))!.Repositories.Single().TargetRepositoryName);
        Assert.Equal("three", (await repo.GetAsync(secondSnapshot, target, default))!.Repositories.Single().TargetRepositoryName);
    }

    private static MigrationPlanDraft NewDraft(Guid snapshot, Guid destination, int revision,
        IReadOnlyList<PlanSelectionInput> selections) =>
        new(snapshot, destination, "gh-org", revision, DateTimeOffset.UtcNow, selections);
}
