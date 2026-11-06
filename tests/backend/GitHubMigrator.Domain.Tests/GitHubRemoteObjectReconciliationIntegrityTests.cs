using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteObjectReconciliationIntegrityTests
{
    private static string Oid(char c) => new(c, 40);
    private static string Sha(char c) => new(c, 64);

    [Fact]
    public void Full_history_remote_copy_is_only_a_SHA_bound_non_executable_observation()
    {
        var (e, opts) = Valid();
        var now = DateTimeOffset.UtcNow;
        var proof = GitHubRemoteObjectReconciliationIntegrity.Verify(
            e, opts, now);
        Assert.Equal(GitHubRemoteObjectReconciliationStates.FullCopyEvidenceOnly,
            proof.State);
        Assert.Equal(64, proof.EvidenceSha256.Length);
        Assert.Equal(e.MirrorReceipt.EvidenceSha256,
            proof.RemoteMirrorReceiptSha256);
        Assert.Equal(e.MirrorRequest.SourceSnapshotId, proof.SourceSnapshotId);
        Assert.Equal(proof.EvidenceSha256,
            GitHubRemoteObjectReconciliationIntegrity.Verify(
                e, opts, now.AddSeconds(1)).EvidenceSha256);
    }

    [Fact]
    public void Altered_F057_receipt_and_remote_write_flags_fail_closed()
    {
        var (e, opts) = Valid();
        Refuse(e with { MirrorReceipt = e.MirrorReceipt with {
            EvidenceSha256 = Sha('0') } }, opts,
            "RemoteObjectReconciliationReceiptInvalid");
        Refuse(e with { MirrorReceipt = e.MirrorReceipt with {
            RemotePushInvoked = true } }, opts,
            "RemoteObjectReconciliationReceiptInvalid");
        Refuse(e with { MirrorReceipt = e.MirrorReceipt with {
            MigrationComplete = true } }, opts,
            "RemoteObjectReconciliationReceiptInvalid");
        Refuse(e with { MirrorReceipt = e.MirrorReceipt with {
            SourceObjectSetSha256 = Sha('0') } }, opts,
            "RemoteObjectReconciliationReceiptInvalid");
        Refuse(e with { MirrorReceipt = e.MirrorReceipt with {
            VerifiedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10) } }, opts,
            "RemoteObjectReconciliationReceiptStaleOrWrongTarget");
    }

    [Fact]
    public void Missing_divergent_and_unexpected_target_refs_cannot_be_reconciled()
    {
        var (e, opts) = Valid();
        var remote = GitHubRefObservationIntegrity.Seal(
            e.MirrorRequest.Organization, e.MirrorRequest.Repository,
            e.MirrorRequest.ExpectedRemoteRefs.ObservedAtUtc,
            [..e.MirrorRequest.ExpectedRemoteRefs.Refs,
                new("refs/heads/unexpected", Oid('f'), "commit")]);
        var source = e.MirrorRequest.ExpectedSourceRefs;
        var request = e.MirrorRequest with { ExpectedRemoteRefs = remote };
        var receipt = ForRequest(request);
        Refuse(e with {
            MirrorRequest = request, MirrorReceipt = receipt
        }, opts, "RemoteObjectReconciliationRefsDiverged");

        Refuse(e with {
            MirrorRequest = e.MirrorRequest with {
                ExpectedSourceRefs = [
                    new("refs/heads/main", Oid('f'))
                ]
            }
        }, opts, "RemoteObjectReconciliationObservationInvalid");
    }

    [Fact]
    public void Source_snapshot_and_typed_original_baseline_are_bound_to_evidence()
    {
        var (e, opts) = Valid();
        var original = GitHubRemoteObjectReconciliationIntegrity.Verify(
            e, opts, DateTimeOffset.UtcNow);
        var alternative = e with {
            OriginalBaseline = e.OriginalBaseline.Select(x =>
                x.Name == "refs/tags/v1"
                    ? x with { ObjectType = "commit" } : x).ToArray()
        };
        var changed = GitHubRemoteObjectReconciliationIntegrity.Verify(
            alternative, opts, DateTimeOffset.UtcNow);
        Assert.NotEqual(original.OriginalBaselineSha256,
            changed.OriginalBaselineSha256);
        Assert.NotEqual(original.EvidenceSha256, changed.EvidenceSha256);

        Refuse(e with {
            MirrorReceipt = e.MirrorReceipt with {
                SourceSnapshotId = Guid.NewGuid() }
        }, opts, "RemoteObjectReconciliationReceiptStaleOrWrongTarget");
        Refuse(e, opts with { Enabled = false },
            "RemoteObjectReconciliationObservationInvalid");
    }

    private static void Refuse(
        GitHubRemoteObjectReconciliationEvidence evidence,
        GitHubReadOnlyObjectMirrorOptions options, string code)
    {
        var ex = Assert.Throws<GitHubRemoteObjectReconciliationException>(() =>
            GitHubRemoteObjectReconciliationIntegrity.Verify(
                evidence, options, DateTimeOffset.UtcNow));
        Assert.Equal(code, ex.Code);
    }

    internal static (GitHubRemoteObjectReconciliationEvidence Evidence,
        GitHubReadOnlyObjectMirrorOptions Options) Valid()
    {
        var now = DateTimeOffset.UtcNow;
        GitHubObservedRef[] baseline = [
            new("refs/heads/main", Oid('a'), "commit"),
            new("refs/tags/v1", Oid('b'), "tag")
        ];
        GitMirrorRef[] creates = [
            new("refs/heads/feature", Oid('c')),
            new("refs/tags/v2", Oid('d'))
        ];
        var remote = GitHubRefObservationIntegrity.Seal(
            "gh-org", "test-sandbox", now, [
                new("refs/heads/feature", Oid('c'), "commit"),
                new("refs/heads/main", Oid('a'), "commit"),
                new("refs/tags/v1", Oid('b'), "tag"),
                new("refs/tags/v2", Oid('d'), "tag")
            ]);
        var request = new GitHubReadOnlyObjectMirrorRequest(
            Guid.NewGuid(), 8123123, "gh-org", "test-sandbox",
            remote.AsMirrorRefs(), remote, now.AddMinutes(40));
        var receipt = ForRequest(request);
        return (new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            request.RepositoryId, Guid.NewGuid(), baseline, creates,
            request, receipt),
            new(true, request.RepositoryId, "gh-org/test-sandbox"));
    }

    private static GitHubReadOnlyObjectMirrorReceipt ForRequest(
        GitHubReadOnlyObjectMirrorRequest request)
    {
        var opts = new GitHubReadOnlyObjectMirrorOptions(
            true, request.RepositoryId, "gh-org/test-sandbox");
        var snapshot = Guid.NewGuid();
        var closure = GitObjectClosureAuditIntegrity.Seal(new(
            request.SourceSnapshotId, snapshot, request.ExpectedSourceRefs,
            request.ExpectedRemoteRefs.AsMirrorRefs()), "sha1",
            Sha('e'), Sha('e'), 16, 16,
            request.ExpectedRemoteRefs.ObservedAtUtc);
        var proof = new GitHubRemoteReadOnlyObjectProof(
            snapshot, request.ExpectedRemoteRefs.AsMirrorRefs(), closure);
        return GitHubReadOnlyObjectMirrorIntegrity.Seal(
            request, proof, opts, request.ExpectedRemoteRefs.ObservedAtUtc);
    }
}
