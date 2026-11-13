using System.Text.Json;
using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubSourceRefChangeManifestTests
{
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);
    private static readonly string C = new('c', 64);

    private static StagingChangeView Row(string name, string kind,
        string? previous, string? current, int revision = 1) =>
        new("ref", JsonSerializer.Serialize(new[] { "repo-1", name }),
            kind, previous, current, revision);

    private static async IAsyncEnumerable<StagingChangeView> Stream(
        params StagingChangeView[] rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    [Fact]
    public async Task F091_streamed_proof_is_stable_but_bound_to_source_and_snapshot()
    {
        var source = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        var rows = new[] {
            Row("refs/heads/alpha", "Added", null, A),
            Row("refs/heads/beta", "Changed", A, B, 2),
            Row("refs/heads/gamma", "Missing", C, null, 4),
            Row("refs/tags/1.0", "Restored", A, A, 3)
        };
        var first = await GitHubSourceRefChangeManifestIntegrity
            .ComputeAsync(source, snapshot, Stream(rows));
        var replay = await GitHubSourceRefChangeManifestIntegrity
            .ComputeAsync(source, snapshot, Stream(rows));
        Assert.Equal(first, replay);
        Assert.Equal((1, 1, 1, 1),
            (first.Added, first.Changed, first.Restored, first.Missing));
        Assert.Equal(4, first.Total);
        GitHubSourceRefChangeManifestIntegrity.Verify(first);
        Assert.Equal(64, first.EvidenceSha256.Length);

        Assert.NotEqual(first.EvidenceSha256, (await
            GitHubSourceRefChangeManifestIntegrity.ComputeAsync(
                Guid.NewGuid(), snapshot, Stream(rows))).EvidenceSha256);
        Assert.NotEqual(first.EvidenceSha256, (await
            GitHubSourceRefChangeManifestIntegrity.ComputeAsync(
                source, Guid.NewGuid(), Stream(rows))).EvidenceSha256);
        Assert.NotEqual(first.EvidenceSha256, (await
            GitHubSourceRefChangeManifestIntegrity.ComputeAsync(
                source, snapshot, Stream(
                    rows[0], rows[1] with { CurrentFingerprint = C },
                    rows[2], rows[3]))).EvidenceSha256);
        Assert.NotEqual(first.EvidenceSha256, (await
            GitHubSourceRefChangeManifestIntegrity.ComputeAsync(
                source, snapshot, Stream(
                    rows[0], rows[1] with { Revision = 5 },
                    rows[2], rows[3]))).EvidenceSha256);
    }

    [Fact]
    public async Task F091_rejects_duplicate_unordered_and_noncanonical_evidence()
    {
        var valid = Row("refs/heads/main", "Added", null, A);
        var other = Row("refs/tags/release", "Changed", A, B, 2);
        var bad = new[] {
            valid with { AssetType = "identity" },
            valid with { SourceId = "[ \"repo-1\", \"refs/heads/main\" ]" },
            valid with { SourceId = "[\"repo-1\",\"foo\"]" },
            valid with { SourceId = "[\"repo-1\"]" },
            valid with { SourceId = "not-json" },
            valid with { Kind = "Deleted" },
            valid with { CurrentFingerprint = "not-a-sha" },
            valid with { PreviousFingerprint = A },
            valid with { Revision = 0 },
            other with { Kind = "Changed", CurrentFingerprint = A },
            other with { Kind = "Missing", CurrentFingerprint = B }
        };
        foreach (var row in bad)
            await Assert.ThrowsAsync<GitHubSourceRefChangeManifestException>(
                () => GitHubSourceRefChangeManifestIntegrity.ComputeAsync(
                    Guid.NewGuid(), Guid.NewGuid(), Stream(row)));

        foreach (var rows in new[] {
            new[] { valid, valid },
            new[] { other, valid }
        })
            await Assert.ThrowsAsync<GitHubSourceRefChangeManifestException>(
                () => GitHubSourceRefChangeManifestIntegrity.ComputeAsync(
                    Guid.NewGuid(), Guid.NewGuid(), Stream(rows)));
    }

    [Fact]
    public async Task F091_empty_source_delta_is_a_real_scoped_hash_not_migration_success()
    {
        var source = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        var empty = await GitHubSourceRefChangeManifestIntegrity
            .ComputeAsync(source, snapshot, Stream());
        Assert.Equal(0, empty.Total);
        Assert.NotEqual(new string('0', 64), empty.EvidenceSha256);
        GitHubSourceRefChangeManifestIntegrity.Verify(empty);
        Assert.Throws<GitHubSourceRefChangeManifestException>(() =>
            GitHubSourceRefChangeManifestIntegrity.Verify(
                empty with { EvidenceSha256 = "INVALID" }));
    }

    [Fact]
    public async Task F091_cancellation_prevents_producing_a_partial_valid_manifest()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GitHubSourceRefChangeManifestIntegrity.ComputeAsync(
                Guid.NewGuid(), Guid.NewGuid(),
                Stream(Row("refs/heads/main", "Added", null, A)),
                cts.Token));
    }
}
