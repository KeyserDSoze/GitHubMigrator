using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real filesystem and real Git adapter boundary, but NO GitHub network:
// invalid/disabled requests must exit before a Git subprocess is started.
public sealed class GitHubSandboxCreateOnlyPushRunnerTests
{
    [Fact]
    public async Task F083_disabled_or_forged_sender_never_creates_a_git_workspace()
    {
        var evidence = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        var packet = evidence.Source.Packet;
        var now = DateTimeOffset.UtcNow;
        var bundle = new GitSourceBundleView(
            packet.WorkpackId, packet.PlanId, Guid.NewGuid(),
            "source", new string('9', 64), packet.ReconciliationSha256,
            "not-used.bundle", packet.BundleSha256, 1024, 2,
            now, true, false);
        var unsealed = new GitHubRemoteSandboxPreSendBarrierReceipt(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), evidence.HazardId, evidence.GrantId,
            packet.RepositoryId, Guid.NewGuid(),
            new string('a', 64), new string('b', 64),
            new string('c', 64), packet.EvidenceSha256,
            packet.CandidateSetSha256, evidence.Latest.EvidenceSha256,
            now, "", GitHubRemoteSandboxPreSendBarrierIntegrity.Recorded,
            false, false, false, false);
        var barrier = unsealed with {
            EvidenceSha256 = GitHubRemoteSandboxPreSendBarrierIntegrity.Digest(
                unsealed)
        };
        var input = new GitHubSandboxPushInput(
            evidence.Source, bundle, barrier,
            evidence.Preflight.ObservedBaselineSha256, evidence.Latest);
        var sandbox = Path.Combine(Path.GetTempPath(),
            "f083-never-connect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var off = new GitHubSandboxCreateOnlyPushRunner(
                "/not-configured", sandbox, new(false, packet.RepositoryId,
                    "gh-org", "sandbox"));
            var disabled = await Assert.ThrowsAsync<
                GitHubSandboxOneShotException>(() =>
                    off.PushOnceAsync(input, "test-one-repository-token", default));
            Assert.Equal("SandboxPushEvidenceInvalid", disabled.Code);
            Assert.Empty(Directory.GetFileSystemEntries(sandbox));

            var on = new GitHubSandboxCreateOnlyPushRunner(
                "/not-configured", sandbox, new(true, packet.RepositoryId,
                    "gh-org", "sandbox"));
            var forged = await Assert.ThrowsAsync<
                GitHubSandboxOneShotException>(() =>
                    on.PushOnceAsync(input with {
                        Barrier = barrier with { CanExecute = true }
                    }, "test-one-repository-token", default));
            Assert.Equal("SandboxPushEvidenceInvalid", forged.Code);
            Assert.Empty(Directory.GetFileSystemEntries(sandbox));

            var token = await Assert.ThrowsAsync<GitHubSandboxOneShotException>(
                () => on.PushOnceAsync(input, "short", default));
            Assert.Equal("SandboxPushTokenInvalid", token.Code);
            Assert.Empty(Directory.GetFileSystemEntries(sandbox));
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }
}
