using System.Text.Json;
using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubCheckpointTargetRefReviewTests
{
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);
    private static readonly string C = new('c', 64);

    private static GitHubCheckpointTargetRefReviewItem Item(
        string name, string kind, bool observed)
    {
        var previous = kind == "Added" ? null : A;
        var current = kind == "Missing" ? null : B;
        var delta = GitHubSourceCheckpointRefChangeIntegrity.Parse(
            new StagingChangeView("ref",
                JsonSerializer.Serialize(new[] { "repo-1", name }),
                kind, previous, current, 2), "target-1");
        return new(delta, observed,
            GitHubCheckpointTargetRefReviewIntegrity.ReviewState(
                kind, observed));
    }

    [Theory]
    [InlineData("Added", false, "SourceAddedTargetAbsentReview")]
    [InlineData("Added", true, "SourceAddedTargetPresentReview")]
    [InlineData("Restored", false, "SourceRestoredTargetAbsentReview")]
    [InlineData("Restored", true, "SourceRestoredTargetPresentReview")]
    [InlineData("Changed", false, "SourceChangedTargetAbsentBlocked")]
    [InlineData("Changed", true, "SourceChangedTargetPresentBlocked")]
    [InlineData("Missing", false, "SourceMissingTargetAbsentPreserved")]
    [InlineData("Missing", true, "SourceMissingTargetPresentPreserved")]
    public void F093_review_does_not_claim_sha_equality_or_write_authority(
        string kind, bool observed, string expected)
    {
        var item = Item("refs/heads/main", kind, observed);
        Assert.Equal(expected, item.ReviewState);
        Assert.DoesNotContain("Ready", item.ReviewState);
        Assert.DoesNotContain("Push", item.ReviewState);
        Assert.DoesNotContain("Complete", item.ReviewState);
    }

    [Fact]
    public void F093_review_rejects_forged_flags_cross_repo_and_cursor()
    {
        var first = Item("refs/heads/a", "Added", false);
        var next = Item("refs/heads/b", "Changed", true);
        var page = new GitHubCheckpointTargetRefReviewPage(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "repo-1", "target-1", A, B, C, DateTimeOffset.UtcNow, 2,
            [first, next], next.SourceChange.SourceId,
            false, false, false, false);
        GitHubCheckpointTargetRefReviewIntegrity.VerifyPage(page, 2);
        var mutations = new[] {
            page with { CanPush = true },
            page with { CanRetry = true },
            page with { CanDispatch = true },
            page with { MigrationComplete = true },
            page with { SourceRefManifestSha256 = "invalid" },
            page with { TargetEvidenceSha256 = "invalid" },
            page with { Items = [first, first] },
            page with { Items = [next, first] },
            page with { NextAfter = first.SourceChange.SourceId },
            page with { Items = [first, next, first] },
            page with { Items = [first with { TargetRefObserved = true }, next] },
            page with { Items = [first with { SourceChange =
                first.SourceChange with { SourceRepositoryId = "repo-2" } }, next] }
        };
        foreach (var bad in mutations)
            Assert.Throws<GitHubSourceSyncCheckpointException>(() =>
                GitHubCheckpointTargetRefReviewIntegrity.VerifyPage(bad, 2));
        Assert.Throws<GitHubSourceSyncCheckpointException>(() =>
            GitHubCheckpointTargetRefReviewIntegrity.VerifyPage(
                page, 2, JsonSerializer.Serialize(new[] {
                    "foreign-repo", "refs/heads/main"
                })));
    }

    [Fact]
    public void F093_empty_review_is_safe_but_not_a_migration_receipt()
    {
        var page = new GitHubCheckpointTargetRefReviewPage(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "repo-1", "target-1", A, B, C,
            DateTimeOffset.UtcNow, 0, [],
            null, false, false, false, false);
        GitHubCheckpointTargetRefReviewIntegrity.VerifyPage(page, 1);
        Assert.Empty(page.Items);
        Assert.Throws<GitHubSourceSyncCheckpointException>(() =>
            GitHubCheckpointTargetRefReviewIntegrity.ReviewState("Deleted", true));
    }
}
