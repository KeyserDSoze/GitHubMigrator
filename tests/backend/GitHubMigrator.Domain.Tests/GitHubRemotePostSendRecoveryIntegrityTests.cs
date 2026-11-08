using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemotePostSendRecoveryIntegrityTests
{
    private static string Sha(char c) => new(c, 64);

    private static GitHubRemotePostSendClosureCheckpoint Valid()
    {
        var now = DateTimeOffset.UtcNow;
        var first = new GitHubRemotePostSendClosureCheckpoint(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), 12345,
            Guid.NewGuid(), Sha('a'), Sha('b'), Sha('c'),
            Sha('d'), now.AddMinutes(-2), now, "",
            "", false, false, false)
        {
            State = GitHubRemotePostSendRecoveryIntegrity.CheckpointState
        };
        return first with {
            EvidenceSha256 =
                GitHubRemotePostSendRecoveryIntegrity.CheckpointDigest(first)
        };
    }

    [Fact]
    public void F085_entire_independent_lineage_is_sha_sealed()
    {
        var r = Valid();
        GitHubRemotePostSendRecoveryIntegrity.VerifyCheckpoint(r);
        foreach (var changed in new[] {
            r with { BarrierId = Guid.NewGuid() },
            r with { ConfirmationId = Guid.NewGuid() },
            r with { IntentId = Guid.NewGuid() },
            r with { HazardId = Guid.NewGuid() },
            r with { GrantId = Guid.NewGuid() },
            r with { ReadbackId = Guid.NewGuid() },
            r with { ReconciliationId = Guid.NewGuid() },
            r with { RepositoryId = 23456 },
            r with { RecordedBy = Guid.NewGuid() },
            r with { BarrierEvidenceSha256 = Sha('f') },
            r with { ReadbackEvidenceSha256 = Sha('f') },
            r with { ReconciliationEvidenceSha256 = Sha('f') },
            r with { FullObjectClosureSha256 = Sha('f') },
            r with { VerifiedAtUtc = r.VerifiedAtUtc.AddSeconds(1) },
            r with { RecordedAtUtc = r.RecordedAtUtc.AddSeconds(1) }
        })
            Assert.Throws<GitHubRemotePostSendRecoveryException>(() =>
                GitHubRemotePostSendRecoveryIntegrity.VerifyCheckpoint(changed));
    }

    [Fact]
    public void F085_never_seals_dispatch_retry_or_migration_complete()
    {
        var r = Valid();
        foreach (var changed in new[] {
            r with { State = "SentAndComplete" },
            r with { CanRetry = true },
            r with { CanDispatch = true },
            r with { MigrationComplete = true },
            r with { RecordedBy = Guid.Empty },
            r with { ReadbackEvidenceSha256 = "unverified" },
            r with { VerifiedAtUtc = r.RecordedAtUtc.AddMinutes(3) }
        })
            Assert.Throws<GitHubRemotePostSendRecoveryException>(() =>
                GitHubRemotePostSendRecoveryIntegrity.CheckpointDigest(changed));
    }

    [Fact]
    public void F085_f058_rehash_refuses_forged_full_object_sha_or_success_flags()
    {
        var p = new GitHubRemoteObjectReconciliationStatus(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), 223344, Guid.NewGuid(),
            GitHubRemoteObjectReconciliationStates.FullCopyEvidenceOnly,
            Sha('a'), Sha('b'), Sha('c'), Sha('d'),
            Sha('e'), Sha('f'), "", Guid.NewGuid(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            false, false, false, false);
        var good = p with {
            EvidenceSha256 =
                GitHubRemotePostSendRecoveryIntegrity.FullClosureDigest(p)
        };
        GitHubRemotePostSendRecoveryIntegrity.VerifyFullClosure(good);
        Assert.Throws<GitHubRemotePostSendRecoveryException>(() =>
            GitHubRemotePostSendRecoveryIntegrity.VerifyFullClosure(
                good with { FullObjectClosureSha256 = Sha('0') }));
        Assert.Throws<GitHubRemotePostSendRecoveryException>(() =>
            GitHubRemotePostSendRecoveryIntegrity.VerifyFullClosure(
                good with { MigrationComplete = true }));
        Assert.Throws<GitHubRemotePostSendRecoveryException>(() =>
            GitHubRemotePostSendRecoveryIntegrity.VerifyFullClosure(
                good with { ReadbackId = Guid.NewGuid() }));
    }
}
