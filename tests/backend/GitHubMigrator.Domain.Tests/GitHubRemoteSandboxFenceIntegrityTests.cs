using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteSandboxFenceIntegrityTests
{
    [Fact]
    public void Only_exact_allowlisted_current_two_person_review_can_prepare_fence()
    {
        var input = Valid();
        var options = new GitHubRemoteSandboxFenceOptions(
            true, input.Packet.RepositoryId, "GH-ORG/SANDBOX");
        GitHubRemoteSandboxFenceIntegrity.Verify(input, options);

        Assert.Equal(64,
            GitHubRemoteSandboxFenceIntegrity.TokenDigest(Guid.NewGuid()).Length);
        Assert.NotEqual(
            GitHubRemoteSandboxFenceIntegrity.TokenDigest(Guid.NewGuid()),
            GitHubRemoteSandboxFenceIntegrity.TokenDigest(Guid.NewGuid()));
        Assert.True(GitHubRemoteSandboxFenceIntegrity.ReviewWindow <
            TimeSpan.FromSeconds(30));
        Assert.False(options.Enabled == false);
    }

    [Fact]
    public void Reject_forged_scope_repository_policy_or_runtime_execution_proof()
    {
        var input = Valid();
        void Reject(GitHubRemoteSandboxFenceInput candidate,
            GitHubRemoteSandboxFenceOptions? options = null) =>
            Assert.ThrowsAny<Exception>(() =>
                GitHubRemoteSandboxFenceIntegrity.Verify(candidate,
                    options ?? new GitHubRemoteSandboxFenceOptions(
                        true, input.Packet.RepositoryId, "gh-org/sandbox")));

        Reject(input, new GitHubRemoteSandboxFenceOptions(false,
            input.Packet.RepositoryId, "gh-org/sandbox"));
        Reject(input, new GitHubRemoteSandboxFenceOptions(true,
            input.Packet.RepositoryId + 1, "gh-org/sandbox"));
        Reject(input, new GitHubRemoteSandboxFenceOptions(true,
            input.Packet.RepositoryId, "gh-org/other"));
        Reject(input with { ActorId = Guid.Empty });
        Reject(input with { Packet = input.Packet with {
            ExecutionAuthorized = true } });
        Reject(input with { Packet = input.Packet with {
            RemoteWritesPerformed = true } });
        Reject(input with { Packet = input.Packet with {
            ReadyForExecution = true } });
        Reject(input with { Packet = input.Packet with {
            WriteCapabilityVerified = true } });
        Reject(input with { Packet = input.Packet with {
            EvidenceSha256 = new string('f', 64) } });
        Reject(input with { Attempt = input.Attempt with {
            ScopeId = Guid.NewGuid() } });
        Reject(input with { Attempt = input.Attempt with {
            State = GitHubTransferAttemptStates.Reviewed } });
        Reject(input with { Attempt = input.Attempt with {
            ReviewedBy = input.Attempt.RequestedBy } });
        Reject(input with { Attempt = input.Attempt with {
            ReviewedBy = null } });
        Reject(input with { Attempt = input.Attempt with {
            PolicySha256 = "fake" } });
        Reject(input with { Attempt = input.Attempt with {
            TransportAttempted = true } });
        Reject(input with { Attempt = input.Attempt with {
            ExecutionAuthorized = true } });
    }

    private static GitHubRemoteSandboxFenceInput Valid()
    {
        static string Sha(char c) => new(c, 64);
        var packet = new GitHubCreateOnlyPacket(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            123456789, "gh-org", "sandbox", Sha('1'), Sha('2'),
            Sha('3'), Sha('4'), Sha('5'), Sha('6'), Sha('7'),
            Sha('8'), Sha('9'), Sha('a'), Sha('b'), 1, 0, 0,
            GitHubCreateOnlyPacketIntegrity.State,
            false, false, false, false);
        var now = DateTimeOffset.UtcNow;
        var attempt = new GitHubTransferAttemptView(
            Guid.NewGuid(), packet.PreparationId, packet.PlanId,
            packet.ScopeId, packet.RepositoryId, packet.Organization,
            packet.Repository, packet.EvidenceSha256, Sha('c'),
            packet.CandidateSetSha256, 1, 1,
            GitHubTransferAttemptStates.BlockedAtWriteBarrier,
            Guid.NewGuid(), Guid.NewGuid(),
            now, now, now, null, false, false, false, false);
        return new(packet, attempt, Guid.NewGuid());
    }
}
