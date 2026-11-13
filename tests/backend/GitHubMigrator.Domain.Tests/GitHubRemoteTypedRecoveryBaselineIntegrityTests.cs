using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteTypedRecoveryBaselineIntegrityTests
{
    private static string Sha(char c) => new(c, 64);
    private static string Oid(char c) => new(c, 40);

    private static GitHubRemoteTypedRecoveryBaselineReceipt Valid()
    {
        var now = DateTimeOffset.UtcNow;
        var baseline = GitHubRefObservationIntegrity.Seal(
            "gh-org", "sandbox", now,
            [new GitHubObservedRef("refs/heads/main", Oid('a'), "commit"),
             new GitHubObservedRef("refs/tags/v1", Oid('b'), "tag")]);
        GitMirrorRef[] candidates = [
            new("refs/heads/feature", Oid('c'))
        ];
        var row = new GitHubRemoteTypedRecoveryBaselineReceipt(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            29831, "gh-org", "sandbox", Guid.NewGuid(),
            Sha('1'), GitImportRehearsalIntegrity.SealRefs(
                "f044-create-only-candidate-refs-v1", candidates),
            GitHubRemoteRefFenceIntegrity.Fingerprint(baseline.Refs),
            baseline, candidates, now,
            GitHubRemoteTypedRecoveryBaselineIntegrity.Captured,
            "", false, false, false);
        return row with {
            EvidenceSha256 =
                GitHubRemoteTypedRecoveryBaselineIntegrity.Digest(row)
        };
    }

    [Fact]
    public void F086_all_recovery_ids_and_exact_typed_refs_are_in_digest()
    {
        var r = Valid();
        GitHubRemoteTypedRecoveryBaselineIntegrity.Verify(r);
        Assert.Equal(64, r.EvidenceSha256.Length);
        foreach (var altered in new[] {
            r with { ConfirmationId = Guid.NewGuid() },
            r with { IntentId = Guid.NewGuid() },
            r with { HazardId = Guid.NewGuid() },
            r with { GrantId = Guid.NewGuid() },
            r with { PreparationId = Guid.NewGuid() },
            r with { PlanId = Guid.NewGuid() },
            r with { ScopeId = Guid.NewGuid() },
            r with { RepositoryId = 223344 },
            r with { CapturedBy = Guid.NewGuid() },
            r with { PacketSha256 = Sha('0') },
            r with { Organization = "another-org" }
        })
        {
            Assert.Throws<GitHubRemoteTypedRecoveryBaselineException>(() =>
                GitHubRemoteTypedRecoveryBaselineIntegrity.Verify(altered));
        }
    }

    [Fact]
    public void F086_ref_type_oid_candidate_and_executable_flag_tampering_rejected()
    {
        var r = Valid();
        var forged = new[] {
            r with {
                Baseline = r.Baseline with {
                    Refs = r.Baseline.Refs.Select(x =>
                        x.Name == "refs/tags/v1"
                            ? x with { ObjectType = "commit" } : x).ToArray()
                }
            },
            r with {
                Baseline = r.Baseline with {
                    Refs = r.Baseline.Refs.Select(x =>
                        x.Name == "refs/heads/main"
                            ? x with { ObjectId = Oid('f') } : x).ToArray()
                }
            },
            r with { CreateCandidates = [new("refs/heads/feature", Oid('d'))] },
            r with { BaselineSha256 = Sha('a') },
            r with { CandidateSetSha256 = Sha('b') },
            r with { State = "CanPush" },
            r with { CanSend = true },
            r with { MayAutomaticallyRetry = true },
            r with { MigrationComplete = true },
            r with {
                Baseline = r.Baseline with {
                    Refs = [.. r.Baseline.Refs,
                        new GitHubObservedRef(
                            "refs/heads/feature", Oid('c'), "commit")]
                }
            }
        };
        foreach (var x in forged)
            Assert.Throws<GitHubRemoteTypedRecoveryBaselineException>(() =>
                GitHubRemoteTypedRecoveryBaselineIntegrity.Digest(x));
    }
}
