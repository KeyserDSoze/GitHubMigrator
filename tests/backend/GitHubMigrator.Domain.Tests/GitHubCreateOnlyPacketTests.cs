using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubCreateOnlyPacketTests
{
    [Fact]
    public void Valid_immutable_lineage_yields_deterministic_non_executable_packet()
    {
        var x = Fixture.Create();
        var first = x.Build();
        var second = x.Build();
        Assert.Equal(first, second);
        Assert.Equal(64, first.EvidenceSha256.Length);
        Assert.Equal(64, first.CandidateSetSha256.Length);
        Assert.Equal(1, first.CreateCandidates);
        Assert.Equal(1, first.AlreadyMatching);
        Assert.Equal(1, first.TargetOnlyPreserved);
        Assert.Equal(GitHubCreateOnlyPacketIntegrity.State, first.State);
        Assert.False(first.WriteCapabilityVerified);
        Assert.False(first.ExecutionAuthorized);
        Assert.False(first.RemoteWritesPerformed);
        Assert.False(first.ReadyForExecution);
        var output = JsonSerializer.Serialize(first);
        Assert.DoesNotContain("refs/heads", output);
        Assert.DoesNotContain("git push", output);
        Assert.DoesNotContain("ghs_", output);
        Assert.DoesNotContain("https:", output);
    }

    [Fact]
    public void Reordering_or_changing_source_refs_never_creates_an_equivalent_packet()
    {
        var x = Fixture.Create();
        var reordered = x with { Refs = x.Refs.Reverse().ToArray() };
        Assert.Throws<GitHubCreateOnlyPacketException>(() => reordered.Build());
        var modified = x.Refs.ToArray();
        modified[1] = modified[1] with {
            SourceObjectId = new string('d', 40)
        };
        var error = Assert.Throws<GitHubCreateOnlyPacketException>(
            () => (x with { Refs = modified }).Build());
        Assert.Equal("PacketRefPlanChanged", error.Code);
    }

    [Fact]
    public void Source_only_and_target_only_are_never_reclassified_as_create_operations()
    {
        var x = Fixture.Create();
        var refs = x.Refs.ToArray();
        refs[2] = refs[2] with {
            Disposition = GitMirrorRefDisposition.CreateCandidate,
            SourceObjectId = new string('b', 40), TargetObjectId = null
        };
        Assert.Throws<GitHubCreateOnlyPacketException>(
            () => (x with { Refs = refs }).Build());
    }

    [Fact]
    public void Change_of_any_attestation_or_rehearsal_digest_is_rejected()
    {
        var x = Fixture.Create();
        var changedScope = x with {
            Scope = x.Scope with { FenceEvidenceSha256 = Sha('d') }
        };
        Assert.Equal("PacketLineageMismatch",
            Assert.Throws<GitHubCreateOnlyPacketException>(
                () => changedScope.Build()).Code);
        var changedRehearsal = x with {
            Rehearsal = x.Rehearsal with { CreatedRefsSha256 = Sha('e') }
        };
        Assert.Equal("PacketRehearsalChanged",
            Assert.Throws<GitHubCreateOnlyPacketException>(
                () => changedRehearsal.Build()).Code);
    }

    [Fact]
    public void Changed_repository_reviewer_and_plan_are_rejected()
    {
        var x = Fixture.Create();
        Assert.Equal("PacketLineageMismatch",
            Assert.Throws<GitHubCreateOnlyPacketException>(() =>
                (x with { Scope = x.Scope with { Repository = "other" } })
                    .Build()).Code);
        Assert.Equal("PacketLineageMismatch",
            Assert.Throws<GitHubCreateOnlyPacketException>(() =>
                (x with { Scope = x.Scope with { ActorId = x.Intent.RequestedBy } })
                    .Build()).Code);
        // Connection identity is part of the immutable packet seal:
        // changing the destination produces different evidence even if
        // the current view still reports the same repository.
        var changedDestination = x with {
            Plan = x.Plan with { DestinationConnectionId = Guid.NewGuid() }
        };
        Assert.NotEqual(x.Build().EvidenceSha256,
            changedDestination.Build().EvidenceSha256);
    }

    [Fact]
    public void Stale_or_executable_evidence_cannot_become_a_packet()
    {
        var x = Fixture.Create();
        Assert.Equal("PacketDependencyStale",
            Assert.Throws<GitHubCreateOnlyPacketException>(() =>
                (x with { Fence = x.Fence with { IsCurrent = false } })
                    .Build()).Code);
        Assert.Equal("PacketDependencyStale",
            Assert.Throws<GitHubCreateOnlyPacketException>(() =>
                (x with { Scope = x.Scope with { ExecutionAuthorized = true } })
                    .Build()).Code);
        Assert.Equal("PacketDependencyStale",
            Assert.Throws<GitHubCreateOnlyPacketException>(() =>
                (x with { Bundle = x.Bundle with { ReadyForExecution = true } })
                    .Build()).Code);
        Assert.Equal("PacketDependencyStale",
            Assert.Throws<GitHubCreateOnlyPacketException>(() =>
                (x with { Intent = x.Intent with { IsExpired = true } })
                    .Build()).Code);
    }

    [Fact]
    public void A_forged_policy_or_candidate_name_is_rejected()
    {
        var x = Fixture.Create();
        Assert.Equal("PacketLineageMismatch",
            Assert.Throws<GitHubCreateOnlyPacketException>(() =>
                (x with { Intent = x.Intent with { PolicySha256 = "not-a-digest" } })
                    .Build()).Code);
        var refs = x.Refs.ToArray();
        refs[1] = refs[1] with { Name = "refs/notes/hijack" };
        Assert.Throws<GitHubCreateOnlyPacketException>(() =>
            (x with { Refs = refs }).Build());
    }

    [Fact]
    public void Candidate_count_cannot_exceed_the_real_git_rehearsal_limit()
    {
        var x = Fixture.Create();
        var candidates = Enumerable.Range(0, 129)
            .Select(i => new GitMirrorRefComparison(
                $"refs/heads/new-{i:000}", new string('b', 40), null,
                GitMirrorRefDisposition.CreateCandidate,
                "DestinationWritePermissionUnverified"))
            .ToArray();
        var safe = GitHubSafeRefPlanner.Build(x.Plan.Id,
            x.Plan.ManifestSha256, x.Readiness.ReconciliationSha256,
            x.Bundle.BundleSha256, candidates);
        var modified = x with {
            Refs = candidates,
            Intent = x.Intent with { SafeRefPlanSha256 = safe.EvidenceSha256 },
            Rehearsal = x.Rehearsal with { SafeRefPlanSha256 = safe.EvidenceSha256 }
        };
        Assert.Equal("PacketRefPlanChanged",
            Assert.Throws<GitHubCreateOnlyPacketException>(
                () => modified.Build()).Code);
    }

    private static string Sha(char c) => new(c, 64);

    private sealed record Fixture(
        GitHubAppWriteScopeView Scope, GitHubRemoteRefFenceView Fence,
        GitHubImportIntentView Intent, GitImportRehearsalView Rehearsal,
        IncrementalOperationPlan Plan, GitHubImportReadinessView Readiness,
        GitSourceBundleView Bundle, IReadOnlyList<GitMirrorRefComparison> Refs)
    {
        public GitHubCreateOnlyPacket Build() =>
            GitHubCreateOnlyPacketIntegrity.Build(Scope, Fence, Intent,
                Rehearsal, Plan, Readiness, Bundle, Refs);

        public static Fixture Create()
        {
            var prep = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var scopeId = Guid.NewGuid();
            var fenceId = Guid.NewGuid();
            var intentId = Guid.NewGuid();
            var rehearsalId = Guid.NewGuid();
            var workpackId = Guid.NewGuid();
            var reviewId = Guid.NewGuid();
            var comparisonId = Guid.NewGuid();
            var connectionId = Guid.NewGuid();
            var requester = Guid.NewGuid();
            var reviewer = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var manifest = Sha('a');
            var bundleHash = Sha('b');
            var reconcHash = Sha('c');
            var intentHash = Sha('d');
            var rehearsalHash = Sha('e');
            var fenceHash = Sha('f');
            var scopeHash = Sha('1');
            var reviewHash = Sha('2');
            var policyHash = Sha('3');
            var fingerprint = Sha('4');
            var items = new[] {
                new GitMirrorRefComparison("refs/heads/main", new string('a', 40),
                    new string('a', 40), GitMirrorRefDisposition.MatchingObjectId,
                    "None"),
                new GitMirrorRefComparison("refs/heads/new", new string('b', 40),
                    null, GitMirrorRefDisposition.CreateCandidate,
                    "DestinationWritePermissionUnverified"),
                new GitMirrorRefComparison("refs/tags/legacy", null,
                    new string('c', 40), GitMirrorRefDisposition.TargetOnlyPreserved,
                    "TargetRefDeletionProhibited")
            }.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
            var safe = GitHubSafeRefPlanner.Build(planId, manifest,
                reconcHash, bundleHash, items);
            var sourceRefs = items.Where(x => x.SourceObjectId is not null)
                .Select(x => new GitMirrorRef(x.Name, x.SourceObjectId!)).ToArray();
            var creates = items.Where(x =>
                x.Disposition == GitMirrorRefDisposition.CreateCandidate)
                .Select(x => new GitMirrorRef(x.Name, x.SourceObjectId!)).ToArray();

            var plan = new IncrementalOperationPlan(planId, prep, Guid.NewGuid(),
                Guid.NewGuid(), connectionId, "gh-org", Sha('5'), Sha('6'),
                Sha('7'), manifest, now, requester, 1, 6, 2, 0, true,
                false, [], []);
            var intent = new GitHubImportIntentView(
                intentId, prep, planId, workpackId, reviewId, reviewHash,
                manifest, reconcHash, bundleHash, safe.EvidenceSha256,
                policyHash, intentHash, 1, 1, 0, 1, "Observed", 0, 0,
                "NotVisibleOrMissing", GitHubImportIntentStates.Acknowledged,
                requester, reviewer, now.AddMinutes(-1), now.AddMinutes(9),
                now, true, false, false, false, false);
            var rehearsal = new GitImportRehearsalView(
                rehearsalId, intentId, prep, planId, workpackId, intentHash,
                manifest, reconcHash, bundleHash, safe.EvidenceSha256, rehearsalHash,
                "sha1", GitImportRehearsalIntegrity.SealRefs(
                    "f041-source-refs-v1", sourceRefs),
                1, 1, 1, 1,
                GitImportRehearsalIntegrity.SealRefs(
                    "f041-created-refs-v1", creates),
                now, reviewer, true, false, false, false);
            var fence = new GitHubRemoteRefFenceView(
                fenceId, prep, planId, workpackId, intentId, rehearsalId,
                Guid.NewGuid(), manifest, reconcHash, bundleHash, intentHash,
                rehearsalHash, Sha('8'), Sha('9'), fingerprint, fenceHash,
                "RefSetUnchanged", 2, 2, 0, now,
                now.AddSeconds(30), reviewer, true, false, false, false, false);
            var scope = new GitHubAppWriteScopeView(
                scopeId, prep, planId, fenceId, workpackId, intentId, rehearsalId,
                "gh-org", "sandbox", 2468, 77, manifest, bundleHash,
                intentHash, rehearsalHash, fenceHash, fingerprint, scopeHash,
                GitHubAppWriteScopeIntegrity.Issued, now, now.AddMinutes(45),
                reviewer, true, true, false, false, false, false);
            var readiness = new GitHubImportReadinessView(
                reviewId, planId, workpackId, comparisonId, "repo-1", "sandbox",
                "gh-org", manifest, reconcHash, Sha('8'), bundleHash,
                reviewHash, now, "Visible", "ReportedAllowed", false, false,
                0, 1, [], true, false, false);
            var bundle = new GitSourceBundleView(
                workpackId, planId, comparisonId, "repo-1", Sha('9'),
                reconcHash, workpackId.ToString("N") + ".bundle",
                bundleHash, 1024, 2, now, true, false);
            return new(scope, fence, intent, rehearsal, plan, readiness, bundle, items);
        }
    }
}
