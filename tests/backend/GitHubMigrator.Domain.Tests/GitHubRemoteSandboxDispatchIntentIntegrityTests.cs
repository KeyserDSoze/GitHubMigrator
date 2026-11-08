using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

// F-080 never emits an executable capability, even when callers supply
// a forged valid-looking hash. All important fields are hash-bound.
public sealed class GitHubRemoteSandboxDispatchIntentIntegrityTests
{
    private static GitHubRemoteSandboxDispatchIntentReceipt Sample()
    {
        var now = DateTimeOffset.UtcNow;
        var draft = new GitHubRemoteSandboxDispatchIntentReceipt(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            123456789, Guid.NewGuid(),
            new string('a', 64), new string('b', 64),
            new string('c', 64), new string('d', 64), new string('e', 64),
            now, now.AddSeconds(10), "",
            GitHubRemoteSandboxDispatchIntentIntegrity.Recorded,
            false, false, false, false);
        return draft with {
            EvidenceSha256 = GitHubRemoteSandboxDispatchIntentIntegrity.Digest(draft)
        };
    }

    [Fact]
    public void F080_canonical_intent_is_stable_and_hash_binds_all_lineage()
    {
        var value = Sample();
        GitHubRemoteSandboxDispatchIntentIntegrity.Verify(value);
        Assert.Equal(64, value.EvidenceSha256.Length);
        Assert.Equal(value.EvidenceSha256,
            GitHubRemoteSandboxDispatchIntentIntegrity.Digest(value));
        foreach (var changed in new[] {
            value with { SessionId = Guid.NewGuid() },
            value with { HazardId = Guid.NewGuid() },
            value with { GrantId = Guid.NewGuid() },
            value with { RequestedBy = Guid.NewGuid() },
            value with { SessionEvidenceSha256 = new string('f', 64) },
            value with { PacketSha256 = new string('f', 64) },
            value with { CandidateSetSha256 = new string('f', 64) },
            value with { LiveRefEvidenceSha256 = new string('f', 64) },
            value with { ExpiresAtUtc = value.ExpiresAtUtc.AddSeconds(1) }
        })
            Assert.Throws<GitHubRemoteSandboxDispatchIntentException>(
                () => GitHubRemoteSandboxDispatchIntentIntegrity.Verify(changed));
    }

    [Fact]
    public void F080_invalid_actor_time_hash_or_enabled_flags_cannot_be_sealed()
    {
        var value = Sample();
        foreach (var invalid in new[] {
            value with { RequestedBy = Guid.Empty },
            value with { SessionId = Guid.Empty },
            value with { ExpiresAtUtc = value.RecordedAtUtc },
            value with { SessionEvidenceSha256 = "not-a-sha" },
            value with { State = "Executable" },
            value with { CanExecute = true },
            value with { TransportInvoked = true },
            value with { MayAutomaticallyRetry = true },
            value with { MigrationComplete = true }
        })
            Assert.Throws<GitHubRemoteSandboxDispatchIntentException>(
                () => GitHubRemoteSandboxDispatchIntentIntegrity.Digest(invalid));
    }
}
