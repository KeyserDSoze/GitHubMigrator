using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteSandboxPreSendBarrierIntegrityTests
{
    private static GitHubRemoteSandboxPreSendBarrierReceipt Sample()
    {
        var draft = new GitHubRemoteSandboxPreSendBarrierReceipt(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 123456,
            Guid.NewGuid(), new string('a', 64), new string('b', 64),
            new string('c', 64), new string('d', 64),
            new string('e', 64), new string('f', 64),
            DateTimeOffset.UtcNow, "",
            GitHubRemoteSandboxPreSendBarrierIntegrity.Recorded,
            false, false, false, false);
        return draft with {
            EvidenceSha256 = GitHubRemoteSandboxPreSendBarrierIntegrity.Digest(draft)
        };
    }

    [Fact]
    public void F082_all_lineage_and_operator_are_bound_to_canonical_SHA()
    {
        var receipt = Sample();
        GitHubRemoteSandboxPreSendBarrierIntegrity.Verify(receipt);
        Assert.Equal(64, receipt.EvidenceSha256.Length);
        Assert.Equal(receipt.EvidenceSha256,
            GitHubRemoteSandboxPreSendBarrierIntegrity.Digest(receipt));
        foreach (var changed in new[] {
            receipt with { ConfirmationId = Guid.NewGuid() },
            receipt with { IntentId = Guid.NewGuid() },
            receipt with { SessionId = Guid.NewGuid() },
            receipt with { HazardId = Guid.NewGuid() },
            receipt with { GrantId = Guid.NewGuid() },
            receipt with { RepositoryId = 111222 },
            receipt with { MarkedBy = Guid.NewGuid() },
            receipt with { ConfirmationEvidenceSha256 = new string('0', 64) },
            receipt with { IntentEvidenceSha256 = new string('0', 64) },
            receipt with { SessionEvidenceSha256 = new string('0', 64) },
            receipt with { PacketSha256 = new string('0', 64) },
            receipt with { CandidateSetSha256 = new string('0', 64) },
            receipt with { LiveRefEvidenceSha256 = new string('0', 64) },
            receipt with { RecordedAtUtc = receipt.RecordedAtUtc.AddSeconds(1) }
        })
            Assert.Throws<GitHubRemoteSandboxPreSendBarrierException>(() =>
                GitHubRemoteSandboxPreSendBarrierIntegrity.Verify(changed));
    }

    [Fact]
    public void F082_cannot_seal_write_permission_or_a_retryable_outcome()
    {
        var receipt = Sample();
        foreach (var invalid in new[] {
            receipt with { ConfirmationId = Guid.Empty },
            receipt with { MarkedBy = Guid.Empty },
            receipt with { ConfirmationEvidenceSha256 = "untrusted" },
            receipt with { State = "TransportAuthorized" },
            receipt with { CanExecute = true },
            receipt with { TransportInvoked = true },
            receipt with { MayAutomaticallyRetry = true },
            receipt with { MigrationComplete = true }
        })
            Assert.Throws<GitHubRemoteSandboxPreSendBarrierException>(() =>
                GitHubRemoteSandboxPreSendBarrierIntegrity.Digest(invalid));
    }
}
