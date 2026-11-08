using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

// F-083 tests are provider-free: they exercise the actual orchestration
// and the exact irreversible F-082-before-writer call boundary. The
// existing F-082 integration suite separately proves PostgreSQL CAS.
public sealed class GitHubSandboxOneShotSenderTests
{
    private static string Sha(char c) => new(c, 64);
    private static string Oid(char c) => new(c, 40);

    private sealed class PacketReader(GitHubCreateOnlyCandidatePacket source)
        : IGitHubCreateOnlyPacketEvidenceReader
    {
        public int Calls;
        public GitHubCreateOnlyCandidatePacket? Replacement;
        public Task<GitHubCreateOnlyCandidatePacket> PreviewWithCandidatesAsync(
            Guid preparationId, Guid planId, Guid scopeId, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(source.Packet.PreparationId, preparationId);
            Assert.Equal(source.Packet.PlanId, planId);
            Assert.Equal(source.Packet.ScopeId, scopeId);
            return Task.FromResult(Calls >= 2 && Replacement is not null
                ? Replacement : source);
        }
    }

    private sealed class BundleStore(GitSourceBundleView bundle)
        : IGitSourceBundleEvidenceStore
    {
        public int Reads;
        public Task<GitSourceBundleView?> GetAsync(
            Guid preparationId, Guid planId, Guid workpackId,
            CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<GitSourceBundleView?>(bundle);
        }

        public Task<GitSourceBundleView> SaveAsync(
            Guid preparationId, Guid planId, Guid workpackId,
            string expectedReconciliationSha256, GitSourceBundleArtifact artifact,
            CancellationToken ct) =>
            throw new InvalidOperationException("No saved bundle changes");
    }

    private sealed class Disk(GitSourceBundleView bundle)
        : IGitSourceBundleArtifactVerifier
    {
        public int Reads;
        public bool Changed;
        public Task<GitSourceBundleStorageCheck> VerifyAsync(
            GitSourceBundleView evidence, CancellationToken ct)
        {
            Reads++;
            Assert.Equal(bundle.WorkpackId, evidence.WorkpackId);
            return Task.FromResult(new GitSourceBundleStorageCheck(
                bundle.WorkpackId, Changed ? Sha('0') : bundle.BundleSha256,
                bundle.BundleBytes, DateTimeOffset.UtcNow, !Changed, false));
        }
    }

    private sealed class Secrets : IConnectionSecretStore
    {
        public int Reads;
        public Task<string?> ReadAsync(string reference, CancellationToken ct)
        {
            Reads++;
            Assert.Equal("sandbox/app-private-key", reference);
            return Task.FromResult<string?>("private-key-in-memory-test");
        }
        public Task SaveAsync(string reference, string secret,
            CancellationToken ct) => throw new InvalidOperationException();
        public Task DeleteAsync(string reference, CancellationToken ct) =>
            throw new InvalidOperationException();
    }

    private sealed class Tokens : IGitHubSandboxVerifiedTokenScope
    {
        public int Calls;
        public Task<GitHubSandboxOneShotOutcome> WithVerifiedTokenAsync(
            long appId, long installationId, string privateKeyPem,
            string organization, string repository, long expectedRepositoryId,
            Func<string, CancellationToken, Task<GitHubSandboxOneShotOutcome>> action,
            CancellationToken ct)
        {
            Calls++;
            Assert.Equal(1234, appId);
            Assert.Equal(88, installationId);
            Assert.Equal(2468, expectedRepositoryId);
            Assert.Equal("gh-org", organization);
            Assert.Equal("sandbox", repository);
            Assert.Equal("private-key-in-memory-test", privateKeyPem);
            return action("test-one-repository-token", ct);
        }
    }

    private sealed class RefReader(GitHubRefObservation baseline)
        : IGitHubRefObservationReader
    {
        public GitHubRefObservation Baseline => baseline;
        public int Calls;
        public GitHubRefObservation? Replacement;
        public Task<GitHubRefObservation> ReadAsync(
            string organization, string repository, string token,
            CancellationToken ct)
        {
            Calls++;
            Assert.Equal("gh-org", organization);
            Assert.Equal("sandbox", repository);
            Assert.Equal("test-one-repository-token", token);
            return Task.FromResult(Calls >= 2 && Replacement is not null
                ? Replacement : baseline);
        }
    }

    private sealed class Policies(GitHubMirrorRefCandidate candidate)
        : IGitHubCandidatePolicyReader
    {
        public int Calls;
        public bool RulesActive;
        public Task<GitHubCandidatePolicyObservation> ReadAsync(
            string organization, string repository, long expectedRepositoryId,
            string readOnlyToken, IReadOnlyList<GitMirrorRef> candidates,
            CancellationToken ct)
        {
            Calls++;
            Assert.Equal("gh-org", organization);
            Assert.Equal("sandbox", repository);
            Assert.Equal(2468, expectedRepositoryId);
            Assert.Equal("test-one-repository-token", readOnlyToken);
            Assert.Single(candidates);
            Assert.Equal(candidate.Ref.Name, candidates[0].Name);
            return Task.FromResult(new GitHubCandidatePolicyObservation(
                [new GitHubCandidateRuleStatus(
                    GitHubCandidatePolicyIntegrity.RefNameDigest(
                        candidate.Ref.Name),
                    "Branch", RulesActive ?
                        GitHubCandidatePolicyStates.RulesRequireReview :
                        GitHubCandidatePolicyStates.NoActiveRulesReported,
                    RulesActive ? 1 : 0, "Observed")],
                DateTimeOffset.UtcNow, "Observed"));
        }
    }

    private sealed record GitHubMirrorRefCandidate(GitMirrorRef Ref);

    private sealed class Authority(
        GitHubRemoteSandboxPreSendBarrierReceipt barrier, string baseline)
        : IGitHubSandboxPreSendAuthority
    {
        public int BaselineReads;
        public int Marks;
        public Task<string> GetBaselineAsync(
            Guid confirmationId, Guid intentId, Guid preparationId,
            Guid planId, Guid scopeId, long repositoryId, CancellationToken ct)
        {
            BaselineReads++;
            Assert.Equal(barrier.ConfirmationId, confirmationId);
            Assert.Equal(barrier.IntentId, intentId);
            Assert.Equal(barrier.RepositoryId, repositoryId);
            return Task.FromResult(baseline);
        }

        public Task<GitHubRemoteSandboxPreSendBarrierReceipt> MarkOnceAsync(
            Guid confirmationId, Guid intentId, Guid preparationId,
            Guid planId, Guid scopeId, Guid operatorId, CancellationToken ct)
        {
            Marks++;
            if (Marks > 1)
                throw new GitHubRemoteSandboxPreSendBarrierException(
                    "PreSendBarrierConfirmationStale");
            Assert.Equal(barrier.ConfirmationId, confirmationId);
            Assert.Equal(barrier.IntentId, intentId);
            Assert.Equal(barrier.MarkedBy, operatorId);
            return Task.FromResult(barrier);
        }
    }

    private sealed class Writer : IGitHubSandboxOneShotGitWriter
    {
        public int Calls;
        public bool Fail;
        public Authority? Authority;
        public Task PushOnceAsync(GitHubSandboxPushInput request,
            string oneRepositoryInstallationToken, CancellationToken ct)
        {
            Calls++;
            Assert.NotNull(Authority);
            Assert.Equal(1, Authority.Marks);
            Assert.Equal("test-one-repository-token",
                oneRepositoryInstallationToken);
            Assert.Equal(request.Barrier.PacketSha256,
                request.Source.Packet.EvidenceSha256);
            if (Fail) throw new IOException("simulate lost push acknowledgement");
            return Task.CompletedTask;
        }
    }

    private sealed class Readback(GitHubRefObservation complete,
        IReadOnlyList<GitMirrorRef> candidates) : IGitHubRemoteTipObjectReader
    {
        public int Calls;
        public Task<GitHubRemoteTipSnapshot> ReadAsync(
            string organization, string repository, string token,
            IReadOnlyList<GitMirrorRef> createCandidates, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("test-one-repository-token", token);
            Assert.Equal(candidates, createCandidates);
            var items = candidates.Select(x => new GitHubRemoteTipObject(
                x.Name, x.ObjectId, "commit", x.ObjectId, Oid('c'), 0))
                .ToArray();
            return Task.FromResult(new GitHubRemoteTipSnapshot(
                complete, GitHubRemoteTipIntegrity.Seal(
                    complete, candidates, DateTimeOffset.UtcNow, items)));
        }
    }

    private sealed class Fixture
    {
        public readonly GitHubCreateOnlyCandidatePacket Source;
        public readonly GitSourceBundleView Bundle;
        public readonly GitHubRemoteSandboxPreSendBarrierReceipt Barrier;
        public readonly PacketReader Packets;
        public readonly BundleStore Bundles;
        public readonly Disk Physical;
        public readonly Secrets Secret = new();
        public readonly Tokens Token = new();
        public readonly RefReader Refs;
        public readonly Policies Policy;
        public readonly Authority Marker;
        public readonly Writer Transport = new();
        public readonly Readback Tips;

        public Fixture()
        {
            var now = DateTimeOffset.UtcNow;
            var candidate = new GitMirrorRef("refs/heads/new", Oid('b'));
            var digest = GitImportRehearsalIntegrity.SealRefs(
                "f044-create-only-candidate-refs-v1", [candidate]);
            var packet = new GitHubCreateOnlyPacket(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), 2468, "gh-org", "sandbox",
                Sha('1'), Sha('2'), Sha('3'), Sha('4'), Sha('5'),
                Sha('6'), Sha('7'), Sha('8'), Sha('a'), digest,
                Sha('9'), 1, 1, 1, GitHubCreateOnlyPacketIntegrity.State,
                false, false, false, false);
            Source = new(packet, [candidate]);
            Bundle = new GitSourceBundleView(
                packet.WorkpackId, packet.PlanId, Guid.NewGuid(),
                "source-repo", Sha('2'), packet.ReconciliationSha256,
                "sample.bundle", packet.BundleSha256, 1024, 2, now,
                true, false);
            var raw = new GitHubRemoteSandboxPreSendBarrierReceipt(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                packet.RepositoryId, Guid.NewGuid(), Sha('c'),
                Sha('d'), Sha('e'), packet.EvidenceSha256,
                packet.CandidateSetSha256, Sha('f'), now,
                "", GitHubRemoteSandboxPreSendBarrierIntegrity.Recorded,
                false, false, false, false);
            Barrier = raw with {
                EvidenceSha256 = GitHubRemoteSandboxPreSendBarrierIntegrity
                    .Digest(raw)
            };
            var original = GitHubRefObservationIntegrity.Seal(
                "gh-org", "sandbox", now,
                [new GitHubObservedRef("refs/heads/main", Oid('a'), "commit")]);
            var complete = GitHubRefObservationIntegrity.Seal(
                "gh-org", "sandbox", now,
                [new GitHubObservedRef("refs/heads/main", Oid('a'), "commit"),
                 new GitHubObservedRef(candidate.Name, candidate.ObjectId, "commit")]);
            Packets = new(Source);
            Bundles = new(Bundle);
            Physical = new(Bundle);
            Refs = new(original);
            Policy = new(new(candidate));
            Marker = new(Barrier,
                GitHubRemoteRefFenceIntegrity.Fingerprint(original.Refs));
            Transport.Authority = Marker;
            Tips = new(complete, [candidate]);
        }

        public GitHubSandboxOneShotSender Service(bool enabled = true) =>
            new(new(enabled, 2468, "gh-org", "sandbox"),
                new(true, 1234, 88, "sandbox/app-private-key", "gh-org/sandbox"),
                Secret, Packets, Bundles, Physical, Token,
                Refs, Policy, Marker, Transport, Tips);

        public Task<GitHubSandboxOneShotOutcome> RunAsync(
            GitHubSandboxOneShotSender? service = null) =>
            (service ?? Service()).SendOnceAsync(
                Barrier.ConfirmationId, Barrier.IntentId,
                Source.Packet.PreparationId, Source.Packet.PlanId,
                Source.Packet.ScopeId, Barrier.MarkedBy,
                CancellationToken.None);
    }

    [Fact]
    public async Task F083_disabled_refuses_before_any_secret_disk_db_or_provider_access()
    {
        var f = new Fixture();
        var error = await Assert.ThrowsAsync<GitHubSandboxOneShotException>(
            () => f.RunAsync(f.Service(enabled: false)));
        Assert.Equal("SandboxSenderDisabled", error.Code);
        Assert.Equal(0, f.Packets.Calls);
        Assert.Equal(0, f.Bundles.Reads);
        Assert.Equal(0, f.Physical.Reads);
        Assert.Equal(0, f.Secret.Reads);
        Assert.Equal(0, f.Token.Calls);
        Assert.Equal(0, f.Marker.Marks);
        Assert.Equal(0, f.Transport.Calls);
    }

    [Fact]
    public async Task F083_marks_once_before_push_and_requires_exact_remote_tip_readback()
    {
        var f = new Fixture();
        var outcome = await f.RunAsync();
        Assert.Equal(f.Barrier.Id, outcome.BarrierId);
        Assert.Equal("SandboxRemoteTipsObservedNeedsClosureAudit",
            outcome.Status);
        Assert.True(outcome.TransportMayHaveRun);
        Assert.True(outcome.RequiresIndependentReconciliation);
        Assert.False(outcome.CanRetry);
        Assert.False(outcome.MigrationComplete);
        Assert.Equal(2, f.Packets.Calls);
        Assert.Equal(2, f.Physical.Reads);
        Assert.Equal(2, f.Refs.Calls);
        Assert.Equal(1, f.Policy.Calls);
        Assert.Equal(1, f.Marker.Marks);
        Assert.Equal(1, f.Transport.Calls);
        Assert.Equal(1, f.Tips.Calls);
    }

    [Fact]
    public async Task F083_lost_push_acknowledgement_is_uncertain_and_must_never_retry()
    {
        var f = new Fixture();
        f.Transport.Fail = true;
        var outcome = await f.RunAsync();
        Assert.Equal("SandboxRemoteOutcomeRequiresReconciliation", outcome.Status);
        Assert.True(outcome.TransportMayHaveRun);
        Assert.False(outcome.CanRetry);
        Assert.False(outcome.MigrationComplete);
        Assert.Equal(1, f.Marker.Marks);
        Assert.Equal(1, f.Transport.Calls);
        Assert.Equal(0, f.Tips.Calls);

        await Assert.ThrowsAsync<GitHubRemoteSandboxPreSendBarrierException>(
            () => f.RunAsync());
        Assert.Equal(1, f.Transport.Calls);
    }

    [Fact]
    public async Task F083_policy_and_remote_ref_drift_do_not_consume_pre_send_barrier()
    {
        var rules = new Fixture();
        rules.Policy.RulesActive = true;
        var error = await Assert.ThrowsAsync<GitHubSandboxOneShotException>(
            () => rules.RunAsync());
        Assert.Equal("SandboxSenderPolicyUnverified", error.Code);
        Assert.Equal(0, rules.Marker.Marks);
        Assert.Equal(0, rules.Transport.Calls);

        var refs = new Fixture();
        refs.Refs.Replacement = GitHubRefObservationIntegrity.Seal(
            "gh-org", "sandbox", DateTimeOffset.UtcNow,
            [new("refs/heads/main", Oid('b'), "commit")]);
        error = await Assert.ThrowsAsync<GitHubSandboxOneShotException>(
            () => refs.RunAsync());
        Assert.Equal("SandboxSenderRemoteDrift", error.Code);
        Assert.Equal(0, refs.Marker.Marks);
        Assert.Equal(0, refs.Transport.Calls);

        var bundle = new Fixture();
        bundle.Physical.Changed = true;
        error = await Assert.ThrowsAsync<GitHubSandboxOneShotException>(
            () => bundle.RunAsync());
        Assert.Equal("SandboxSenderBundleChanged", error.Code);
        Assert.Equal(0, bundle.Marker.Marks);
        Assert.Equal(0, bundle.Transport.Calls);
    }

    [Fact]
    public void F083_forged_packet_barrier_or_target_is_rejected_before_git()
    {
        var f = new Fixture();
        var opts = new GitHubSandboxOneShotOptions(true, 2468,
            "gh-org", "sandbox");
        var request = new GitHubSandboxPushInput(
            f.Source, f.Bundle, f.Barrier,
            GitHubRemoteRefFenceIntegrity.Fingerprint(f.Refs.Baseline.Refs),
            f.Refs.Baseline);
        GitHubSandboxPushIntegrity.Verify(request, opts);
        Assert.Throws<GitHubSandboxOneShotException>(
            () => GitHubSandboxPushIntegrity.Verify(
                request with { Barrier = f.Barrier with { CanExecute = true } },
                opts));
        Assert.Throws<GitHubSandboxOneShotException>(
            () => GitHubSandboxPushIntegrity.Verify(
                request with { Source = f.Source with {
                    Packet = f.Source.Packet with { RemoteWritesPerformed = true }
                } }, opts));
        Assert.Throws<GitHubSandboxOneShotException>(
            () => GitHubSandboxPushIntegrity.Verify(
                request, opts with { AllowedRepositoryId = 9999 }));
        Assert.Throws<GitHubSandboxOneShotException>(
            () => GitHubSandboxPushIntegrity.Verify(
                request, opts with { Enabled = false }));
    }
}
