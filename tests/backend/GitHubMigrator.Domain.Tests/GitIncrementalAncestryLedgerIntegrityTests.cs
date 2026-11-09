using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitIncrementalAncestryLedgerIntegrityTests
{
    private const string Ref = "refs/heads/main";
    private static readonly string GitOid = new('a', 40);
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);

    [Fact]
    public void F095_verifies_exact_source_and_destination_oid_proofs()
    {
        var (plan, checkpoint, source, target, observation, audit) = Fixture();
        GitIncrementalAncestryLedgerIntegrity.VerifyBinding(
            plan, checkpoint, source, target, observation, audit, true);

        var entry = new GitIncrementalAncestryLedgerEntry(
            Guid.NewGuid(), plan.PreparationId, plan.Id,
            source.SourceRepositoryId, target.TargetRepositoryName,
            plan.ManifestSha256, source.SourceEvidenceSha256,
            audit, Guid.NewGuid(), DateTimeOffset.UtcNow,
            false, false, false, false);
        GitIncrementalAncestryLedgerIntegrity.VerifyEntry(
            entry, plan, checkpoint, source, target, observation);
        Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
            GitIncrementalAncestryLedgerIntegrity.VerifyEntry(
                entry with { CanPush = true },
                plan, checkpoint, source, target, observation));
    }

    [Fact]
    public void F095_refuses_replayed_foreign_or_incremental_epoch()
    {
        var (plan, checkpoint, source, target, observation, audit) = Fixture();
        void Reject(GitIncrementalAncestryReceipt physical,
            GitHubSourceSyncCheckpointReceipt? cp = null,
            GitHubTargetRefEvidenceView? dest = null,
            bool current = true) =>
            Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
                GitIncrementalAncestryLedgerIntegrity.VerifyBinding(
                    plan, cp ?? checkpoint, source, dest ?? target,
                    observation, physical, current));
        Reject(audit with { Request = audit.Request with {
            TargetObservationId = Guid.NewGuid() } });
        Reject(audit with { Request = audit.Request with {
            SourceCheckpointId = Guid.NewGuid() } });
        Reject(audit with { Request = audit.Request with {
            ExpectedSourceRefs = [new(Ref, new string('b', 40))] } });
        Reject(audit with { Request = audit.Request with {
            ExpectedTargetRefs = [new(Ref, new string('b', 40))] } });
        Reject(audit, checkpoint with { EvidenceVersion = 1 });
        Reject(audit, checkpoint with { Proof = checkpoint.Proof with {
            SnapshotId = Guid.NewGuid() } });
        Reject(audit, dest: target with { EvidenceSha256 = A });
        Reject(audit, dest: target with { IsCurrent = false });
    }

    private static (
        IncrementalOperationPlan, GitHubSourceSyncCheckpointReceipt,
        VerifiedSourceGitRefs, GitHubTargetRefEvidenceView,
        GitHubRefObservation, GitIncrementalAncestryReceipt) Fixture()
    {
        var now = DateTimeOffset.UtcNow;
        var sourceId = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        var preparationId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var checkpointId = Guid.NewGuid();
        var manifest = A;
        var step = new IncrementalOperationStep(1, null,
            "repo-1", "target-1", "ReviewTargetName",
            "Blocked", "DestinationWritePermissionUnverified", 1);
        var plan = new IncrementalOperationPlan(planId, preparationId,
            snapshot, sourceId, Guid.NewGuid(), "gh-org",
            A, A, A, manifest, now, Guid.NewGuid(),
            1, 6, 1, 0, true, false, [],
            [step]);
        var proof = new GitHubSourceSyncCheckpointProof(
            preparationId, planId, sourceId, snapshot, snapshot,
            null, null, manifest,
            now.AddMinutes(-3), now.AddMinutes(-2), true, true,
            1, 0, 0, 0, 1, 0,
            [new GitHubSourceRefDelta("repo-1", "target-1",
                1, 0, 0, 0)]);
        var refs = new GitHubSourceRefChangeManifest(
            sourceId, snapshot, 1, 0, 0, 0, B);
        var v1 = GitHubSourceSyncCheckpointIntegrity.Digest(proof);
        var attestation = GitHubSourceSyncCheckpointIntegrity
            .RefAttestationDigest(checkpointId, v1, refs);
        var checkpoint = new GitHubSourceSyncCheckpointReceipt(
            checkpointId, proof, v1,
            Guid.NewGuid(), now,
            false, false, false, false, refs, attestation, 2);
        var source = VerifiedSourceGitRefIntegrity.Seal(
            snapshot, sourceId, "repo-1", B,
            [new(Ref, GitOid)]);
        var observation = GitHubRefObservationIntegrity.Seal(
            "gh-org", "target-1", now,
            [new(Ref, GitOid, "commit")]);
        var target = new GitHubTargetRefEvidenceView(
            targetId, planId, "repo-1", "target-1", "gh-org",
            manifest, observation.EvidenceSha256, observation.ObservedAtUtc,
            1, true, false);
        var request = new GitIncrementalAncestryRequest(
            snapshot, Guid.NewGuid(), checkpointId, attestation,
            targetId, target.EvidenceSha256, source.Refs,
            observation.AsMirrorRefs());
        var audit = GitIncrementalAncestryIntegrity.Seal(
            request, "sha1", A, B, 2, 2,
            [new(Ref, GitOid, GitOid,
                GitIncrementalAncestryStatus.MatchingObjectId)],
            now);
        return (plan, checkpoint, source, target, observation, audit);
    }
}
