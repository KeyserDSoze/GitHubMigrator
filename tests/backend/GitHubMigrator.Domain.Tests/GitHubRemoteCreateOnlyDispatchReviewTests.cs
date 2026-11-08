using System.Text.Json;
using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteCreateOnlyDispatchReviewTests
{
    private static string Sha(char value) => new(value, 64);
    private static string Oid(char value) => new(value, 40);

    [Fact]
    public void Stable_F044_F051_F052_F055_and_last_ref_readback_seal_only_write_disabled_review()
    {
        var f = Valid();
        var first = f.Seal();
        var replay = f.Seal();
        Assert.Equal(first, replay);
        Assert.Equal(GitHubRemoteCreateOnlyDispatchIntegrity.State, first.State);
        Assert.Equal(f.Hazard.Id, first.HazardId);
        Assert.Equal(f.Grant.Id, first.GrantId);
        Assert.Equal(f.Source.Packet.RepositoryId, first.RepositoryId);
        Assert.Equal(f.Source.Packet.EvidenceSha256, first.PacketSha256);
        Assert.Equal(f.Source.Packet.CandidateSetSha256,
            first.CandidateSetSha256);
        Assert.Equal(f.Preflight.CurrentPolicySha256,
            first.ReviewedPolicySha256);
        Assert.Equal(1, first.AbsentCreateCandidates);
        Assert.Equal(2, first.ExistingPreservedRefs);
        Assert.Equal(64, first.EvidenceSha256.Length);
        Assert.True(first.ExpiresAtUtc > f.Now);
        Assert.True(first.ExpiresAtUtc <= f.Now.AddSeconds(10));
        Assert.False(first.RemoteTransportInvoked);
        Assert.False(first.RemoteWritesAuthorized);
        Assert.False(first.MayAutomaticallyRetry);
        Assert.False(first.MigrationComplete);
        var body = JsonSerializer.Serialize(first);
        Assert.DoesNotContain("refs/", body);
        Assert.DoesNotContain("ghs_", body);
        Assert.DoesNotContain("bearer", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE KEY", body);
        Assert.NotEqual(first.EvidenceSha256, f.Seal(
            actor: Guid.NewGuid()).EvidenceSha256);
    }

    [Theory]
    [InlineData("candidate-hash")]
    [InlineData("candidate-list")]
    [InlineData("candidate-outside-heads-tags")]
    [InlineData("packet-writes")]
    [InlineData("wrong-repository")]
    [InlineData("wrong-packet")]
    [InlineData("wrong-generation")]
    [InlineData("policy-drift")]
    [InlineData("grant-cancelled")]
    [InlineData("grant-expired")]
    [InlineData("grant-unreviewed")]
    [InlineData("prior-reviewer")]
    [InlineData("preflight-old")]
    [InlineData("preflight-token-expired")]
    [InlineData("preflight-unverified")]
    [InlineData("preflight-baseline")]
    [InlineData("hazard-may-have-transmitted")]
    [InlineData("hazard-operator-review")]
    [InlineData("hazard-epoch")]
    [InlineData("hazard-expired")]
    [InlineData("hazard-marked")]
    [InlineData("hazard-auto-retry")]
    [InlineData("hazard-writes")]
    [InlineData("latest-old")]
    [InlineData("latest-forged-digest")]
    [InlineData("latest-wrong-owner")]
    [InlineData("latest-noncanonical")]
    [InlineData("remote-token-observation-older-than-preflight")]
    public void Cross_witness_mismatch_is_rejected_without_returning_credential(
        string mutation)
    {
        var f = Valid();
        var wrongName = new GitMirrorRef("refs/custom/outside", Oid('b'));
        var corrupted = mutation switch
        {
            "candidate-hash" => f with {
                Source = f.Source with {
                    Packet = f.Source.Packet with {
                        CandidateSetSha256 = Sha('f')
                    }
                }
            },
            "candidate-list" => f with {
                Source = f.Source with {
                    Candidates = [new GitMirrorRef("refs/heads/other", Oid('b'))]
                }
            },
            "candidate-outside-heads-tags" => f with {
                Source = f.Source with {
                    Candidates = [wrongName]
                }
            },
            "packet-writes" => f with {
                Source = f.Source with {
                    Packet = f.Source.Packet with { ExecutionAuthorized = true }
                }
            },
            "wrong-repository" => f with {
                Grant = f.Grant with { RepositoryId = 9999 }
            },
            "wrong-packet" => f with {
                Hazard = f.Hazard with { PacketSha256 = Sha('f') }
            },
            "wrong-generation" => f with {
                Hazard = f.Hazard with { FenceGeneration = 42 }
            },
            "policy-drift" => f with {
                Grant = f.Grant with { OriginalPolicySha256 = Sha('f') }
            },
            "grant-cancelled" => f with {
                Grant = f.Grant with {
                    State = GitHubRemoteTestGrantStates.Cancelled
                }
            },
            "grant-expired" => f with {
                Grant = f.Grant with { DeadlineUtc = f.Now.AddSeconds(-1) }
            },
            "grant-unreviewed" => f with {
                Grant = f.Grant with { ReviewedBy = null }
            },
            "prior-reviewer" => f with {
                Actor = f.Grant.ReviewedBy!.Value
            },
            "preflight-old" => f with {
                Preflight = f.Preflight with {
                    ObservedAtUtc = f.Now.AddSeconds(-30)
                }
            },
            "preflight-token-expired" => f with {
                Preflight = f.Preflight with {
                    InstallationTokenExpiresAtUtc = f.Now.AddSeconds(30)
                }
            },
            "preflight-unverified" => f with {
                Preflight = f.Preflight with { State = "Unverified" }
            },
            "preflight-baseline" => f with {
                Preflight = f.Preflight with {
                    ExpectedBaselineSha256 = Sha('f')
                }
            },
            "hazard-may-have-transmitted" => f with {
                Hazard = f.Hazard with {
                    State = GitHubRemoteTransmitHazardStates.MayHaveTransmitted
                }
            },
            "hazard-operator-review" => f with {
                Hazard = f.Hazard with {
                    State = GitHubRemoteTransmitHazardStates.OperatorReviewRequired
                }
            },
            "hazard-epoch" => f with {
                Hazard = f.Hazard with { FenceRequestId = Guid.NewGuid() }
            },
            "hazard-expired" => f with {
                Hazard = f.Hazard with { DeadlineUtc = f.Now.AddSeconds(-1) }
            },
            "hazard-marked" => f with {
                Hazard = f.Hazard with { MarkedBy = Guid.NewGuid() }
            },
            "hazard-auto-retry" => f with {
                Hazard = f.Hazard with { MayAutomaticallyRetry = true }
            },
            "hazard-writes" => f with {
                Hazard = f.Hazard with { TransportInvoked = true }
            },
            "latest-old" => f with {
                Latest = GitHubRefObservationIntegrity.Seal(
                    "gh-org", "sandbox", f.Now.AddSeconds(-12),
                    f.Latest.Refs)
            },
            "latest-forged-digest" => f with {
                Latest = f.Latest with { EvidenceSha256 = Sha('f') }
            },
            "latest-wrong-owner" => f with {
                Latest = GitHubRefObservationIntegrity.Seal(
                    "wrong-org", "sandbox", f.Now,
                    f.Latest.Refs)
            },
            "latest-noncanonical" => f with {
                Latest = f.Latest with {
                    Refs = f.Latest.Refs.Reverse().ToArray()
                }
            },
            "remote-token-observation-older-than-preflight" => f with {
                Latest = GitHubRefObservationIntegrity.Seal(
                    "gh-org", "sandbox", f.Now.AddSeconds(-5),
                    f.Latest.Refs)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        var error = Assert.Throws<GitHubRemoteCreateOnlyDispatchException>(
            () => corrupted.Seal());
        Assert.Equal("RemoteDispatchEvidenceInvalid", error.Code);
        Assert.DoesNotContain("refs/", error.Message);
        Assert.DoesNotContain("ghs_", error.Message);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("annotated-tag")]
    [InlineData("new-candidate-ref")]
    [InlineData("target-only-added")]
    public void Fresh_remote_ref_changes_or_create_ref_collision_abort_review(
        string mutation)
    {
        var f = Valid();
        var observed = mutation switch
        {
            "head" => f.Latest.Refs.Select(x =>
                x.Name == "refs/heads/main" ? x with {
                    ObjectId = Oid('e')
                } : x).ToArray(),
            "annotated-tag" => f.Latest.Refs.Select(x =>
                x.Name == "refs/tags/v1" ? x with {
                    ObjectId = Oid('d')
                } : x).ToArray(),
            "new-candidate-ref" => [
                ..f.Latest.Refs,
                new GitHubObservedRef("refs/heads/new", Oid('b'), "commit")
            ],
            "target-only-added" => [
                ..f.Latest.Refs,
                new GitHubObservedRef("refs/heads/external", Oid('d'), "commit")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        var changed = GitHubRefObservationIntegrity.Seal(
            "gh-org", "sandbox", f.Now, observed);
        var ex = Assert.Throws<GitHubRemoteCreateOnlyDispatchException>(
            () => (f with { Latest = changed }).Seal());
        Assert.Equal("RemoteDispatchTargetDriftBlocked", ex.Code);
    }

    [Fact]
    public void Independent_operator_and_pessimistic_hazard_epoch_are_necessary()
    {
        var f = Valid();
        Assert.Equal("RemoteDispatchEvidenceInvalid",
            Assert.Throws<GitHubRemoteCreateOnlyDispatchException>(
                () => (f with {
                    Actor = f.Grant.RequestedBy
                }).Seal()).Code);
        Assert.Equal("RemoteDispatchEvidenceInvalid",
            Assert.Throws<GitHubRemoteCreateOnlyDispatchException>(
                () => (f with {
                    Hazard = f.Hazard with {
                        GrantId = Guid.NewGuid()
                    }
                }).Seal()).Code);

        // Even an apparently clean last ref readback cannot turn an
        // already possible transmission into permission to retry.
        Assert.Equal("RemoteDispatchEvidenceInvalid",
            Assert.Throws<GitHubRemoteCreateOnlyDispatchException>(
                () => (f with {
                    Hazard = f.Hazard with {
                        State =
                            GitHubRemoteTransmitHazardStates.MayHaveTransmitted
                    }
                }).Seal()).Code);
    }

    private sealed record Fixture(
        GitHubCreateOnlyCandidatePacket Source,
        GitHubRemoteAppPreflightReceipt Preflight,
        GitHubRemoteTestGrantStatus Grant,
        GitHubRemoteTransmitHazardStatus Hazard,
        GitHubRefObservation Latest, Guid Actor, DateTimeOffset Now)
    {
        public GitHubRemoteCreateOnlyDispatchReview Seal(Guid? actor = null) =>
            GitHubRemoteCreateOnlyDispatchIntegrity.Seal(
                Source, Preflight, Grant, Hazard, Latest,
                actor ?? Actor, Now);
    }

    private static Fixture Valid()
    {
        var now = DateTimeOffset.UtcNow;
        var candidate = new GitMirrorRef("refs/heads/new", Oid('b'));
        var candidates = new[] { candidate };
        var candidateSha = GitImportRehearsalIntegrity.SealRefs(
            "f044-create-only-candidate-refs-v1", candidates);
        var prep = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var scope = Guid.NewGuid();
        var fence = Guid.NewGuid();
        var repo = 2468L;
        var packetSha = Sha('9');
        var packet = new GitHubCreateOnlyPacket(
            prep, planId, scope, fence, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), repo, "gh-org", "sandbox", Sha('1'),
            Sha('2'), Sha('3'), Sha('4'), Sha('5'), Sha('6'),
            Sha('7'), Sha('8'), Sha('a'), candidateSha, packetSha,
            1, 1, 1, GitHubCreateOnlyPacketIntegrity.State,
            false, false, false, false);
        var source = new GitHubCreateOnlyCandidatePacket(
            packet, candidates);
        var observed = GitHubRefObservationIntegrity.Seal(
            "gh-org", "sandbox", now,
            [new("refs/heads/main", Oid('a'), "commit"),
             new("refs/tags/v1", Oid('c'), "tag")]);
        var baseline = GitHubRemoteRefFenceIntegrity.Fingerprint(
            observed.Refs);
        var requestId = Guid.NewGuid();
        var preflight = new GitHubRemoteAppPreflightReceipt(
            requestId, 2, repo, packetSha, candidateSha,
            Sha('8'), Sha('c'), baseline, baseline, 2, 1,
            now.AddSeconds(-1), now.AddMinutes(45), Sha('d'),
            GitHubRemoteAppPreflightIntegrity.VerifiedState,
            false, false, false);
        var grant = new GitHubRemoteTestGrantStatus(
            Guid.NewGuid(), repo, "gh-org", "sandbox",
            prep, planId, scope, Guid.NewGuid(), requestId, 2,
            packetSha, candidateSha, preflight.OriginalPolicySha256,
            baseline, preflight.EvidenceSha256,
            GitHubRemoteTestGrantStates.ReviewedWriteDisabled,
            Guid.NewGuid(), Guid.NewGuid(),
            now.AddSeconds(40), now, false, false, false);
        var hazard = new GitHubRemoteTransmitHazardStatus(
            Guid.NewGuid(), grant.Id, repo, requestId, 2,
            packetSha, candidateSha, baseline,
            GitHubRemoteTransmitHazardStates.PreparedWriteDisabled,
            Guid.NewGuid(), null, now.AddSeconds(25), now,
            false, false, false, false);
        return new(source, preflight, grant, hazard, observed,
            Guid.NewGuid(), now);
    }
}
