using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteTrustedDispatchReadOnlyOrchestratorTests
{
    private static string Sha(char c) => new(c, 64);
    private static string Oid(char c) => new(c, 40);

    private sealed class PacketSource(
        GitHubCreateOnlyCandidatePacket initial)
        : IGitHubCreateOnlyPacketEvidenceReader
    {
        public int Calls { get; private set; }
        public GitHubCreateOnlyCandidatePacket? Next { get; set; }
        public Task<GitHubCreateOnlyCandidatePacket> PreviewWithCandidatesAsync(
            Guid prep, Guid plan, Guid scope, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(initial.Packet.PreparationId, prep);
            Assert.Equal(initial.Packet.PlanId, plan);
            Assert.Equal(initial.Packet.ScopeId, scope);
            return Task.FromResult(Calls == 2 && Next is not null
                ? Next : initial);
        }
    }

    private sealed class Secrets : IConnectionSecretStore
    {
        public int Reads { get; private set; }
        public string? Value { get; set; } = "FAKE_RSA_PRIVATE_KEY_ONLY_IN_MEMORY";
        public Task<string?> ReadAsync(string reference, CancellationToken ct)
        {
            Assert.Equal("secrets/f076-test-app", reference);
            Reads++;
            return Task.FromResult(Value);
        }
        public Task SaveAsync(string reference, string secret,
            CancellationToken ct) =>
            throw new InvalidOperationException("No secret writes permitted");
        public Task DeleteAsync(string reference, CancellationToken ct) =>
            throw new InvalidOperationException("No secret deletion permitted");
    }

    private sealed class Remote(
        GitHubRemoteAppPreflightObservation stable)
        : IGitHubRemoteAppPreflightReader
    {
        public int Calls { get; private set; }
        public GitHubRemoteAppPreflightObservation Response { get; set; } = stable;
        public Task<GitHubRemoteAppPreflightObservation> InspectAsync(
            long appId, long installationId, string key,
            GitHubCreateOnlyCandidatePacket packet, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(1234L, appId);
            Assert.Equal(77L, installationId);
            Assert.Equal("FAKE_RSA_PRIVATE_KEY_ONLY_IN_MEMORY", key);
            Assert.Equal(2468L, packet.Packet.RepositoryId);
            Assert.Equal("gh-org", packet.Packet.Organization);
            Assert.Equal("sandbox", packet.Packet.Repository);
            Assert.Equal("refs/heads/new", Assert.Single(packet.Candidates).Name);
            return Task.FromResult(Response);
        }
    }

    private sealed class Authority : IGitHubRemoteDispatchAuthorityVerifier
    {
        public int Calls { get; private set; }
        public GitHubRemoteAppPreflightReceipt? SubmittedPreflight { get; private set; }
        public GitHubRefObservation? SubmittedRefs { get; private set; }
        public Task<GitHubRemoteCreateOnlyDispatchReview> VerifyAsync(
            GitHubCreateOnlyCandidatePacket packet,
            GitHubRemoteAppPreflightReceipt preflight,
            GitHubRefObservation latest, Guid hazard,
            Guid actor, CancellationToken ct)
        {
            Calls++;
            SubmittedPreflight = preflight;
            SubmittedRefs = latest;
            Assert.NotEqual(Guid.Empty, hazard);
            Assert.NotEqual(Guid.Empty, actor);
            Assert.Equal("refs/heads/new", Assert.Single(packet.Candidates).Name);
            Assert.Equal(preflight.ObservedBaselineSha256,
                GitHubRemoteRefFenceIntegrity.Fingerprint(latest.Refs));
            return Task.FromResult(new GitHubRemoteCreateOnlyDispatchReview(
                hazard, Guid.NewGuid(), preflight.RequestId,
                preflight.Generation, packet.Packet.RepositoryId,
                packet.Packet.EvidenceSha256,
                packet.Packet.CandidateSetSha256,
                preflight.OriginalPolicySha256,
                preflight.CurrentPolicySha256,
                preflight.EvidenceSha256,
                preflight.ObservedBaselineSha256,
                latest.EvidenceSha256,
                1, 2, latest.ObservedAtUtc,
                latest.ObservedAtUtc.AddSeconds(5), Sha('d'),
                GitHubRemoteCreateOnlyDispatchIntegrity.State,
                false, false, false, false));
        }
    }

    private sealed record Fixture(
        GitHubCreateOnlyCandidatePacket Source,
        GitHubRemoteAppPreflightReceipt Preflight,
        GitHubRemoteAppPreflightObservation Observation,
        PacketSource Packets, Remote RemoteReader, Secrets Secrets,
        Authority AuthorityReader, DateTimeOffset Now)
    {
        public GitHubRemoteTrustedDispatchReadOnlyOrchestrator Service(
            bool enabled = true, bool credentialsEnabled = true,
            long allowedRepoId = 2468L) =>
            new(Packets, RemoteReader, Secrets, AuthorityReader,
                new(enabled, allowedRepoId, "gh-org/sandbox"),
                new(credentialsEnabled, 1234, 77,
                    "secrets/f076-test-app", "gh-org/sandbox"),
                TimeProvider.System);

        public Task<GitHubRemoteCreateOnlyDispatchReview> RunAsync(
            GitHubRemoteAppPreflightReceipt? pinned = null,
            GitHubRemoteTrustedDispatchReadOnlyOrchestrator? service = null) =>
            (service ?? Service()).ReviewAsync(
                Source.Packet.PreparationId, Source.Packet.PlanId,
                Source.Packet.ScopeId, Guid.NewGuid(), Guid.NewGuid(),
                pinned ?? Preflight, CancellationToken.None);
    }

    private static Fixture Valid()
    {
        var now = DateTimeOffset.UtcNow;
        var candidate = new GitMirrorRef("refs/heads/new", Oid('b'));
        var candidates = new[] { candidate };
        var packet = new GitHubCreateOnlyPacket(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), 2468, "gh-org", "sandbox",
            Sha('1'), Sha('2'), Sha('3'), Sha('4'), Sha('5'),
            Sha('6'), Sha('7'), Sha('8'), Sha('a'),
            GitImportRehearsalIntegrity.SealRefs(
                "f044-create-only-candidate-refs-v1", candidates),
            Sha('9'), 1, 1, 1,
            GitHubCreateOnlyPacketIntegrity.State,
            false, false, false, false);
        var source = new GitHubCreateOnlyCandidatePacket(packet, candidates);
        var refs = GitHubRefObservationIntegrity.Seal(
            "gh-org", "sandbox", now,
            [new("refs/heads/main", Oid('a'), "commit"),
             new("refs/tags/v1", Oid('c'), "tag")]);
        var baseline = GitHubRemoteRefFenceIntegrity.Fingerprint(refs.Refs);
        var receipt = new GitHubRemoteAppPreflightReceipt(
            Guid.NewGuid(), 2, 2468, packet.EvidenceSha256,
            packet.CandidateSetSha256, packet.PolicySha256,
            Sha('c'), baseline, baseline, 2, 1,
            now.AddSeconds(-1), now.AddMinutes(55), Sha('d'),
            GitHubRemoteAppPreflightIntegrity.VerifiedState,
            false, false, false);
        var policy = new GitHubCandidatePolicyObservation(
            [new(GitHubCandidatePolicyIntegrity.RefNameDigest(candidate.Name),
                "Branch",
                GitHubCandidatePolicyStates.NoActiveRulesReported,
                0, "Observed")],
            now, "Observed");
        var live = new GitHubRemoteAppPreflightObservation(
            77, 2468, refs, policy, now.AddMinutes(55));
        return new(source, receipt, live, new(source),
            new(live), new(), new(), now);
    }

    [Fact]
    public async Task F076_rebuilds_disk_and_DB_source_twice_and_checks_authenticated_GET_before_F075()
    {
        var f = Valid();
        var receipt = await f.RunAsync();
        Assert.Equal(2, f.Packets.Calls);
        Assert.Equal(1, f.Secrets.Reads);
        Assert.Equal(1, f.RemoteReader.Calls);
        Assert.Equal(1, f.AuthorityReader.Calls);
        Assert.Equal(f.Preflight, f.AuthorityReader.SubmittedPreflight);
        Assert.Equal(f.Observation.TargetRefs.EvidenceSha256,
            f.AuthorityReader.SubmittedRefs!.EvidenceSha256);
        Assert.Equal(f.Observation.TargetRefs.Refs,
            f.AuthorityReader.SubmittedRefs.Refs);
        Assert.Equal(GitHubRemoteCreateOnlyDispatchIntegrity.State,
            receipt.State);
        Assert.False(receipt.RemoteTransportInvoked);
        Assert.False(receipt.RemoteWritesAuthorized);
        Assert.False(receipt.MayAutomaticallyRetry);
        Assert.False(receipt.MigrationComplete);
        var serialized = System.Text.Json.JsonSerializer.Serialize(receipt);
        Assert.DoesNotContain("refs/", serialized);
        Assert.DoesNotContain("PRIVATE KEY", serialized);
        Assert.DoesNotContain("FAKE_RSA", serialized);
    }

    [Theory]
    [InlineData("wrong-packet")]
    [InlineData("wrong-repository")]
    [InlineData("wrong-policy")]
    [InlineData("bad-evidence")]
    [InlineData("unverified")]
    [InlineData("disabled")]
    [InlineData("credentials-disabled")]
    [InlineData("wrong-allowlist")]
    public async Task F076_refuses_incoherent_authority_before_even_reading_secret(
        string mutation)
    {
        var f = Valid();
        var pinned = f.Preflight;
        switch (mutation)
        {
            case "wrong-packet":
                pinned = pinned with { PacketSha256 = Sha('f') }; break;
            case "wrong-repository":
                pinned = pinned with { RepositoryId = 4444 }; break;
            case "wrong-policy":
                pinned = pinned with { OriginalPolicySha256 = Sha('f') }; break;
            case "bad-evidence":
                pinned = pinned with { EvidenceSha256 = "invalid" }; break;
            case "unverified":
                pinned = pinned with { State = "Unverified" }; break;
        }
        var service = f.Service(enabled: mutation != "disabled",
            credentialsEnabled: mutation != "credentials-disabled",
            allowedRepoId: mutation == "wrong-allowlist" ? 9999L : 2468L);
        var ex = await Assert.ThrowsAsync<GitHubRemoteCreateOnlyDispatchException>(
            () => f.RunAsync(pinned, service));
        Assert.Equal("RemoteDispatchTrustedEvidenceInvalid", ex.Code);
        Assert.Equal(0, f.Secrets.Reads);
        Assert.Equal(0, f.RemoteReader.Calls);
        Assert.Equal(0, f.AuthorityReader.Calls);
    }

    [Theory]
    [InlineData("repo-id")]
    [InlineData("installation")]
    [InlineData("head")]
    [InlineData("tag")]
    [InlineData("candidate-occupied")]
    [InlineData("extra-target-ref")]
    [InlineData("wrong-account")]
    [InlineData("bogus-ref-proof")]
    [InlineData("active-rules")]
    [InlineData("unknown-rules")]
    [InlineData("token-expiring")]
    public async Task F076_attested_GitHub_response_drift_never_reaches_DB_review(
        string mutation)
    {
        var f = Valid();
        var x = f.Observation;
        if (mutation == "repo-id")
            x = x with { RepositoryId = 3000 };
        if (mutation == "installation")
            x = x with { InstallationId = 88 };
        if (mutation == "wrong-account")
            x = x with {
                TargetRefs = GitHubRefObservationIntegrity.Seal(
                    "attacker", "sandbox", f.Now, x.TargetRefs.Refs)
            };
        if (mutation == "bogus-ref-proof")
            x = x with {
                TargetRefs = x.TargetRefs with { EvidenceSha256 = Sha('f') }
            };
        if (mutation == "token-expiring")
            x = x with {
                TokenExpiresAtUtc = f.Now.AddMinutes(1)
            };
        if (mutation == "head" || mutation == "tag" ||
            mutation == "candidate-occupied" ||
            mutation == "extra-target-ref")
        {
            var refs = x.TargetRefs.Refs.ToArray();
            if (mutation == "head")
                refs[0] = refs[0] with { ObjectId = Oid('f') };
            if (mutation == "tag")
                refs[1] = refs[1] with { ObjectId = Oid('f') };
            if (mutation == "candidate-occupied")
                refs = [..refs, new GitHubObservedRef(
                    "refs/heads/new", Oid('b'), "commit")];
            if (mutation == "extra-target-ref")
                refs = [..refs, new GitHubObservedRef(
                    "refs/heads/external", Oid('e'), "commit")];
            x = x with {
                TargetRefs = GitHubRefObservationIntegrity.Seal(
                    "gh-org", "sandbox", f.Now, refs)
            };
        }
        if (mutation is "active-rules" or "unknown-rules")
            x = x with {
                CandidatePolicies = new(
                    [new(GitHubCandidatePolicyIntegrity.RefNameDigest(
                        "refs/heads/new"), "Branch",
                        mutation == "active-rules"
                            ? GitHubCandidatePolicyStates.RulesRequireReview
                            : GitHubCandidatePolicyStates.PolicyUnverified,
                        mutation == "active-rules" ? 1 : 0,
                        "Observed")],
                    f.Now, "Observed")
            };
        f.RemoteReader.Response = x;
        var ex = await Assert.ThrowsAsync<GitHubRemoteCreateOnlyDispatchException>(
            () => f.RunAsync());
        Assert.Equal(mutation is "repo-id" or "installation" or
                "token-expiring" ? "RemoteDispatchTrustedEvidenceInvalid" :
                "RemoteDispatchLiveTargetChanged", ex.Code);
        Assert.Equal(1, f.RemoteReader.Calls);
        Assert.Equal(0, f.AuthorityReader.Calls);
    }

    [Theory]
    [InlineData("packet")]
    [InlineData("candidate")]
    public async Task F076_changed_F044_source_after_provider_GET_never_reaches_DB_review(
        string mutation)
    {
        var f = Valid();
        f.Packets.Next = mutation == "packet"
            ? f.Source with { Packet = f.Source.Packet with {
                EvidenceSha256 = Sha('f') } }
            : f.Source with {
                Candidates = [new GitMirrorRef("refs/heads/unexpected", Oid('d'))]
            };
        var ex = await Assert.ThrowsAsync<GitHubRemoteCreateOnlyDispatchException>(
            () => f.RunAsync());
        Assert.Equal("RemoteDispatchSourceChangedDuringRead", ex.Code);
        Assert.Equal(2, f.Packets.Calls);
        Assert.Equal(1, f.RemoteReader.Calls);
        Assert.Equal(0, f.AuthorityReader.Calls);
    }

    [Fact]
    public async Task F076_secret_unavailable_fails_closed_without_token_mint()
    {
        var f = Valid();
        f.Secrets.Value = null;
        var ex = await Assert.ThrowsAsync<GitHubRemoteCreateOnlyDispatchException>(
            () => f.RunAsync());
        Assert.Equal("RemoteDispatchTrustedEvidenceInvalid", ex.Code);
        Assert.Equal(1, f.Secrets.Reads);
        Assert.Equal(0, f.RemoteReader.Calls);
        Assert.Equal(0, f.AuthorityReader.Calls);
    }

    [Fact]
    public async Task F076_postgresql_veto_propagates_and_receipt_is_never_replaced()
    {
        var f = Valid();
        var rejected = new GitHubRemoteCreateOnlyDispatchException(
            "RemoteDispatchPersistedAuthorityChanged");
        var authority = new RejectingAuthority(rejected);
        var service = new GitHubRemoteTrustedDispatchReadOnlyOrchestrator(
            f.Packets, f.RemoteReader, f.Secrets, authority,
            new(true, 2468L, "gh-org/sandbox"),
            new(true, 1234, 77, "secrets/f076-test-app",
                "gh-org/sandbox"), TimeProvider.System);
        var ex = await Assert.ThrowsAsync<GitHubRemoteCreateOnlyDispatchException>(
            () => f.RunAsync(service: service));
        Assert.Same(rejected, ex);
        Assert.Equal(1, authority.Calls);
    }

    [Fact]
    public async Task F077_hazard_bound_F052_digest_replaces_body_supplied_prior_receipt()
    {
        var f = Valid();
        var hazard = Guid.NewGuid();
        var journal = new EvidenceJournal { Value = f.Preflight };
        var service = new GitHubRemoteTrustedDispatchReadOnlyOrchestrator(
            f.Packets, f.RemoteReader, f.Secrets, f.AuthorityReader,
            new(true, 2468, "gh-org/sandbox"),
            new(true, 1234, 77, "secrets/f076-test-app",
                "gh-org/sandbox"), TimeProvider.System, journal);

        var receipt = await service.ReviewFromJournalAsync(
            f.Source.Packet.PreparationId, f.Source.Packet.PlanId,
            f.Source.Packet.ScopeId, hazard, Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(1, journal.Calls);
        Assert.Equal(hazard, journal.LastHazardId);
        Assert.Equal(1, f.AuthorityReader.Calls);
        Assert.Equal(f.Preflight.EvidenceSha256,
            f.AuthorityReader.SubmittedPreflight!.EvidenceSha256);
        Assert.Equal(2, f.Packets.Calls);
        Assert.False(receipt.RemoteWritesAuthorized);
        Assert.False(receipt.RemoteTransportInvoked);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-repository")]
    [InlineData("wrong-packet")]
    public async Task F077_missing_or_mismatched_history_denies_before_secret_read(
        string mutation)
    {
        var f = Valid();
        var journal = new EvidenceJournal { Value = mutation switch
        {
            "missing" => null,
            "wrong-repository" => f.Preflight with {
                RepositoryId = 9999
            },
            "wrong-packet" => f.Preflight with {
                PacketSha256 = Sha('e')
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        }};
        var service = new GitHubRemoteTrustedDispatchReadOnlyOrchestrator(
            f.Packets, f.RemoteReader, f.Secrets, f.AuthorityReader,
            new(true, 2468, "gh-org/sandbox"),
            new(true, 1234, 77, "secrets/f076-test-app",
                "gh-org/sandbox"), TimeProvider.System, journal);

        var ex = await Assert.ThrowsAsync<
            GitHubRemoteCreateOnlyDispatchException>(() =>
            service.ReviewFromJournalAsync(
                f.Source.Packet.PreparationId, f.Source.Packet.PlanId,
                f.Source.Packet.ScopeId, Guid.NewGuid(), Guid.NewGuid(),
                CancellationToken.None));
        Assert.Equal(mutation == "missing"
            ? "RemoteDispatchPreflightJournalMissing"
            : "RemoteDispatchTrustedEvidenceInvalid", ex.Code);
        Assert.Equal(1, journal.Calls);
        Assert.Equal(0, f.Secrets.Reads);
        Assert.Equal(0, f.RemoteReader.Calls);
        Assert.Equal(0, f.AuthorityReader.Calls);
    }

    [Fact]
    public async Task F078_trusted_hazard_entrypoint_persists_only_after_all_readonly_checks()
    {
        var f = Valid();
        var historic = new EvidenceJournal { Value = f.Preflight };
        var sessions = new SessionRecorder();
        var service = new GitHubRemoteTrustedDispatchReadOnlyOrchestrator(
            f.Packets, f.RemoteReader, f.Secrets, f.AuthorityReader,
            new(true, 2468, "gh-org/sandbox"),
            new(true, 1234, 77, "secrets/f076-test-app",
                "gh-org/sandbox"), TimeProvider.System, historic, sessions);

        var actor = Guid.NewGuid();
        var receipt = await service.ReviewAndRecordSessionFromJournalAsync(
            f.Source.Packet.PreparationId, f.Source.Packet.PlanId,
            f.Source.Packet.ScopeId, Guid.NewGuid(), actor, CancellationToken.None);

        Assert.Equal(1, historic.Calls);
        Assert.Equal(2, f.Packets.Calls);
        Assert.Equal(1, f.RemoteReader.Calls);
        Assert.Equal(1, f.AuthorityReader.Calls);
        Assert.Equal(1, sessions.Calls);
        Assert.Equal(actor, receipt.ObservedBy);
        Assert.Equal(
            GitHubRemoteSandboxReviewSessionIntegrity.Recorded, receipt.State);
        Assert.False(receipt.TransportInvoked);
        Assert.False(receipt.RemoteWritesAuthorized);
        Assert.False(receipt.MayAutomaticallyRetry);
        Assert.False(receipt.MigrationComplete);
        GitHubRemoteSandboxReviewSessionIntegrity.Verify(receipt);
    }

    [Fact]
    public async Task F078_missing_session_journal_refuses_before_secret_or_GitHub_io()
    {
        var f = Valid();
        var service = new GitHubRemoteTrustedDispatchReadOnlyOrchestrator(
            f.Packets, f.RemoteReader, f.Secrets, f.AuthorityReader,
            new(true, 2468, "gh-org/sandbox"),
            new(true, 1234, 77, "secrets/f076-test-app",
                "gh-org/sandbox"), TimeProvider.System,
            new EvidenceJournal { Value = f.Preflight });

        var exception = await Assert.ThrowsAsync<
            GitHubRemoteSandboxReviewSessionException>(() =>
            service.ReviewAndRecordSessionFromJournalAsync(
                f.Source.Packet.PreparationId, f.Source.Packet.PlanId,
                f.Source.Packet.ScopeId, Guid.NewGuid(), Guid.NewGuid(),
                CancellationToken.None));
        Assert.Equal("SandboxReviewSessionJournalUnavailable", exception.Code);
        Assert.Equal(0, f.Packets.Calls);
        Assert.Equal(0, f.Secrets.Reads);
        Assert.Equal(0, f.RemoteReader.Calls);
    }

    private sealed class SessionRecorder : IGitHubRemoteSandboxReviewSessionRecorder
    {
        public int Calls { get; private set; }

        public Task<GitHubRemoteSandboxReviewSessionReceipt> RecordAsync(
            GitHubRemoteCreateOnlyDispatchReview reviewed,
            Guid independentActor, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(GitHubRemoteCreateOnlyDispatchIntegrity.State,
                reviewed.State);
            Assert.False(reviewed.RemoteWritesAuthorized);
            Assert.False(reviewed.MayAutomaticallyRetry);
            var result = new GitHubRemoteSandboxReviewSessionReceipt(
                Guid.NewGuid(), reviewed.HazardId, reviewed.GrantId,
                reviewed.FenceRequestId, reviewed.FenceGeneration,
                reviewed.RepositoryId, independentActor,
                reviewed.PacketSha256, reviewed.CandidateSetSha256,
                reviewed.PreflightSha256, reviewed.BaselineSha256,
                reviewed.EvidenceSha256,
                reviewed.LastRefObservationSha256,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(4),
                "", GitHubRemoteSandboxReviewSessionIntegrity.Recorded,
                false, false, false, false);
            return Task.FromResult(result with {
                EvidenceSha256 =
                    GitHubRemoteSandboxReviewSessionIntegrity.Digest(result)
            });
        }
    }

    private sealed class EvidenceJournal : IGitHubRemoteAppPreflightEvidenceJournal
    {
        public GitHubRemoteAppPreflightReceipt? Value { get; set; }
        public int Calls { get; private set; }
        public Guid LastHazardId { get; private set; }

        public Task<GitHubRemoteAppPreflightReceipt> RecordAsync(
            GitHubRemoteAppPreflightReceipt receipt,
            DateTimeOffset policyObservedAtUtc, CancellationToken ct) =>
            throw new InvalidOperationException("Review cannot write a journal");

        public Task<GitHubRemoteAppPreflightReceipt?> GetAsync(
            Guid fenceRequestId, long generation, long repositoryId,
            string evidenceSha256, CancellationToken ct) =>
            throw new InvalidOperationException("No body-supplied SHA query");

        public Task<GitHubRemoteAppPreflightReceipt?> GetForHazardAsync(
            Guid hazardId, CancellationToken ct)
        {
            Calls++;
            LastHazardId = hazardId;
            return Task.FromResult(Value);
        }
    }

    private sealed class RejectingAuthority(
        GitHubRemoteCreateOnlyDispatchException refusal)
        : IGitHubRemoteDispatchAuthorityVerifier
    {
        public int Calls { get; private set; }
        public Task<GitHubRemoteCreateOnlyDispatchReview> VerifyAsync(
            GitHubCreateOnlyCandidatePacket source,
            GitHubRemoteAppPreflightReceipt preflight,
            GitHubRefObservation latest, Guid hazardId,
            Guid independentActor, CancellationToken ct)
        {
            Calls++;
            throw refusal;
        }
    }
}
