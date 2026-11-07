using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteAppPreflightIntegrityTests
{
    [Fact]
    public void Sealed_receipt_contains_only_scoped_hashes_and_no_remote_write_permission()
    {
        var fixture = Valid();
        var result = fixture.Seal();
        Assert.Equal(GitHubRemoteAppPreflightIntegrity.VerifiedState,
            result.State);
        Assert.Equal(fixture.Source.Packet.EvidenceSha256,
            result.PacketSha256);
        Assert.Equal(fixture.Source.Packet.RepositoryId, result.RepositoryId);
        Assert.Equal(2, result.MatchingBaselineRefs);
        Assert.Equal(1, result.AbsentCreateCandidates);
        Assert.Equal(64, result.EvidenceSha256.Length);
        Assert.False(result.RemoteWritesPerformed);
        Assert.False(result.ExecutionAuthorized);
        Assert.False(result.ReadyForExecution);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("refs/", json);
        Assert.DoesNotContain("ghs_", json);
        Assert.DoesNotContain("PRIVATE KEY", json);
        Assert.Equal(result.EvidenceSha256, fixture.Seal().EvidenceSha256);
    }

    [Fact]
    public void Changed_global_ref_set_or_annotated_tag_object_refuses_a_verified_receipt()
    {
        var fixture = Valid();
        var altered = GitHubRefObservationIntegrity.Seal(
            fixture.Source.Packet.Organization,
            fixture.Source.Packet.Repository, fixture.Now,
            [new("refs/heads/main", new string('a', 40), "commit"),
             new("refs/tags/v1", new string('d', 40), "tag")]);
        Assert.Equal("RemotePreflightTargetDriftBlocked",
            Assert.Throws<GitHubRemoteAppPreflightException>(() =>
                fixture.Seal(observation:
                    fixture.Observation with { TargetRefs = altered })).Code);

        var withCandidate = GitHubRefObservationIntegrity.Seal(
            fixture.Source.Packet.Organization,
            fixture.Source.Packet.Repository, fixture.Now,
            [..fixture.Observation.TargetRefs.Refs,
             new("refs/heads/feature", new string('b', 40), "commit")]);
        Assert.Equal("RemotePreflightTargetDriftBlocked",
            Assert.Throws<GitHubRemoteAppPreflightException>(() =>
                fixture.Seal(observation:
                    fixture.Observation with { TargetRefs = withCandidate })).Code);
    }

    [Fact]
    public void Active_rules_or_unverified_policy_deny_the_packet()
    {
        var fixture = Valid();
        var item = Assert.Single(fixture.Observation.CandidatePolicies.Items);
        var blocked = fixture.Observation.CandidatePolicies with {
            Items = [item with {
                Status = GitHubCandidatePolicyStates.RulesRequireReview,
                ActiveRules = 1
            }]
        };
        Assert.Equal("RemotePreflightPolicyReviewRequired",
            Assert.Throws<GitHubRemoteAppPreflightException>(() =>
                fixture.Seal(observation:
                    fixture.Observation with { CandidatePolicies = blocked })).Code);

        var unknown = fixture.Observation.CandidatePolicies with {
            RulesetCatalogStatus = "CatalogUnverified",
            Items = [item with {
                Status = GitHubCandidatePolicyStates.PolicyUnverified,
                ProviderStatus = "BranchUnverified"
            }]
        };
        Assert.Equal("RemotePreflightEvidenceInvalid",
            Assert.Throws<GitHubRemoteAppPreflightException>(() =>
                fixture.Seal(observation:
                    fixture.Observation with { CandidatePolicies = unknown })).Code);
    }

    [Fact]
    public void Expired_or_forged_F050_fence_and_wrong_installation_are_never_exec_authority()
    {
        var fixture = Valid();
        void Reject(GitHubRemoteSandboxFenceStatus writeFence,
            GitHubAppWriteScopeView? scope = null) =>
            Assert.Equal("RemotePreflightEvidenceInvalid",
                Assert.Throws<GitHubRemoteAppPreflightException>(() =>
                    fixture.Seal(fence: writeFence,
                        scope: scope ?? fixture.Scope)).Code);
        Reject(fixture.Fence with {
            State = GitHubRemoteSandboxFenceStates.RecoveryRequired
        });
        Reject(fixture.Fence with {
            DeadlineUtc = fixture.Now.AddSeconds(-1)
        });
        Reject(fixture.Fence with {
            PacketSha256 = Sha('f')
        });
        Reject(fixture.Fence with {
            Generation = 0
        });
        Reject(fixture.Fence with {
            ReviewedBy = fixture.Fence.RequestedBy
        });
        Reject(fixture.Fence with {
            ExecutionAuthorized = true
        });
        Reject(fixture.Fence with {
            RemoteWritesPerformed = true
        });
        Reject(fixture.Fence with {
            ReadyForExecution = true
        });
        Reject(fixture.Fence, fixture.Scope with {
            InstallationId = 0
        });
        Reject(fixture.Fence, fixture.Scope with {
            ScopedContentsWriteReported = false
        });
    }

    [Fact]
    public void Stale_clock_or_unverified_App_token_expiry_refuse_seal()
    {
        var fixture = Valid();
        var old = fixture.Observation with {
            TargetRefs = fixture.Observation.TargetRefs with {
                ObservedAtUtc = fixture.Now.AddSeconds(-26)
            }
        };
        Assert.Equal("RemotePreflightEvidenceInvalid",
            Assert.Throws<GitHubRemoteAppPreflightException>(() =>
                fixture.Seal(observation: old)).Code);
        var oldToken = fixture.Observation with {
            TokenExpiresAtUtc = fixture.Now.AddSeconds(10)
        };
        Assert.Equal("RemotePreflightEvidenceInvalid",
            Assert.Throws<GitHubRemoteAppPreflightException>(() =>
                fixture.Seal(observation: oldToken)).Code);
    }

    [Fact]
    public void F077_historical_receipt_recomputes_exact_F051_original_digest()
    {
        var fixture = Valid();
        var issued = fixture.Seal();
        var recomputed = GitHubRemoteAppPreflightIntegrity
            .RecomputeHistoricalEvidenceSha256(issued,
                fixture.Observation.CandidatePolicies.ObservedAtUtc);
        Assert.Equal(issued.EvidenceSha256, recomputed);
        Assert.NotEqual(issued.EvidenceSha256,
            GitHubRemoteAppPreflightIntegrity
                .RecomputeHistoricalEvidenceSha256(issued,
                    fixture.Observation.CandidatePolicies.ObservedAtUtc
                        .AddMilliseconds(10)));
        Assert.Equal(64, recomputed.Length);
    }

    [Theory]
    [InlineData("wrong-baseline")]
    [InlineData("wrong-state")]
    [InlineData("write-enabled")]
    [InlineData("too-many-candidates")]
    [InlineData("token-expired")]
    [InlineData("bad-policy-timestamp")]
    public void F077_corrupted_historical_F051_witness_cannot_be_rehashed(
        string mutation)
    {
        var fixture = Valid();
        var issued = fixture.Seal();
        var policyTime = fixture.Observation.CandidatePolicies.ObservedAtUtc;
        var altered = mutation switch
        {
            "wrong-baseline" => issued with {
                ObservedBaselineSha256 = Sha('f')
            },
            "wrong-state" => issued with {
                State = "Unverified"
            },
            "write-enabled" => issued with {
                ExecutionAuthorized = true
            },
            "too-many-candidates" => issued with {
                AbsentCreateCandidates = 17
            },
            "token-expired" => issued with {
                InstallationTokenExpiresAtUtc = issued.ObservedAtUtc
                    .AddSeconds(10)
            },
            "bad-policy-timestamp" => issued,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        if (mutation == "bad-policy-timestamp")
            policyTime = policyTime.AddMinutes(2);
        var ex = Assert.Throws<GitHubRemoteAppPreflightException>(() =>
            GitHubRemoteAppPreflightIntegrity
                .RecomputeHistoricalEvidenceSha256(altered, policyTime));
        Assert.Equal("RemotePreflightHistoricalEvidenceInvalid", ex.Code);
    }

    private static string Sha(char c) => new(c, 64);

    private static Fixture Valid()
    {
        var now = DateTimeOffset.UtcNow;
        var candidate = new GitMirrorRef(
            "refs/heads/feature", new string('b', 40));
        var hash = GitImportRehearsalIntegrity.SealRefs(
            "f044-create-only-candidate-refs-v1", [candidate]);
        var packet = new GitHubCreateOnlyPacket(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2468, "gh-org", "sandbox", Sha('1'), Sha('2'), Sha('3'),
            Sha('4'), Sha('5'), Sha('6'), Sha('7'), Sha('8'),
            Sha('9'), hash, Sha('a'),
            1, 1, 1, GitHubCreateOnlyPacketIntegrity.State,
            false, false, false, false);
        var source = new GitHubCreateOnlyCandidatePacket(packet, [candidate]);
        var remote = GitHubRefObservationIntegrity.Seal(
            packet.Organization, packet.Repository, now,
            [new("refs/heads/main", new string('a', 40), "commit"),
             new("refs/tags/v1", new string('c', 40), "tag")]);
        var baseline = GitHubRemoteRefFenceIntegrity.Fingerprint(remote.Refs);
        var previousFence = new GitHubRemoteRefFenceView(
            packet.FenceId, packet.PreparationId, packet.PlanId,
            packet.WorkpackId, packet.IntentId, packet.RehearsalId,
            Guid.NewGuid(), packet.ManifestSha256,
            packet.ReconciliationSha256, packet.BundleSha256,
            packet.IntentEvidenceSha256, packet.RehearsalEvidenceSha256,
            Sha('b'), Sha('c'), baseline, packet.FenceEvidenceSha256,
            "RefSetUnchanged", 2, 2, 0, now, now.AddSeconds(30),
            Guid.NewGuid(), true, false, false, false, false);
        var scope = new GitHubAppWriteScopeView(
            packet.ScopeId, packet.PreparationId, packet.PlanId,
            packet.FenceId, packet.WorkpackId, packet.IntentId,
            packet.RehearsalId, packet.Organization, packet.Repository,
            packet.RepositoryId, 77, packet.ManifestSha256,
            packet.BundleSha256, packet.IntentEvidenceSha256,
            packet.RehearsalEvidenceSha256, packet.FenceEvidenceSha256,
            baseline, packet.ScopeEvidenceSha256,
            GitHubAppWriteScopeIntegrity.Issued, now,
            now.AddMinutes(45), Guid.NewGuid(),
            true, true, false, false, false, false);
        var actor = Guid.NewGuid();
        var fence = new GitHubRemoteSandboxFenceStatus(
            Guid.NewGuid(), packet.RepositoryId, packet.Organization,
            packet.Repository, packet.PreparationId, packet.PlanId,
            packet.ScopeId, Guid.NewGuid(), packet.EvidenceSha256,
            packet.CandidateSetSha256, Sha('d'), 2,
            GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled,
            actor, Guid.NewGuid(), now.AddSeconds(20), now,
            false, false, false);
        var policies = new GitHubCandidatePolicyObservation(
            [new(GitHubCandidatePolicyIntegrity.RefNameDigest(candidate.Name),
                "Branch", GitHubCandidatePolicyStates.NoActiveRulesReported,
                0, "Observed")], now, "Observed");
        var observation = new GitHubRemoteAppPreflightObservation(
            77, packet.RepositoryId, remote, policies, now.AddMinutes(45));
        return new(source, fence, previousFence, scope, observation, now);
    }

    private sealed record Fixture(
        GitHubCreateOnlyCandidatePacket Source,
        GitHubRemoteSandboxFenceStatus Fence,
        GitHubRemoteRefFenceView Prior,
        GitHubAppWriteScopeView Scope,
        GitHubRemoteAppPreflightObservation Observation,
        DateTimeOffset Now)
    {
        public GitHubRemoteAppPreflightReceipt Seal(
            GitHubRemoteSandboxFenceStatus? fence = null,
            GitHubAppWriteScopeView? scope = null,
            GitHubRemoteAppPreflightObservation? observation = null) =>
            GitHubRemoteAppPreflightIntegrity.Seal(
                Source, fence ?? Fence, Prior, scope ?? Scope,
                observation ?? Observation, Now);
    }
}
