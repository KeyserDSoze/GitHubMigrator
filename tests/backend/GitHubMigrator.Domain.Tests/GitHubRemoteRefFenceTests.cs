using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteRefFenceTests
{
    private const string First = "1111111111111111111111111111111111111111";
    private const string Second = "2222222222222222222222222222222222222222";

    [Fact]
    public void Exact_branch_and_annotated_tag_object_type_are_required()
    {
        var baseline = Refs();
        var observed = GitHubRefObservationIntegrity.Seal(
            "gh-org", "repo", DateTimeOffset.UtcNow, baseline);
        var same = GitHubRemoteRefFenceIntegrity.Compare(baseline, observed);
        Assert.Equal("RefSetUnchanged", same.Status);
        Assert.Equal(0, same.ChangedRefs);
        Assert.Equal(baseline.Length, same.BaselineCount);
        Assert.Equal(baseline.Length, same.RemoteCount);
        Assert.Equal(64, same.RemoteRefFingerprintSha256.Length);

        var tagChanged = GitHubRefObservationIntegrity.Seal(
            "gh-org", "repo", DateTimeOffset.UtcNow,
            baseline.Select(x => x.ObjectType == "tag"
                ? x with { ObjectType = "commit" } : x).ToArray());
        var drift = GitHubRemoteRefFenceIntegrity.Compare(baseline, tagChanged);
        Assert.Equal("RemoteDriftBlocked", drift.Status);
        Assert.Equal(1, drift.ChangedRefs);
        Assert.NotEqual(same.RemoteRefFingerprintSha256,
            drift.RemoteRefFingerprintSha256);
    }

    [Fact]
    public void New_deleted_or_mutated_remote_ref_is_a_blocker()
    {
        var original = Refs();
        var added = GitHubRefObservationIntegrity.Seal("gh-org", "repo",
            DateTimeOffset.UtcNow, original.Append(new(
                "refs/heads/new", Second, "commit")).ToArray());
        var removed = GitHubRefObservationIntegrity.Seal("gh-org", "repo",
            DateTimeOffset.UtcNow, original.Take(1).ToArray());
        var mutated = GitHubRefObservationIntegrity.Seal("gh-org", "repo",
            DateTimeOffset.UtcNow, original.Select(x =>
                x.Name == "refs/heads/main"
                    ? x with { ObjectId = Second } : x).ToArray());
        foreach (var observed in new[] { added, removed, mutated })
        {
            var state = GitHubRemoteRefFenceIntegrity.Compare(original, observed);
            Assert.Equal("RemoteDriftBlocked", state.Status);
            Assert.Equal(1, state.ChangedRefs);
        }
    }

    [Fact]
    public void Old_and_new_timestamps_do_not_change_ref_fingerprint()
    {
        var baseline = Refs();
        var past = GitHubRefObservationIntegrity.Seal("gh-org", "repo",
            DateTimeOffset.UtcNow.AddMinutes(-2), baseline);
        var now = GitHubRefObservationIntegrity.Seal("gh-org", "repo",
            DateTimeOffset.UtcNow, baseline);
        Assert.NotEqual(past.EvidenceSha256, now.EvidenceSha256);
        Assert.Equal(GitHubRemoteRefFenceIntegrity.Compare(baseline, past)
            .RemoteRefFingerprintSha256,
            GitHubRemoteRefFenceIntegrity.Compare(baseline, now)
            .RemoteRefFingerprintSha256);
    }

    [Fact]
    public void Immutable_proof_changes_with_any_authority_ref_or_actor()
    {
        var input = Input(new("RefSetUnchanged",
            new string('a', 64), 2, 2, 0, DateTimeOffset.UtcNow));
        var seal = GitHubRemoteRefFenceIntegrity.Seal(input);
        Assert.Equal(seal, GitHubRemoteRefFenceIntegrity.Seal(input));
        Assert.NotEqual(seal, GitHubRemoteRefFenceIntegrity.Seal(input with {
            RehearsalEvidenceSha256 = new string('c', 64)
        }));
        Assert.NotEqual(seal, GitHubRemoteRefFenceIntegrity.Seal(input with {
            TargetEvidenceSha256 = new string('d', 64)
        }));
        Assert.NotEqual(seal, GitHubRemoteRefFenceIntegrity.Seal(input with {
            ActorId = Guid.NewGuid()
        }));
        Assert.NotEqual(seal, GitHubRemoteRefFenceIntegrity.Seal(input with {
            Comparison = new("RemoteDriftBlocked",
                new string('b', 64), 2, 3, 1, input.Comparison.ObservedAtUtc)
        }));
    }

    [Fact]
    public void Forged_match_count_and_unsorted_snapshot_fail_closed()
    {
        var input = Input(new("RefSetUnchanged",
            new string('a', 64), 2, 3, 0, DateTimeOffset.UtcNow));
        Assert.Equal("RemoteFenceEvidenceInvalid",
            Assert.Throws<GitHubRemoteRefFenceException>(() =>
                GitHubRemoteRefFenceIntegrity.Seal(input)).Code);

        var refs = Refs();
        var observed = GitHubRefObservationIntegrity.Seal("gh-org", "repo",
            DateTimeOffset.UtcNow, refs);
        var reversed = refs.Reverse().ToArray();
        Assert.Equal("RemoteFenceObservationInvalid",
            Assert.Throws<GitHubRemoteRefFenceException>(() =>
                GitHubRemoteRefFenceIntegrity.Compare(reversed, observed)).Code);
    }

    private static GitHubRemoteRefFenceInput Input(
        GitHubRemoteRefFenceComparison proof) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new string('a',64), new string('b',64), new string('c',64),
            new string('d',64), new string('e',64), new string('f',64),
            new string('a',64), proof, Guid.NewGuid());

    private static GitHubObservedRef[] Refs() =>
    [
        new("refs/heads/main", First, "commit"),
        new("refs/tags/rel", Second, "tag")
    ];
}
