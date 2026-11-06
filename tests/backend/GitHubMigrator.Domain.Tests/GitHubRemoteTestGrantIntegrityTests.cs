using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteTestGrantIntegrityTests
{
    [Fact]
    public void Exact_full_remote_ref_set_is_observed_but_never_grants_a_retry()
    {
        var (baseline, creates, actual) = Refs();
        var checkedRefs = GitHubRemotePostSendIntegrity.Inspect(
            baseline, creates, actual);
        Assert.Equal("ExactRemoteRefsObserved", checkedRefs.State);
        Assert.Equal(1, checkedRefs.ExactCreateRefs);
        Assert.Equal(3, checkedRefs.TotalRefs);
        Assert.Equal(64, checkedRefs.EvidenceSha256.Length);
        Assert.Equal(64, checkedRefs.RefSetSha256.Length);
        Assert.False(checkedRefs.MayAutomaticallyRetry);
        Assert.False(checkedRefs.RemoteWritesAuthorized);
        Assert.Equal(checkedRefs.EvidenceSha256,
            GitHubRemotePostSendIntegrity.Inspect(
                baseline, creates, actual).EvidenceSha256);
    }

    [Fact]
    public void No_ref_created_is_not_a_safe_retry_signal()
    {
        var (baseline, creates, actual) = Refs();
        var empty = GitHubRefObservationIntegrity.Seal(
            actual.Organization, actual.Repository,
            actual.ObservedAtUtc, actual.Refs.Where(x =>
                x.Name != "refs/heads/feature").ToArray());
        var inspected = GitHubRemotePostSendIntegrity.Inspect(
            baseline, creates, empty);
        Assert.Equal("NoCreatedRefsObserved", inspected.State);
        Assert.Equal(0, inspected.ExactCreateRefs);
        Assert.False(inspected.MayAutomaticallyRetry);
        Assert.False(inspected.RemoteWritesAuthorized);
    }

    [Fact]
    public void Annotated_tag_changes_unexpected_ref_and_partial_success_require_operator()
    {
        var (baseline, creates, actual) = Refs();
        var tamperedTag = GitHubRefObservationIntegrity.Seal(
            actual.Organization, actual.Repository,
            actual.ObservedAtUtc,
            actual.Refs.Select(x => x.Name == "refs/tags/v1"
                ? x with { ObjectId = new string('d', 40) }
                : x).ToArray());
        var first = GitHubRemotePostSendIntegrity.Inspect(
            baseline, creates, tamperedTag);
        Assert.Equal("RemoteDivergenceRequiresOperator", first.State);
        Assert.False(first.MayAutomaticallyRetry);

        var unexpected = GitHubRefObservationIntegrity.Seal(
            actual.Organization, actual.Repository, actual.ObservedAtUtc,
            [..actual.Refs,
                new GitHubObservedRef("refs/heads/unexpected",
                    new string('e', 40), "commit")]);
        var second = GitHubRemotePostSendIntegrity.Inspect(
            baseline, creates, unexpected);
        Assert.Equal("RemoteDivergenceRequiresOperator", second.State);
        Assert.Equal(1, second.ExactCreateRefs);
        Assert.False(second.MayAutomaticallyRetry);
    }

    [Fact]
    public void Spoofed_provider_evidence_fails_before_a_post_send_classification()
    {
        var (baseline, creates, actual) = Refs();
        var spoofed = actual with {
            EvidenceSha256 = new string('a', 64)
        };
        var ex = Assert.Throws<GitHubRemoteTestGrantException>(() =>
            GitHubRemotePostSendIntegrity.Inspect(baseline, creates, spoofed));
        Assert.Equal("RemotePostSendObservationInvalid", ex.Code);

        ex = Assert.Throws<GitHubRemoteTestGrantException>(() =>
            GitHubRemotePostSendIntegrity.Inspect(baseline,
                [baseline[0]], actual));
        Assert.Equal("RemotePostSendInputInvalid", ex.Code);
    }

    [Fact]
    public void Consent_token_digest_is_domain_separated_and_unrelated_to_GitHub_credentials()
    {
        var token = Guid.NewGuid();
        var proof = GitHubRemoteTestGrantIntegrity.TokenDigest(token);
        Assert.Equal(64, proof.Length);
        Assert.NotEqual(
            GitHubRemoteSandboxFenceIntegrity.TokenDigest(token), proof);
        Assert.NotEqual(token.ToString("N"), proof);
    }

    private static (IReadOnlyList<GitMirrorRef> Baseline,
        IReadOnlyList<GitMirrorRef> Create,
        GitHubRefObservation Actual) Refs()
    {
        var when = DateTimeOffset.UtcNow;
        var main = new GitMirrorRef("refs/heads/main", new string('a', 40));
        var tag = new GitMirrorRef("refs/tags/v1", new string('b', 40));
        var feature = new GitMirrorRef("refs/heads/feature", new string('c', 40));
        var baseline = GitMirrorRefReconciler.CanonicalizeObservedRefs(
            [main, tag]);
        var creates = new[] { feature };
        var observed = GitHubRefObservationIntegrity.Seal("gh-org", "test",
            when, [
                new("refs/heads/feature", feature.ObjectId, "commit"),
                new("refs/heads/main", main.ObjectId, "commit"),
                new("refs/tags/v1", tag.ObjectId, "tag")
            ]);
        return (baseline, creates, observed);
    }
}
