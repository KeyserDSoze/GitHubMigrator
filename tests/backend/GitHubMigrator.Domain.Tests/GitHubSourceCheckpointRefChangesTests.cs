using System.Text.Json;
using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubSourceCheckpointRefChangesTests
{
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);

    private static StagingChangeView Ref(
        string repository, string reference, string kind,
        string? before, string? after, int revision = 1) =>
        new("ref", JsonSerializer.Serialize(new[] { repository, reference }),
            kind, before, after, revision);

    [Fact]
    public void F092_accepts_all_four_kinds_and_unmapped_ref_keys()
    {
        var rows = new[] {
            Ref("repo", "refs/heads/main", "Added", null, A),
            Ref("repo", "refs/heads/next", "Changed", A, B, 2),
            Ref("repo", "refs/tags/v1", "Restored", B, B, 3),
            Ref("unknown", "refs/tags/v2", "Missing", A, null, 4)
        };
        var result = rows.Select((r, index) =>
            GitHubSourceCheckpointRefChangeIntegrity.Parse(
                r, index == 3 ? null : "target")).ToArray();
        Assert.Equal(4, result.Length);
        Assert.Null(result[^1].TargetRepositoryName);
        Assert.Equal("refs/heads/main", result[0].RefName);
        var page = new GitHubSourceCheckpointRefChangePage(
            Guid.NewGuid(), Guid.NewGuid(), A, B, 4,
            result, null, false, false, false, false);
        GitHubSourceCheckpointRefChangeIntegrity.VerifyPage(page, 100);
        Assert.Throws<GitHubSourceSyncCheckpointException>(() =>
            GitHubSourceCheckpointRefChangeIntegrity.VerifyPage(
                page with { CanPush = true }, 100));
    }

    [Fact]
    public void F092_invalid_and_noncanonical_cursor_fails_closed()
    {
        var good = Ref("repo", "refs/heads/main", "Added", null, A);
        var invalid = new[] {
            good with { SourceId = "not-json" },
            good with { SourceId = "[ \"repo\",\"refs/heads/main\"]" },
            good with { SourceId = "[\"repo\"]" },
            good with { SourceId = "[\"repo\",\"HEAD\"]" },
            good with { CurrentFingerprint = "invalid" },
            good with { PreviousFingerprint = B },
            good with { Kind = "Unknown" },
            good with { Revision = 0 },
            good with { AssetType = "identity" }
        };
        foreach (var row in invalid)
            Assert.Throws<GitHubSourceSyncCheckpointException>(() =>
                GitHubSourceCheckpointRefChangeIntegrity.Parse(row, null));
    }

    [Fact]
    public void F092_ref_pages_have_no_duplicates_or_fabricated_cursor()
    {
        var ref1 = GitHubSourceCheckpointRefChangeIntegrity.Parse(
            Ref("repo", "refs/heads/a", "Added", null, A), "target");
        var ref2 = GitHubSourceCheckpointRefChangeIntegrity.Parse(
            Ref("repo", "refs/heads/b", "Added", null, B), "target");
        var page = new GitHubSourceCheckpointRefChangePage(
            Guid.NewGuid(), Guid.NewGuid(), A, B, 2,
            [ref1, ref2], ref2.SourceId,
            false, false, false, false);
        GitHubSourceCheckpointRefChangeIntegrity.VerifyPage(page, 2);
        foreach (var forged in new[] {
            page with { Items = [ref1, ref1] },
            page with { Items = [ref2, ref1] },
            page with { NextAfter = ref1.SourceId },
            page with { Items = [ref1, ref2, ref1] },
            page with { RefManifestSha256 = "fake" },
            page with { MigrationComplete = true }
        })
            Assert.Throws<GitHubSourceSyncCheckpointException>(() =>
                GitHubSourceCheckpointRefChangeIntegrity.VerifyPage(forged, 2));
    }
}
