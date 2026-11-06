using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitObjectClosureAuditIntegrityTests
{
    private static string Oid(char c) => new(c, 40);
    private static string Digest(char c) => new(c, 64);

    private static GitObjectClosureAuditRequest Valid()
    {
        GitMirrorRef[] source = [
            new("refs/heads/main", Oid('a')),
            new("refs/tags/v1", Oid('b'))
        ];
        GitMirrorRef[] target = [
            new("refs/heads/main", Oid('a')),
            new("refs/heads/target-only", Oid('c')),
            new("refs/tags/v1", Oid('b'))
        ];
        return new(Guid.NewGuid(), Guid.NewGuid(), source, target);
    }

    [Fact]
    public void Exact_source_refs_and_target_only_refs_are_accepted()
    {
        var request = Valid();
        var (source, target, format) =
            GitObjectClosureAuditIntegrity.Verify(request);
        Assert.Equal("sha1", format);
        Assert.Equal(2, source.Count);
        Assert.Equal(3, target.Count);
        var result = GitObjectClosureAuditIntegrity.Seal(
            request, format, Digest('c'), Digest('c'), 9, 9,
            DateTimeOffset.UtcNow);
        Assert.Equal(9, result.ReachableObjectCount);
        Assert.Equal(result.SourceObjectSetSha256, result.TargetObjectSetSha256);
        Assert.Equal(64, result.EvidenceSha256.Length);
        Assert.False(result.RemoteMigrationVerified);
        Assert.False(result.RemoteWritesAuthorized);
        Assert.False(result.TransportInvoked);
        Assert.Equal(result.EvidenceSha256, GitObjectClosureAuditIntegrity.Seal(
            request, format, Digest('c'), Digest('c'), 9, 9,
            DateTimeOffset.UtcNow).EvidenceSha256);
    }

    [Fact]
    public void Wrong_oid_missing_ref_and_aliases_are_refused()
    {
        var request = Valid();
        Refuse(request with {
            ExpectedTargetRefs = [
                new("refs/heads/main", Oid('f')),
                new("refs/tags/v1", Oid('b'))
            ]
        }, "GitClosureRefNotTransferred");
        Refuse(request with {
            ExpectedTargetRefs = [
                new("refs/heads/main", Oid('a'))
            ]
        }, "GitClosureRefNotTransferred");
        Refuse(request with {
            ExpectedSourceRefs = [
                new("refs/heads/main", Oid('a')),
                new("refs/heads/Main", Oid('a'))
            ]
        }, "GitClosureRefInvalid");
        Refuse(request with {
            ExpectedSourceRefs = request.ExpectedSourceRefs.Reverse().ToArray()
        }, "GitClosureRefOrderInvalid");
        Refuse(request with {
            ExpectedSourceRefs = [
                new("refs/heads/main", new string('a', 64)),
                new("refs/tags/v1", new string('b', 64))
            ]
        }, "GitClosureObjectFormatInvalid");
    }

    [Fact]
    public void Closure_mismatch_and_bad_resource_limits_never_seal_success()
    {
        var req = Valid();
        Assert.Equal("GitClosureMismatch",
            Assert.Throws<GitObjectClosureAuditException>(() =>
                GitObjectClosureAuditIntegrity.Seal(req, "sha1",
                    Digest('a'), Digest('b'), 5, 5,
                    DateTimeOffset.UtcNow)).Code);
        Assert.Equal("GitClosureMismatch",
            Assert.Throws<GitObjectClosureAuditException>(() =>
                GitObjectClosureAuditIntegrity.Seal(req, "sha1",
                    Digest('a'), Digest('a'), 5, 6,
                    DateTimeOffset.UtcNow)).Code);
        Assert.Equal("GitClosureMismatch",
            Assert.Throws<GitObjectClosureAuditException>(() =>
                GitObjectClosureAuditIntegrity.Seal(req, "sha1",
                    Digest('a'), Digest('a'),
                    GitObjectClosureAuditIntegrity.MaxObjects + 1,
                    GitObjectClosureAuditIntegrity.MaxObjects + 1,
                    DateTimeOffset.UtcNow)).Code);
        Assert.Equal("GitClosureRequestInvalid",
            Assert.Throws<GitObjectClosureAuditException>(() =>
                GitObjectClosureAuditIntegrity.Verify(
                    req with { TargetSandboxId = req.SourceSnapshotId })).Code);
    }

    private static void Refuse(
        GitObjectClosureAuditRequest request, string expected)
    {
        var ex = Assert.Throws<GitObjectClosureAuditException>(() =>
            GitObjectClosureAuditIntegrity.Verify(request));
        Assert.Equal(expected, ex.Code);
    }
}
