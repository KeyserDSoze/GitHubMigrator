using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitMirrorRefReconciliationTests
{
    private const string PlanProof =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SourceProof =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string TargetProof =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string OidA = "1111111111111111111111111111111111111111";
    private const string OidB = "2222222222222222222222222222222222222222";

    private static readonly Guid PlanId =
        Guid.Parse("12345678-1234-4234-8234-123456789abc");

    private static GitMirrorRefReconciliationRequest Request(
        IReadOnlyList<GitMirrorRef> source, IReadOnlyList<GitMirrorRef> target) =>
        new(PlanId, PlanProof, SourceProof, TargetProof, source, target);

    [Fact]
    public void Reconciles_branches_and_tags_in_stable_order_without_any_execution_permit()
    {
        var source = new[] {
            new GitMirrorRef("refs/tags/v1", OidA),
            new GitMirrorRef("refs/heads/main", OidA),
            new GitMirrorRef("refs/heads/feature", OidB)
        };
        var target = new[] {
            new GitMirrorRef("refs/heads/obsolete", OidA),
            new GitMirrorRef("refs/heads/main", OidA),
            new GitMirrorRef("refs/tags/v1", OidB)
        };
        var result = GitMirrorRefReconciler.Compare(Request(source, target));
        Assert.False(result.ReadyForExecution);
        Assert.Equal(3, result.SourceRefCount);
        Assert.Equal(3, result.TargetRefCount);
        Assert.Equal(1, result.MatchingCount);
        Assert.Equal(1, result.CreateCandidateCount);
        Assert.Equal(1, result.DivergedCount);
        Assert.Equal(1, result.TargetOnlyCount);
        Assert.Equal(4, result.Items.Count);
        Assert.Equal(new[] {
            "refs/heads/feature", "refs/heads/main",
            "refs/heads/obsolete", "refs/tags/v1"
        }, result.Items.Select(x => x.Name));
        Assert.Equal(GitMirrorRefDisposition.CreateCandidate,
            result.Items[0].Disposition);
        Assert.Equal("DestinationWritePermissionUnverified",
            result.Items[0].BlockerCode);
        Assert.Equal(GitMirrorRefDisposition.MatchingObjectId,
            result.Items[1].Disposition);
        Assert.Equal("None", result.Items[1].BlockerCode);
        Assert.Equal(GitMirrorRefDisposition.TargetOnlyPreserved,
            result.Items[2].Disposition);
        Assert.Equal("TargetRefDeletionProhibited", result.Items[2].BlockerCode);
        Assert.Equal(GitMirrorRefDisposition.DivergedBlocked,
            result.Items[3].Disposition);
        Assert.Equal("RefFastForwardUnverified", result.Items[3].BlockerCode);
        Assert.Equal(64, result.ReconciliationSha256.Length);

        var permuted = GitMirrorRefReconciler.Compare(
            Request(source.Reverse().ToArray(), target.Reverse().ToArray()));
        Assert.Equal(result.ReconciliationSha256, permuted.ReconciliationSha256);
        Assert.Equal(result.Items.ToArray(), permuted.Items.ToArray());
    }

    [Fact]
    public void Object_id_casing_is_normalized_but_a_divergent_oid_is_not_considered_fast_forward()
    {
        var same = GitMirrorRefReconciler.Compare(Request(
            [new GitMirrorRef("refs/heads/main", new string('A', 40))],
            [new GitMirrorRef("refs/heads/main", new string('a', 40))]));
        Assert.Equal(1, same.MatchingCount);
        Assert.Equal(new string('a', 40), same.Items[0].SourceObjectId);
        Assert.False(same.ReadyForExecution);

        var changed = GitMirrorRefReconciler.Compare(Request(
            [new GitMirrorRef("refs/heads/main", OidA)],
            [new GitMirrorRef("refs/heads/main", OidB)]));
        Assert.Equal(1, changed.DivergedCount);
        Assert.Equal(GitMirrorRefDisposition.DivergedBlocked,
            changed.Items[0].Disposition);
        Assert.False(changed.ReadyForExecution);
    }

    [Fact]
    public void Every_evidence_digest_and_ref_object_id_is_included_in_a_stable_sha256()
    {
        var baseline = Request(
            [new GitMirrorRef("refs/heads/main", OidA)],
            [new GitMirrorRef("refs/heads/main", OidA)]);
        var original = GitMirrorRefReconciler.Compare(baseline).ReconciliationSha256;

        Assert.NotEqual(original, GitMirrorRefReconciler.Compare(
            baseline with { OperationManifestSha256 = TargetProof }).ReconciliationSha256);
        Assert.NotEqual(original, GitMirrorRefReconciler.Compare(
            baseline with { SourceRefEvidenceSha256 = PlanProof }).ReconciliationSha256);
        Assert.NotEqual(original, GitMirrorRefReconciler.Compare(
            baseline with { TargetRefEvidenceSha256 = PlanProof }).ReconciliationSha256);
        Assert.NotEqual(original, GitMirrorRefReconciler.Compare(
            baseline with { OperationPlanId = Guid.NewGuid() }).ReconciliationSha256);
        Assert.NotEqual(original, GitMirrorRefReconciler.Compare(
            baseline with { SourceRefs = [new GitMirrorRef("refs/heads/main", OidB)] })
            .ReconciliationSha256);
        Assert.NotEqual(original, GitMirrorRefReconciler.Compare(
            baseline with { SourceRefs = [new GitMirrorRef("refs/heads/feature", OidA)] })
            .ReconciliationSha256);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("refs/pull/1/head")]
    [InlineData("refs/heads/")]
    [InlineData("refs/heads/a//b")]
    [InlineData("refs/heads/.hidden")]
    [InlineData("refs/heads/a.lock")]
    [InlineData("refs/heads/a..b")]
    [InlineData("refs/heads/a@{b")]
    [InlineData("refs/heads/a b")]
    [InlineData("refs/heads/a?b")]
    [InlineData("refs/heads/a\\b")]
    [InlineData("refs/heads/a.")]
    [InlineData("refs/heads/-bad")]
    public void Rejects_invalid_or_unsupported_names_before_planning(string name)
    {
        var error = Assert.Throws<GitMirrorRefReconciliationException>(() =>
            GitMirrorRefReconciler.Compare(Request(
                [new GitMirrorRef(name, OidA)], [])));
        Assert.Equal("InvalidSourceRef", error.Code);
    }

    [Fact]
    public void Rejects_duplicate_case_aliases_on_each_side_and_across_source_and_target()
    {
        var duplicate = Assert.Throws<GitMirrorRefReconciliationException>(() =>
            GitMirrorRefReconciler.Compare(Request(
                [new GitMirrorRef("refs/heads/main", OidA),
                 new GitMirrorRef("refs/heads/MAIN", OidA)], [])));
        Assert.Equal("DuplicateSourceRef", duplicate.Code);
        var targetDuplicate = Assert.Throws<GitMirrorRefReconciliationException>(() =>
            GitMirrorRefReconciler.Compare(Request([], [
                new GitMirrorRef("refs/tags/V1", OidA),
                new GitMirrorRef("refs/tags/v1", OidA)])));
        Assert.Equal("DuplicateTargetRef", targetDuplicate.Code);
        var crossSide = Assert.Throws<GitMirrorRefReconciliationException>(() =>
            GitMirrorRefReconciler.Compare(Request(
                [new GitMirrorRef("refs/heads/Main", OidA)],
                [new GitMirrorRef("refs/heads/main", OidA)])));
        Assert.Equal("RefCaseCollision", crossSide.Code);
    }

    [Fact]
    public void Fails_closed_for_malformed_or_mixed_git_object_formats()
    {
        var malformed = Assert.Throws<GitMirrorRefReconciliationException>(() =>
            GitMirrorRefReconciler.Compare(Request(
                [new GitMirrorRef("refs/heads/main", "not-an-object-id")], [])));
        Assert.Equal("InvalidSourceRef", malformed.Code);

        var mixed = Assert.Throws<GitMirrorRefReconciliationException>(() =>
            GitMirrorRefReconciler.Compare(Request(
                [new GitMirrorRef("refs/heads/main", OidA)],
                [new GitMirrorRef("refs/tags/v1", new string('a', 64))])));
        Assert.Equal("RefObjectFormatMismatch", mixed.Code);
    }

    [Fact]
    public void Requires_exact_plan_and_observation_proofs_and_explicit_bounds()
    {
        var bad = Request([], []) with { OperationManifestSha256 = "NOT-A-DIGEST" };
        Assert.Equal("RefEvidenceInvalid",
            Assert.Throws<GitMirrorRefReconciliationException>(() =>
                GitMirrorRefReconciler.Compare(bad)).Code);
        Assert.Equal("RefEvidenceInvalid",
            Assert.Throws<GitMirrorRefReconciliationException>(() =>
                GitMirrorRefReconciler.Compare(Request([], []) with {
                    OperationPlanId = Guid.Empty
                })).Code);

        var tooMany = Enumerable.Range(0, GitMirrorRefReconciler.MaxRefsPerSide + 1)
            .Select(i => new GitMirrorRef($"refs/heads/branch{i}", OidA))
            .ToArray();
        Assert.Equal("RefSelectionLimit",
            Assert.Throws<GitMirrorRefReconciliationException>(() =>
                GitMirrorRefReconciler.Compare(Request(tooMany, []))).Code);
    }

    [Fact]
    public void Empty_or_all_equal_observations_never_become_executable()
    {
        var empty = GitMirrorRefReconciler.Compare(Request([], []));
        Assert.Empty(empty.Items);
        Assert.False(empty.ReadyForExecution);
        Assert.Equal(64, empty.ReconciliationSha256.Length);

        var equal = GitMirrorRefReconciler.Compare(Request(
            [new GitMirrorRef("refs/heads/main", OidA)],
            [new GitMirrorRef("refs/heads/main", OidA)]));
        Assert.Equal(1, equal.MatchingCount);
        Assert.False(equal.ReadyForExecution);
    }

    [Fact]
    public void Thousands_of_refs_are_reconciled_without_quadratic_matching()
    {
        const int count = 3_000;
        var source = Enumerable.Range(0, count).Select(i =>
            new GitMirrorRef($"refs/heads/feature/{i:D5}", OidA)).ToArray();
        var target = source.Take(count / 2).Reverse().ToArray();
        var result = GitMirrorRefReconciler.Compare(Request(source, target));
        Assert.Equal(count, result.SourceRefCount);
        Assert.Equal(count / 2, result.TargetRefCount);
        Assert.Equal(count / 2, result.MatchingCount);
        Assert.Equal(count / 2, result.CreateCandidateCount);
        Assert.False(result.ReadyForExecution);
        Assert.Equal(count, result.Items.Count);
    }
}
