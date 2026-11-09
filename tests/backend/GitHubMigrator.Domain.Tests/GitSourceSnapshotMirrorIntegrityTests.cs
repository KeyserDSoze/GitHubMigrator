using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitSourceSnapshotMirrorIntegrityTests
{
    private static string Sha(char c) => new(c, 64);
    private static string Oid(char c) => new(c, 40);

    private static GitHubTrustedRemoteReadPlan Plan(
        Guid? snapshot = null, Guid? workpack = null)
    {
        var id = snapshot ?? Guid.NewGuid();
        var pack = workpack ?? Guid.NewGuid();
        GitMirrorRef[] refs = [
            new("refs/heads/main", Oid('a')),
            new("refs/tags/v1", Oid('b'))
        ];
        var bundle = new GitSourceBundleView(pack,
            Guid.NewGuid(), Guid.NewGuid(), "repository-id",
            Sha('c'), Sha('d'), pack.ToString("N") + ".bundle",
            Sha('e'), 1234, refs.Length, DateTimeOffset.UtcNow,
            true, false);
        return new GitHubTrustedRemoteReadPlan(
            id, "test-org", "test-repo", bundle.SourceEvidenceSha256,
            refs, Sha('1'), Sha('2'), Sha('3'), "")
        {
            SourceMirrorId = GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                id, pack),
            SourceBundle = bundle
        };
    }

    [Fact]
    public void The_same_snapshot_with_two_workpacks_cannot_share_a_bare_directory()
    {
        var snapshot = Guid.NewGuid();
        var a = Plan(snapshot);
        var b = Plan(snapshot);
        Assert.NotEqual(a.SourceMirrorId, b.SourceMirrorId);
        Assert.Equal(a.SourceMirrorId,
            GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                snapshot, a.SourceBundle!.WorkpackId));
        Assert.Equal(a.SourceMirrorId,
            GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                snapshot, a.SourceBundle!.WorkpackId));
    }

    [Fact]
    public void Proof_seals_bundle_bytes_source_refs_and_Git_object_set_without_remote_authority()
    {
        var plan = Plan();
        var proof = GitSourceSnapshotMirrorIntegrity.Seal(
            plan, Sha('f'), 18, DateTimeOffset.UtcNow);
        Assert.Equal(plan.SourceMirrorId, proof.PhysicalMirrorId);
        Assert.Equal(plan.SourceSnapshotId, proof.SourceSnapshotId);
        Assert.Equal(plan.SourceBundle!.BundleSha256, proof.BundleSha256);
        Assert.Equal(18, proof.VerifiedObjectCount);
        Assert.Equal(64, proof.EvidenceSha256.Length);
        Assert.False(proof.RemotePushInvoked);
        Assert.False(proof.RemoteWritesAuthorized);
        Assert.False(proof.MayAutomaticallyRetry);
        Assert.False(proof.MigrationComplete);
        Assert.Equal(proof.EvidenceSha256,
            GitSourceSnapshotMirrorIntegrity.MirrorEvidenceSha(
                plan.SourceSnapshotId, plan.SourceMirrorId,
                plan.SourceBundle.WorkpackId,
                plan.SourceBundle.BundleSha256,
                plan.SourceBundle.SourceEvidenceSha256,
                GitSourceSnapshotMirrorIntegrity.SourceRefsSha(plan.SourceRefs),
                plan.SourceRefs.Count, Sha('f'), 18));
        Assert.Equal(proof.EvidenceSha256,
            GitSourceSnapshotMirrorIntegrity.Seal(
                plan, Sha('f'), 18,
                DateTimeOffset.UtcNow.AddSeconds(5)).EvidenceSha256);
        Assert.NotEqual(proof.EvidenceSha256,
            GitSourceSnapshotMirrorIntegrity.Seal(
                plan, Sha('9'), 18,
                DateTimeOffset.UtcNow).EvidenceSha256);
    }

    [Fact]
    public void Foreign_bundle_tampered_sha_unapproved_namespace_and_executable_flag_are_refused()
    {
        var plan = Plan();
        Refuse(plan with { SourceMirrorId = Guid.NewGuid() },
            "SourceMirrorPlanNotBound");
        Refuse(plan with { SourceBundle = plan.SourceBundle! with {
            SourceEvidenceSha256 = Sha('f') } },
            "SourceMirrorPlanNotBound");
        Refuse(plan with { SourceBundle = plan.SourceBundle! with {
            BundleFileName = "../escape.bundle" } },
            "SourceMirrorPlanNotBound");
        Refuse(plan with { SourceBundle = plan.SourceBundle! with {
            ReadyForExecution = true } },
            "SourceMirrorPlanNotBound");
        Refuse(plan with { SourceRefs = [
            new("refs/notes/unapproved", Oid('a')),
            new("refs/tags/v1", Oid('b'))
        ] }, "SourceMirrorPlanNotBound");
        Assert.Equal("SourceMirrorObjectsInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitSourceSnapshotMirrorIntegrity.Seal(
                    plan, "bad", 5, DateTimeOffset.UtcNow)).Code);
        Assert.Equal("SourceMirrorObjectsInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitSourceSnapshotMirrorIntegrity.Seal(
                    plan, Sha('a'),
                    GitObjectClosureAuditIntegrity.MaxObjects + 1,
                    DateTimeOffset.UtcNow)).Code);
    }

    private static void Refuse(
        GitHubTrustedRemoteReadPlan plan, string expected)
    {
        var error = Assert.Throws<GitSourceSnapshotMirrorException>(() =>
            GitSourceSnapshotMirrorIntegrity.RequirePlan(plan));
        Assert.Equal(expected, error.Code);
    }
}
