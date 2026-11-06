using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubReadOnlyObjectMirrorIntegrityTests
{
    private static string Oid(char c) => new(c, 40);
    private static string Sha(char c) => new(c, 64);

    private static (
        GitHubReadOnlyObjectMirrorRequest Request,
        GitHubReadOnlyObjectMirrorOptions Options,
        DateTimeOffset Now) Fixture()
    {
        var now = DateTimeOffset.UtcNow;
        GitMirrorRef[] source = [
            new("refs/heads/main", Oid('a')),
            new("refs/tags/v1", Oid('b'))
        ];
        GitHubObservedRef[] remote = [
            new("refs/heads/main", Oid('a'), "commit"),
            new("refs/heads/target-only", Oid('c'), "commit"),
            new("refs/tags/v1", Oid('b'), "tag")
        ];
        var observed = GitHubRefObservationIntegrity.Seal(
            "gh-org", "test-sandbox", now, remote);
        return (new(Guid.NewGuid(), 7654321, "gh-org", "test-sandbox",
            source, observed, now.AddMinutes(40)),
            new(true, 7654321, "gh-org/test-sandbox"), now);
    }

    [Fact]
    public void Fixed_https_origin_is_derived_from_explicit_allowlist_only()
    {
        var (r, options, now) = Fixture();
        Assert.Equal("https://github.com/gh-org/test-sandbox.git",
            GitHubReadOnlyObjectMirrorIntegrity.RemoteUrl(r, options, now));
        Assert.False(r.ExpectedRemoteRefs.Refs.All(x =>
            x.ObjectType == "commit"));
    }

    [Fact]
    public void Ref_type_integrity_token_expiry_and_identity_are_fail_closed()
    {
        var (r, options, now) = Fixture();
        Reject(r with { Organization = "gh-org@attacker.invalid" },
            options, now, "RemoteMirrorInputInvalid");
        Reject(r with { Repository = "../another-repo" },
            options, now, "RemoteMirrorInputInvalid");
        Reject(r with { RepositoryId = 7654322 },
            options, now, "RemoteMirrorDisabledOrWrongRepository");
        Reject(r with { TokenExpiresAtUtc = now.AddSeconds(30) },
            options, now, "RemoteMirrorInputInvalid");
        Reject(r with { ExpectedRemoteRefs = r.ExpectedRemoteRefs with {
            EvidenceSha256 = Sha('f') } },
            options, now, "RemoteMirrorObservationInvalid");
        Reject(r with { ExpectedRemoteRefs = r.ExpectedRemoteRefs with {
            Refs = r.ExpectedRemoteRefs.Refs.Select(x =>
                x.Name == "refs/tags/v1" ?
                    x with { ObjectType = "commit" } : x).ToArray() } },
            options, now, "RemoteMirrorObservationInvalid");
        Reject(r with { ExpectedSourceRefs = [
            new("refs/heads/main", Oid('f')),
            new("refs/tags/v1", Oid('b'))
        ] }, options, now, "RemoteMirrorSourceNotMatching");
        Reject(r, options, now.AddMinutes(5), "RemoteMirrorInputInvalid");
        Reject(r, options with { Enabled = false }, now,
            "RemoteMirrorDisabledOrWrongRepository");
    }

    [Fact]
    public void Independent_full_object_receipt_preserves_no_push_guarantees()
    {
        var (request, options, now) = Fixture();
        var id = Guid.NewGuid();
        var closure = GitObjectClosureAuditIntegrity.Seal(new(
            request.SourceSnapshotId, id, request.ExpectedSourceRefs,
            request.ExpectedRemoteRefs.AsMirrorRefs()),
            "sha1", Sha('d'), Sha('d'), 14, 14, now);
        var proof = new GitHubRemoteReadOnlyObjectProof(id,
            request.ExpectedRemoteRefs.AsMirrorRefs(), closure);
        var receipt = GitHubReadOnlyObjectMirrorIntegrity.Seal(
            request, proof, options, now);
        Assert.True(receipt.GitHubFetchPerformed);
        Assert.False(receipt.RemotePushInvoked);
        Assert.False(receipt.RemoteWritesAuthorized);
        Assert.False(receipt.MigrationComplete);
        Assert.Equal(receipt.SourceObjectSetSha256,
            receipt.RemoteObjectSetSha256);
        Assert.Equal(14, receipt.VerifiedReachableObjects);
        Assert.Equal(64, receipt.EvidenceSha256.Length);
        Assert.Equal(receipt.EvidenceSha256,
            GitHubReadOnlyObjectMirrorIntegrity.Seal(
                request, proof, options, now.AddSeconds(1)).EvidenceSha256);

        var forged = proof with { Closure = closure with {
            RemoteMigrationVerified = true } };
        Assert.Equal("RemoteMirrorProofInvalid",
            Assert.Throws<GitHubReadOnlyObjectMirrorException>(() =>
                GitHubReadOnlyObjectMirrorIntegrity.Seal(
                    request, forged, options, now)).Code);
        Assert.Equal("RemoteMirrorProofInvalid",
            Assert.Throws<GitHubReadOnlyObjectMirrorException>(() =>
                GitHubReadOnlyObjectMirrorIntegrity.Seal(request,
                    proof with { FetchedSnapshotId = Guid.NewGuid() },
                    options, now)).Code);
    }

    private static void Reject(
        GitHubReadOnlyObjectMirrorRequest request,
        GitHubReadOnlyObjectMirrorOptions options,
        DateTimeOffset at, string expected)
    {
        var ex = Assert.Throws<GitHubReadOnlyObjectMirrorException>(() =>
            GitHubReadOnlyObjectMirrorIntegrity.Verify(request, options, at));
        Assert.Equal(expected, ex.Code);
    }
}
