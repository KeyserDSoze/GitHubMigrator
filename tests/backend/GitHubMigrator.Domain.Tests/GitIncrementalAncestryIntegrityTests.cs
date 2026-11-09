using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitIncrementalAncestryIntegrityTests
{
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);
    private static readonly string C = new('c', 40);

    private static GitIncrementalAncestryRequest Request() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        A, Guid.NewGuid(), B,
        [new("refs/heads/main", C)],
        [new("refs/heads/main", C)]);

    [Fact]
    public void F094_receipt_is_deterministic_but_bound_to_both_proofs_and_object_sets()
    {
        var request = Request();
        var items = new[] {
            new GitIncrementalAncestryItem("refs/heads/main", C, C,
                GitIncrementalAncestryStatus.MatchingObjectId)
        };
        var receipt = GitIncrementalAncestryIntegrity.Seal(
            request, "sha1", A, B, 4, 4, items, DateTimeOffset.UtcNow);
        GitIncrementalAncestryIntegrity.VerifyReceipt(receipt);
        Assert.Equal(receipt.EvidenceSha256,
            GitIncrementalAncestryIntegrity.Seal(request, "sha1", A, B,
                4, 4, items, DateTimeOffset.UtcNow).EvidenceSha256);
        Assert.NotEqual(receipt.EvidenceSha256,
            GitIncrementalAncestryIntegrity.Seal(
                request with { TargetObservationId = Guid.NewGuid() },
                "sha1", A, B, 4, 4, items,
                DateTimeOffset.UtcNow).EvidenceSha256);
        Assert.NotEqual(receipt.EvidenceSha256,
            GitIncrementalAncestryIntegrity.Seal(request, "sha1",
                A, new string('d', 64), 4, 4, items,
                DateTimeOffset.UtcNow).EvidenceSha256);
        Assert.Throws<GitIncrementalAncestryException>(() =>
            GitIncrementalAncestryIntegrity.VerifyReceipt(
                receipt with { CanPush = true }));
        Assert.Throws<GitIncrementalAncestryException>(() =>
            GitIncrementalAncestryIntegrity.VerifyReceipt(
                receipt with { EvidenceSha256 = new string('d', 64) }));
    }

    [Fact]
    public void F094_canonical_ref_order_same_format_and_case_safety_are_mandatory()
    {
        var valid = Request();
        var bad = new[] {
            valid with { SourceCheckpointId = Guid.Empty },
            valid with { TargetObservationSha256 = "invalid" },
            valid with { ExpectedSourceRefs = [
                new("refs/heads/main", C), new("refs/heads/z", C),
                new("refs/heads/a", C)] },
            valid with { ExpectedTargetRefs = [
                new("refs/heads/Main", C)] },
            valid with { ExpectedTargetRefs = [
                new("refs/heads/main", new string('d', 64))] },
            valid with { ExpectedSourceRefs = [
                new("refs/heads/main", C), new("refs/heads/main", C)] }
        };
        foreach (var request in bad)
            Assert.Throws<GitIncrementalAncestryException>(() =>
                GitIncrementalAncestryIntegrity.VerifyRequest(request));
    }

    [Fact]
    public void F094_mismatched_oid_or_tag_cannot_claim_unblocked_migration()
    {
        var request = Request() with {
            ExpectedTargetRefs = [new("refs/heads/main", new string('d', 40))]
        };
        var impossible = new GitIncrementalAncestryItem(
            "refs/heads/main", C, new string('d', 40),
            GitIncrementalAncestryStatus.MatchingObjectId);
        Assert.Throws<GitIncrementalAncestryException>(() =>
            GitIncrementalAncestryIntegrity.Seal(request, "sha1",
                A, B, 2, 2, [impossible], DateTimeOffset.UtcNow));

        var tags = request with {
            ExpectedSourceRefs = [new("refs/tags/v1", C)],
            ExpectedTargetRefs = [new("refs/tags/v1", new string('d', 40))]
        };
        Assert.Throws<GitIncrementalAncestryException>(() =>
            GitIncrementalAncestryIntegrity.Seal(tags, "sha1", A, B,
                2, 2, [new("refs/tags/v1", C, new string('d', 40),
                    GitIncrementalAncestryStatus.FastForwardCandidateBlocked)],
                DateTimeOffset.UtcNow));
        var blocked = GitIncrementalAncestryIntegrity.Seal(tags, "sha1", A, B,
            2, 2, [new("refs/tags/v1", C, new string('d', 40),
                GitIncrementalAncestryStatus.TagChangeBlocked)],
            DateTimeOffset.UtcNow);
        GitIncrementalAncestryIntegrity.VerifyReceipt(blocked);
        Assert.False(blocked.CanPush);
        Assert.False(blocked.MigrationComplete);
    }
}
