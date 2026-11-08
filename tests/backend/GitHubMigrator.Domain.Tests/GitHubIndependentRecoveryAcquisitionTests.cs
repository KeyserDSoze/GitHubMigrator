using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubIndependentRecoveryAcquisitionTests
{
    private static string Sha(char c) => new(c, 64);
    private static string Oid(char c) => new(c, 40);

    private sealed class Store(GitHubRemoteTypedRecoveryBaselineReceipt original)
        : IGitHubRemoteTypedRecoveryBaselineJournal
    {
        public int Reads;
        public bool Missing;
        public Task<GitHubRemoteTypedRecoveryBaselineReceipt> CaptureAsync(
            Guid confirmationId, Guid intentId, Guid preparationId,
            Guid planId, Guid scopeId, Guid independentOperator,
            GitHubCreateOnlyCandidatePacket source,
            GitHubRefObservation latestBaseline, CancellationToken ct) =>
            throw new InvalidOperationException("Recovery cannot capture F086");
        public Task<GitHubRemoteTypedRecoveryBaselineReceipt?> ReadAfterBarrierAsync(
            Guid confirmationId, Guid intentId, Guid preparationId,
            Guid planId, Guid scopeId, CancellationToken ct)
        {
            Reads++;
            Assert.Equal(original.ConfirmationId, confirmationId);
            Assert.Equal(original.IntentId, intentId);
            Assert.Equal(original.PreparationId, preparationId);
            Assert.Equal(original.PlanId, planId);
            Assert.Equal(original.ScopeId, scopeId);
            return Task.FromResult<GitHubRemoteTypedRecoveryBaselineReceipt?>(
                Missing ? null : original);
        }
    }

    private sealed class Secrets : IConnectionSecretStore
    {
        public int Reads;
        public Task<string?> ReadAsync(string key, CancellationToken ct)
        {
            Reads++;
            Assert.Equal("sandbox/app-private-key", key);
            return Task.FromResult<string?>("private-key-held-only-in-memory");
        }
        public Task SaveAsync(string key, string secret, CancellationToken ct)
            => throw new InvalidOperationException();
        public Task DeleteAsync(string key, CancellationToken ct)
            => throw new InvalidOperationException();
    }

    private sealed class ReadToken : IGitHubRecoveryReadOnlyTokenScope
    {
        public int Minted;
        public Task<GitHubIndependentRecoveryResult> WithReadOnlyTokenAsync(
            long appId, long installationId, string privateKeyPem,
            string organization, string repository, long repositoryId,
            Func<string, DateTimeOffset, CancellationToken,
                Task<GitHubIndependentRecoveryResult>> action,
            CancellationToken ct)
        {
            Minted++;
            Assert.Equal(1234, appId);
            Assert.Equal(88, installationId);
            Assert.Equal(2468, repositoryId);
            Assert.Equal("gh-org", organization);
            Assert.Equal("sandbox", repository);
            Assert.Equal("private-key-held-only-in-memory", privateKeyPem);
            return action("test-contents-read-token-ephemeral",
                DateTimeOffset.UtcNow.AddMinutes(35), ct);
        }
    }

    private sealed class RemoteTips(GitHubRemoteTipSnapshot snapshot)
        : IGitHubRemoteTipObjectReader
    {
        public int Reads;
        public GitHubRemoteTipSnapshot Value = snapshot;
        public Task<GitHubRemoteTipSnapshot> ReadAsync(
            string organization, string repository, string token,
            IReadOnlyList<GitMirrorRef> createCandidates,
            CancellationToken ct)
        {
            Reads++;
            Assert.Equal("gh-org", organization);
            Assert.Equal("sandbox", repository);
            Assert.Equal("test-contents-read-token-ephemeral", token);
            Assert.Single(createCandidates);
            Assert.Equal("refs/heads/new-feature", createCandidates[0].Name);
            return Task.FromResult(Value);
        }
    }

    private sealed class Journal : IGitHubRemoteReadbackJournal,
        IGitHubRemoteRecoveredReadbackJournal
    {
        public int Writes;
        public GitHubRemoteReadbackStatus? Value;
        public Task<GitHubRemoteReadbackStatus> RecordAsync(
            GitHubRemoteReadbackEvidence evidence, CancellationToken ct) =>
            throw new InvalidOperationException("Legacy F054 writer forbidden");
        public Task<GitHubRemoteReadbackStatus> RecordRecoveredAsync(
            GitHubRemoteTypedRecoveryBaselineReceipt typed,
            GitHubRemoteReadbackEvidence evidence, CancellationToken ct)
        {
            Writes++;
            Assert.Equal(typed.GrantId, evidence.GrantId);
            Assert.Equal(typed.Baseline.Refs, evidence.Baseline);
            Assert.Equal(typed.CreateCandidates, evidence.CreateCandidates);
            var decision = GitHubRemoteReadbackIntegrity.Verify(
                evidence, DateTimeOffset.UtcNow);
            Value = new(Guid.NewGuid(), evidence.GrantId,
                evidence.RepositoryId, decision.State,
                decision.BaselineSha256, decision.CandidateSetSha256,
                decision.RefSetSha256, decision.TipEvidenceSha256,
                decision.AssessmentSha256, decision.JournalEvidenceSha256,
                evidence.ObserverId, decision.ObservedAtUtc,
                DateTimeOffset.UtcNow, false, false, false);
            return Task.FromResult(Value);
        }
        public Task<GitHubRemoteReadbackStatus?> GetAsync(
            Guid grantId, long repositoryId, CancellationToken ct) =>
            Task.FromResult(Value);
        public Task<IReadOnlyList<GitHubRemoteReadbackEvent>> EventsAsync(
            Guid grantId, long repositoryId, CancellationToken ct) =>
            throw new InvalidOperationException();
    }

    private sealed class Closures : IGitHubRemoteObjectReconciliationJournal,
        IGitHubTrustedRemoteReadExecutor
    {
        public int Executions;
        public GitHubRemoteObjectReconciliationStatus? Status;
        public GitHubRemoteReadbackStatus? Tips;
        public Task<GitHubRemoteObjectReconciliationStatus?> GetAsync(
            Guid hazardId, long repositoryId, CancellationToken ct) =>
            Task.FromResult(Status);
        public Task<IReadOnlyList<GitHubRemoteObjectReconciliationEvent>> EventsAsync(
            Guid hazardId, long repositoryId, CancellationToken ct) =>
            throw new InvalidOperationException();
        public Task<GitHubRemoteObjectReconciliationStatus> RecordAsync(
            GitHubRemoteObjectReconciliationEvidence evidence,
            CancellationToken ct) =>
            throw new InvalidOperationException("Must execute trusted F059");
        public Task<GitHubRemoteObjectReconciliationStatus> ExecuteAsync(
            GitHubTrustedRemoteReadCommand cmd, string token,
            CancellationToken ct)
        {
            Executions++;
            Assert.NotNull(Tips);
            Assert.Equal(Tips.Id, cmd.ReadbackId);
            Assert.NotEqual(Tips.ObservedBy, cmd.ObserverId);
            Assert.Equal("test-contents-read-token-ephemeral", token);
            Assert.True(cmd.TokenExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2));
            Assert.Single(cmd.CreateCandidates);
            var original = new GitHubRemoteObjectReconciliationStatus(
                Guid.NewGuid(), cmd.GrantId, cmd.ReadbackId,
                cmd.HazardId, cmd.RepositoryId, Guid.NewGuid(),
                GitHubRemoteObjectReconciliationStates.FullCopyEvidenceOnly,
                Tips.BaselineSha256, Tips.CandidateSetSha256,
                Tips.RefSetSha256, Sha('a'), Sha('b'), Sha('c'), "",
                cmd.ObserverId, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, false, false, false, false);
            Status = original with {
                EvidenceSha256 =
                    GitHubRemotePostSendRecoveryIntegrity.FullClosureDigest(
                        original)
            };
            return Task.FromResult(Status);
        }
    }

    private sealed class Fixture
    {
        public readonly GitHubRemoteTypedRecoveryBaselineReceipt Baseline;
        public readonly Store Typed;
        public readonly Secrets Secret = new();
        public readonly ReadToken Token = new();
        public readonly RemoteTips Tips;
        public readonly Journal Readback = new();
        public readonly Closures Closure = new();

        public Fixture()
        {
            var now = DateTimeOffset.UtcNow;
            var historical = GitHubRefObservationIntegrity.Seal(
                "gh-org", "sandbox", now,
                [new("refs/heads/main", Oid('a'), "commit"),
                 new("refs/tags/v1", Oid('d'), "tag")]);
            GitMirrorRef[] creates = [
                new("refs/heads/new-feature", Oid('b'))
            ];
            var raw = new GitHubRemoteTypedRecoveryBaselineReceipt(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                2468, "gh-org", "sandbox", Guid.NewGuid(),
                Sha('1'), GitImportRehearsalIntegrity.SealRefs(
                    "f044-create-only-candidate-refs-v1", creates),
                GitHubRemoteRefFenceIntegrity.Fingerprint(historical.Refs),
                historical, creates, now,
                GitHubRemoteTypedRecoveryBaselineIntegrity.Captured, "",
                false, false, false);
            Baseline = raw with {
                EvidenceSha256 =
                    GitHubRemoteTypedRecoveryBaselineIntegrity.Digest(raw),
                PossibleSendRecordedAtUtc = now.AddTicks(10)
            };
            Typed = new(Baseline);
            // The reconstructed F-082 marker must precede the independent
            // provider observation, including GitHub's millisecond clock.
            var postSend = now.AddMilliseconds(50);
            var remote = GitHubRefObservationIntegrity.Seal(
                "gh-org", "sandbox", postSend,
                [new("refs/heads/main", Oid('a'), "commit"),
                 new("refs/heads/new-feature", Oid('b'), "commit"),
                 new("refs/tags/v1", Oid('d'), "tag")]);
            var tips = GitHubRemoteTipIntegrity.Seal(
                remote, creates, postSend,
                [new("refs/heads/new-feature", Oid('b'), "commit",
                    Oid('b'), Oid('c'), 0)]);
            Tips = new(new(remote, tips));
        }

        public GitHubIndependentRecoveryAcquisition Service(bool enabled = true) =>
            new(new(enabled, 2468, "gh-org/sandbox"),
                new(true, 1234, 88,
                    "sandbox/app-private-key", "gh-org/sandbox"),
                Secret, Typed, Token, Tips, Readback,
                Readback, Closure, Closure);

        public Task<GitHubIndependentRecoveryResult> ObserveAsync(
            Guid? actor = null, GitHubIndependentRecoveryAcquisition? svc = null) =>
            (svc ?? Service()).AcquireTipsAsync(
                Baseline.ConfirmationId, Baseline.IntentId,
                Baseline.PreparationId, Baseline.PlanId, Baseline.ScopeId,
                actor ?? Guid.NewGuid(), default);

        public Task<GitHubIndependentRecoveryResult> VerifyAsync(
            Guid? actor = null, GitHubIndependentRecoveryAcquisition? svc = null) =>
            (svc ?? Service()).AcquireClosureAsync(
                Baseline.ConfirmationId, Baseline.IntentId,
                Baseline.PreparationId, Baseline.PlanId, Baseline.ScopeId,
                actor ?? Guid.NewGuid(), default);
    }

    [Fact]
    public async Task F087_disabled_or_uncommitted_marker_never_reads_secret_or_GitHub()
    {
        var f = new Fixture();
        var disabled = await Assert.ThrowsAsync<GitHubIndependentRecoveryException>(
            () => f.ObserveAsync(svc: f.Service(false)));
        Assert.Equal("IndependentRecoveryDisabled", disabled.Code);
        Assert.Equal(0, f.Typed.Reads);
        f.Typed.Missing = true;
        var absent = await Assert.ThrowsAsync<GitHubIndependentRecoveryException>(
            () => f.ObserveAsync());
        Assert.Equal("IndependentRecoveryNoCommittedBarrier", absent.Code);
        Assert.Equal(0, f.Secret.Reads);
        Assert.Equal(0, f.Token.Minted);
        Assert.Equal(0, f.Tips.Reads);
    }

    [Fact]
    public async Task F087_readonly_tips_then_full_closure_reuses_distinct_actors()
    {
        var f = new Fixture();
        var observer1 = Guid.NewGuid();
        var r = await f.ObserveAsync(observer1);
        Assert.Equal(GitHubIndependentRecoveryStates.ReadbackCaptured, r.State);
        Assert.False(r.CanPush);
        Assert.False(r.CanRetry);
        Assert.False(r.MigrationComplete);
        Assert.True(r.ReadOnlyAcquisition);
        Assert.Equal(1, f.Tips.Reads);
        Assert.Equal(1, f.Readback.Writes);
        f.Closure.Tips = f.Readback.Value;

        var r2 = await f.VerifyAsync(Guid.NewGuid());
        Assert.Equal(GitHubIndependentRecoveryStates.ClosureCaptured, r2.State);
        Assert.NotNull(r2.ReconciliationId);
        Assert.Equal(1, f.Closure.Executions);
        Assert.Equal(2, f.Token.Minted);
        Assert.Equal(2, f.Secret.Reads);
        Assert.False(r2.CanRetry);
        Assert.False(r2.CanPush);
        Assert.False(r2.MigrationComplete);

        var replayTips = await f.ObserveAsync();
        Assert.Equal(r.ReadbackId, replayTips.ReadbackId);
        var replayClosure = await f.VerifyAsync();
        Assert.Equal(GitHubIndependentRecoveryStates.NoNewEvidence,
            replayClosure.State);
        Assert.Equal(r2.ReconciliationId, replayClosure.ReconciliationId);
        Assert.Equal(2, f.Token.Minted);
        Assert.Equal(1, f.Readback.Writes);
        Assert.Equal(1, f.Closure.Executions);
        Assert.DoesNotContain("test-contents-read-token",
            System.Text.Json.JsonSerializer.Serialize(replayClosure));
    }

    [Fact]
    public async Task F087_same_observer_cannot_certify_its_own_tips_or_sender()
    {
        var f = new Fixture();
        var error = await Assert.ThrowsAsync<GitHubIndependentRecoveryException>(
            () => f.ObserveAsync(f.Baseline.CapturedBy));
        Assert.Equal("IndependentRecoveryWrongTargetOrActor", error.Code);
        Assert.Equal(0, f.Token.Minted);

        var first = Guid.NewGuid();
        await f.ObserveAsync(first);
        error = await Assert.ThrowsAsync<GitHubIndependentRecoveryException>(
            () => f.VerifyAsync(first));
        Assert.Equal("IndependentRecoveryExactTipsRequired", error.Code);
        Assert.Equal(1, f.Token.Minted);
    }

    [Fact]
    public async Task F087_prebarrier_readback_cannot_be_replayed_as_postsend_evidence()
    {
        var f = new Fixture();
        var result = await f.ObserveAsync();
        Assert.NotNull(f.Readback.Value);
        f.Readback.Value = f.Readback.Value with {
            ObservedAtUtc =
                f.Baseline.PossibleSendRecordedAtUtc!.Value.AddSeconds(-2)
        };
        var ex = await Assert.ThrowsAsync<GitHubIndependentRecoveryException>(
            () => f.ObserveAsync());
        Assert.Equal("IndependentRecoveryOldReadback", ex.Code);
        ex = await Assert.ThrowsAsync<GitHubIndependentRecoveryException>(
            () => f.VerifyAsync());
        Assert.Equal("IndependentRecoveryExactTipsRequired", ex.Code);
        Assert.Equal(1, f.Token.Minted);
        Assert.Equal(0, f.Closure.Executions);
    }

    [Fact]
    public async Task F087_mismatch_stays_operator_review_and_never_fetches_objects()
    {
        var f = new Fixture();
        var previous = f.Tips.Value;
        var remote = GitHubRefObservationIntegrity.Seal("gh-org", "sandbox",
            DateTimeOffset.UtcNow,
            [.. previous.RemoteRefs.Refs,
                new("refs/heads/unexpected", Oid('c'), "commit")]);
        f.Tips.Value = new(remote,
            GitHubRemoteTipIntegrity.Seal(remote,
                f.Baseline.CreateCandidates, remote.ObservedAtUtc,
                previous.TipReadback.Tips));
        var r = await f.ObserveAsync();
        Assert.Equal(GitHubIndependentRecoveryStates.OperatorReview, r.State);
        Assert.False(r.CanRetry);
        var error = await Assert.ThrowsAsync<GitHubIndependentRecoveryException>(
            () => f.VerifyAsync());
        Assert.Equal("IndependentRecoveryExactTipsRequired", error.Code);
        Assert.Equal(0, f.Closure.Executions);
        Assert.Equal(1, f.Token.Minted);
    }
}
