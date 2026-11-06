using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteReadbackJournalIntegrityTests
{
    private static string Oid(char c) => new(c, 40);

    [Fact]
    public void Exact_annotated_tag_baseline_and_tip_objects_are_bound_to_no_send_proof()
    {
        var evidence = Valid();
        var first = GitHubRemoteReadbackIntegrity.Verify(evidence, DateTimeOffset.UtcNow);
        var second = GitHubRemoteReadbackIntegrity.Verify(evidence, DateTimeOffset.UtcNow);
        Assert.Equal(first, second);
        Assert.Equal(GitHubRemoteReadbackStates.ExactTipEvidenceOnly, first.State);
        Assert.Equal(64, first.JournalEvidenceSha256.Length);
        Assert.Equal(64, first.BaselineSha256.Length);
        Assert.Equal(GitHubRemoteRefFenceIntegrity.Fingerprint(evidence.Baseline),
            first.BaselineSha256);
        Assert.NotEqual(first.TipEvidenceSha256, first.AssessmentSha256);
        Assert.NotEqual(first.AssessmentSha256, first.JournalEvidenceSha256);
    }

    [Fact]
    public void Valid_object_tips_with_unexpected_remote_ref_are_not_a_success()
    {
        var evidence = Valid();
        var remote = GitHubRefObservationIntegrity.Seal("gh-org", "sandbox",
            evidence.Snapshot.RemoteRefs.ObservedAtUtc,
            [..evidence.Snapshot.RemoteRefs.Refs,
             new("refs/heads/unexpected", Oid('f'), "commit")]);
        var readback = GitHubRemoteTipIntegrity.Seal(
            remote, evidence.CreateCandidates, remote.ObservedAtUtc,
            evidence.Snapshot.TipReadback.Tips);
        var changed = evidence with {
            Snapshot = new GitHubRemoteTipSnapshot(remote, readback)
        };
        var decision = GitHubRemoteReadbackIntegrity.Verify(
            changed, DateTimeOffset.UtcNow);
        Assert.Equal(GitHubRemoteReadbackStates.OperatorReviewRequired,
            decision.State);
    }

    [Fact]
    public void Forged_tip_receipt_baseline_object_type_and_stale_timestamp_fail_closed()
    {
        var evidence = Valid();
        var ex = Assert.Throws<GitHubRemoteReadbackException>(() =>
            GitHubRemoteReadbackIntegrity.Verify(
                evidence with {
                    Snapshot = evidence.Snapshot with {
                        TipReadback = evidence.Snapshot.TipReadback with {
                            EvidenceSha256 = new string('0', 64)
                        }
                    }
                }, DateTimeOffset.UtcNow));
        Assert.Equal("RemoteReadbackEvidenceInvalid", ex.Code);

        var alteredBaseline = evidence with {
            Baseline = evidence.Baseline.Select(x =>
                x.Name == "refs/tags/v1"
                    ? x with { ObjectType = "commit" } : x).ToArray()
        };
        Assert.NotEqual(
            GitHubRemoteReadbackIntegrity.Verify(evidence, DateTimeOffset.UtcNow)
                .BaselineSha256,
            GitHubRemoteReadbackIntegrity.Verify(alteredBaseline, DateTimeOffset.UtcNow)
                .BaselineSha256);

        var ex2 = Assert.Throws<GitHubRemoteReadbackException>(() =>
            GitHubRemoteReadbackIntegrity.Verify(evidence,
                DateTimeOffset.UtcNow.AddMinutes(2)));
        Assert.Equal("RemoteReadbackObservationStale", ex2.Code);
    }

    public static GitHubRemoteReadbackEvidence Valid()
    {
        var now = DateTimeOffset.UtcNow;
        var baseline = new GitHubObservedRef[] {
            new("refs/heads/main", Oid('e'), "commit"),
            new("refs/tags/v1", Oid('d'), "tag")
        };
        var creates = new GitMirrorRef[] {
            new("refs/heads/feature", Oid('a')),
            new("refs/tags/v2", Oid('b'))
        };
        var remote = GitHubRefObservationIntegrity.Seal("gh-org", "sandbox",
            now, [
                new("refs/heads/feature", Oid('a'), "commit"),
                new("refs/heads/main", Oid('e'), "commit"),
                new("refs/tags/v1", Oid('d'), "tag"),
                new("refs/tags/v2", Oid('b'), "tag")
            ]);
        var tips = GitHubRemoteTipIntegrity.Seal(remote, creates,
            remote.ObservedAtUtc, [
                new("refs/heads/feature", Oid('a'), "commit",
                    Oid('a'), Oid('c'), 0),
                new("refs/tags/v2", Oid('b'), "tag",
                    Oid('f'), Oid('c'), 1)
            ]);
        return new(Guid.NewGuid(), 24680, Guid.NewGuid(), baseline, creates,
            new(remote, tips));
    }
}
