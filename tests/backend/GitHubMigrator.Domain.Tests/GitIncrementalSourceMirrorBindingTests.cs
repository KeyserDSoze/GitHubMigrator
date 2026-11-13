using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitIncrementalSourceMirrorBindingTests
{
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);
    private static readonly string Oid = new('a', 40);

    private static (
        IncrementalOperationPlan Plan, VerifiedSourceGitRefs Source,
        GitIncrementalSourceMirrorBinding Mirror,
        GitIncrementalAncestryReceipt Audit, DateTimeOffset Now) Fixture()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshotId = Guid.NewGuid();
        var workpackId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var plan = new IncrementalOperationPlan(
            planId, Guid.NewGuid(), snapshotId, sourceId,
            Guid.NewGuid(), "gh-org", A, A, A, A, now, Guid.NewGuid(),
            1, 6, 1, 0, true, false, [], []);
        var source = VerifiedSourceGitRefIntegrity.Seal(
            snapshotId, sourceId, "repo-1", A,
            [new("refs/heads/main", Oid)]);
        var mirrorId = GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
            snapshotId, workpackId);
        var refSha = GitSourceSnapshotMirrorIntegrity.SourceRefsSha(
            source.Refs);
        var mirrorProof = GitSourceSnapshotMirrorIntegrity.MirrorEvidenceSha(
            snapshotId, mirrorId, workpackId, B,
            source.SourceEvidenceSha256, refSha, 1, A, 3);
        var mirror = new GitIncrementalSourceMirrorBinding(
            mirrorId, snapshotId, workpackId, planId, Guid.NewGuid(),
            "repo-1", source.SourceEvidenceSha256, B, 1, refSha,
            A, 3, mirrorProof, now.AddSeconds(-1),
            GitSourceMirrorLifecycleStates.Verified,
            false, false, false, false);
        var request = new GitIncrementalAncestryRequest(
            snapshotId, Guid.NewGuid(), Guid.NewGuid(), B,
            Guid.NewGuid(), A, source.Refs, source.Refs,
            mirrorId, mirrorProof);
        var audit = GitIncrementalAncestryIntegrity.Seal(
            request, "sha1", A, B, 3, 3,
            [new("refs/heads/main", Oid, Oid,
                GitIncrementalAncestryStatus.MatchingObjectId)],
            now);
        return (plan, source, mirror, audit, now);
    }

    [Fact]
    public void F096_null_plan_remains_a_typed_refusal_after_F097c_extension()
    {
        var f = Fixture();
        var failure = Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
            GitIncrementalSourceMirrorBindingIntegrity.VerifyContext(
                null!, f.Source, f.Mirror, f.Now, true));
        Assert.Equal("GitAncestryLedgerMirrorEvidenceInvalid", failure.Code);
    }

    [Fact]
    public void F096_verified_F060_workpack_proof_is_bound_to_physical_F094_object_set()
    {
        var f = Fixture();
        GitIncrementalSourceMirrorBindingIntegrity.VerifyContext(
            f.Plan, f.Source, f.Mirror, f.Now, true);
        GitIncrementalSourceMirrorBindingIntegrity.VerifyAudit(
            f.Plan, f.Source, f.Mirror, f.Audit, f.Now, true);
        Assert.False(f.Audit.CanPush);
        Assert.False(f.Audit.CanRetry);
        Assert.False(f.Audit.MigrationComplete);
    }

    [Fact]
    public void F096_foreign_workpack_wrong_ref_set_forged_mirror_sha_and_replayed_fsck_refuse()
    {
        var f = Fixture();
        var mutations = new[] {
            f.Mirror with { WorkpackId = Guid.NewGuid() },
            f.Mirror with { SourceRepositoryId = "foreign-repo" },
            f.Mirror with { SourceEvidenceSha256 = B },
            f.Mirror with { BundleSha256 = A },
            f.Mirror with { RefsSha256 = B },
            f.Mirror with { ObjectCount = 4 },
            f.Mirror with { MirrorProofSha256 = B },
            f.Mirror with { State = GitSourceMirrorLifecycleStates.Leased },
            f.Mirror with { RemotePushInvoked = true },
            f.Mirror with { RemoteWritesAuthorized = true },
            f.Mirror with { MayAutomaticallyRetry = true },
            f.Mirror with { MigrationComplete = true }
        };
        foreach (var bad in mutations)
            Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
                GitIncrementalSourceMirrorBindingIntegrity.VerifyContext(
                    f.Plan, f.Source, bad, f.Now, true));
        var expired = f.Mirror with {
            VerifiedAtUtc = f.Now.AddMinutes(-3)
        };
        Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
            GitIncrementalSourceMirrorBindingIntegrity.VerifyContext(
                f.Plan, f.Source, expired, f.Now, true));

        var wrongObjectSet = f.Audit with {
            SourceObjectSetSha256 = B
        };
        Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
            GitIncrementalSourceMirrorBindingIntegrity.VerifyAudit(
                f.Plan, f.Source, f.Mirror, wrongObjectSet, f.Now, true));
        var foreignPhysical = f.Audit with {
            Request = f.Audit.Request with {
                SourcePhysicalMirrorId = Guid.NewGuid()
            }
        };
        Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
            GitIncrementalSourceMirrorBindingIntegrity.VerifyAudit(
                f.Plan, f.Source, f.Mirror, foreignPhysical, f.Now, true));
        var oldAudit = f.Audit with {
            VerifiedAtUtc = f.Mirror.VerifiedAtUtc.AddSeconds(-1)
        };
        Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
            GitIncrementalSourceMirrorBindingIntegrity.VerifyAudit(
                f.Plan, f.Source, f.Mirror, oldAudit, f.Now, true));
    }
}
