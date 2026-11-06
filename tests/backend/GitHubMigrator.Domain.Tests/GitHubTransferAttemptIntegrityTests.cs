using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubTransferAttemptIntegrityTests
{
    [Fact]
    public void Fresh_review_only_packet_and_clear_read_only_rules_create_inert_draft()
    {
        var (packet, review) = Fixture();
        var actor = Guid.NewGuid();
        var token = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var draft = GitHubTransferAttemptIntegrity.Build(
            packet, review with { ObservedAtUtc = now }, token, 5, actor, now);
        Assert.Equal(packet.ScopeId, draft.ScopeId);
        Assert.Equal(packet.RepositoryId, draft.RepositoryId);
        Assert.Equal(packet.EvidenceSha256, draft.PacketSha256);
        Assert.Equal(review.EvidenceSha256, draft.PolicySha256);
        Assert.Equal(token, draft.LeaseToken);
        Assert.Equal(5, draft.LeaseGeneration);
        Assert.Equal(actor, draft.RequestedBy);
        Assert.DoesNotContain("refs/", System.Text.Json.JsonSerializer.Serialize(draft));
        Assert.DoesNotContain("ghs_", System.Text.Json.JsonSerializer.Serialize(draft));
    }

    [Fact]
    public void Deny_missing_or_stale_or_mismatched_packet_policy_and_any_execution_flag()
    {
        var (packet, review) = Fixture();
        var now = DateTimeOffset.UtcNow;
        void Deny(GitHubCreateOnlyPacket p, GitHubCandidatePolicyReview r,
            Guid? token = null, long gen = 2) =>
            Assert.Equal("TransferPreparationEvidenceInvalid",
                Assert.Throws<GitHubTransferAttemptException>(() =>
                    GitHubTransferAttemptIntegrity.Build(
                        p, r, token ?? Guid.NewGuid(), gen, Guid.NewGuid(), now)).Code);
        Deny(packet, review with { ObservedAtUtc = now.AddSeconds(-31) });
        Deny(packet, review with { ObservedAtUtc = now.AddSeconds(6) });
        Deny(packet, review with { Status = "HumanReviewRequired" });
        Deny(packet, review with { RulesRequireReview = 1 });
        Deny(packet, review with { PolicyUnverified = 1 });
        Deny(packet, review with { PacketSha256 = Sha('f') });
        Deny(packet, review with { EvidenceSha256 = "wrong" });
        Deny(packet with { ExecutionAuthorized = true }, review);
        Deny(packet with { WriteCapabilityVerified = true }, review);
        Deny(packet with { RemoteWritesPerformed = true }, review);
        Deny(packet with { ReadyForExecution = true }, review);
        Deny(packet, review with { ExecutionAuthorized = true });
        Deny(packet, review, Guid.Empty);
        Deny(packet, review, gen: 0);
    }

    private static string Sha(char c) => new(c, 64);

    private static (GitHubCreateOnlyPacket, GitHubCandidatePolicyReview) Fixture()
    {
        var scopeId = Guid.NewGuid();
        var packet = new GitHubCreateOnlyPacket(
            Guid.NewGuid(), Guid.NewGuid(), scopeId, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2468,
            "gh-org", "sandbox", Sha('1'), Sha('2'), Sha('3'), Sha('4'),
            Sha('5'), Sha('6'), Sha('7'), Sha('8'), Sha('9'),
            Sha('a'), Sha('b'), 1, 0, 0,
            GitHubCreateOnlyPacketIntegrity.State, false, false, false, false);
        var review = new GitHubCandidatePolicyReview(scopeId, 2468,
            packet.EvidenceSha256, Sha('c'), DateTimeOffset.UtcNow, 1, 1, 0, 0,
            "AdvisoryNoRulesReported", [
                new(Sha('d'), "Branch",
                    GitHubCandidatePolicyStates.NoActiveRulesReported, 0, "Observed")
            ], false, false, false, false);
        return (packet, review);
    }
}
