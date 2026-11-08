using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

// Offline cryptographic contract: the second human never acquires
// a sender or automatic retry authority by altering receipt fields.
public sealed class GitHubRemoteSandboxDispatchConfirmationIntegrityTests
{
    private static GitHubRemoteSandboxDispatchConfirmationReceipt Sample()
    {
        var now = DateTimeOffset.UtcNow;
        var draft = new GitHubRemoteSandboxDispatchConfirmationReceipt(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), 777100, Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), new string('b', 64),
            new string('c', 64), new string('d', 64), new string('e', 64),
            now, now.AddSeconds(9), "",
            GitHubRemoteSandboxDispatchConfirmationIntegrity.Recorded,
            false, false, false, false);
        return draft with {
            EvidenceSha256 =
                GitHubRemoteSandboxDispatchConfirmationIntegrity.Digest(draft)
        };
    }

    [Fact]
    public void F081_hash_seals_manual_actors_and_all_predecessor_evidence()
    {
        var receipt = Sample();
        GitHubRemoteSandboxDispatchConfirmationIntegrity.Verify(receipt);
        Assert.Equal(64, receipt.EvidenceSha256.Length);
        Assert.Equal(receipt.EvidenceSha256,
            GitHubRemoteSandboxDispatchConfirmationIntegrity.Digest(receipt));
        foreach (var changed in new[] {
            receipt with { IntentId = Guid.NewGuid() },
            receipt with { SessionId = Guid.NewGuid() },
            receipt with { HazardId = Guid.NewGuid() },
            receipt with { GrantId = Guid.NewGuid() },
            receipt with { RepositoryId = 777101 },
            receipt with { RequestedBy = Guid.NewGuid() },
            receipt with { ConfirmedBy = Guid.NewGuid() },
            receipt with { IntentEvidenceSha256 = new string('f', 64) },
            receipt with { SessionEvidenceSha256 = new string('f', 64) },
            receipt with { PacketSha256 = new string('f', 64) },
            receipt with { CandidateSetSha256 = new string('f', 64) },
            receipt with { LiveRefEvidenceSha256 = new string('f', 64) },
            receipt with { ExpiresAtUtc = receipt.ExpiresAtUtc.AddSeconds(1) }
        })
            Assert.Throws<GitHubRemoteSandboxDispatchConfirmationException>(
                () => GitHubRemoteSandboxDispatchConfirmationIntegrity.Verify(changed));
    }

    [Fact]
    public void F081_cannot_seal_self_confirmation_bogus_hash_or_write_capability()
    {
        var receipt = Sample();
        foreach (var invalid in new[] {
            receipt with { RequestedBy = receipt.ConfirmedBy },
            receipt with { RequestedBy = Guid.Empty },
            receipt with { ConfirmedBy = Guid.Empty },
            receipt with { IntentEvidenceSha256 = "not-a-sha" },
            receipt with { ExpiresAtUtc = receipt.RecordedAtUtc },
            receipt with { State = "ApprovedCanExecute" },
            receipt with { CanDispatch = true },
            receipt with { TransportInvoked = true },
            receipt with { MayAutomaticallyRetry = true },
            receipt with { MigrationComplete = true }
        })
            Assert.Throws<GitHubRemoteSandboxDispatchConfirmationException>(
                () => GitHubRemoteSandboxDispatchConfirmationIntegrity.Digest(invalid));
    }
}
