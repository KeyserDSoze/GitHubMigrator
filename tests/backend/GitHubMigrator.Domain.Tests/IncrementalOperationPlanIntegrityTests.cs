using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class IncrementalOperationPlanIntegrityTests
{
    [Fact]
    public void Produces_a_deterministic_blocked_DAG_independent_of_source_mapping_order()
    {
        var mappings = new[] {
            new IncrementalTargetProbe("repo-b", "target-b", "GitMirror", "NotVisibleOrMissing"),
            new IncrementalTargetProbe("repo-a", "target-a", "GitMirror", "Exists")
        };
        var changed = new Dictionary<string, int> { ["repo-a"] = 602, ["repo-b"] = 0 };
        var ordered = IncrementalOperationPlanIntegrity.BuildSteps(mappings, changed);
        var reversed = IncrementalOperationPlanIntegrity.BuildSteps(mappings.Reverse().ToArray(), changed);
        Assert.Equal(12, ordered.Count);
        Assert.Equal(ordered.ToArray(), reversed.ToArray());
        Assert.All(ordered, x => Assert.Equal("Blocked", x.State));
        Assert.Equal("repo-a", ordered[0].SourceRepositoryId);
        Assert.Equal("repo-b", ordered[6].SourceRepositoryId);
        Assert.Equal(602, ordered[0].ChangedRefs);
        Assert.Equal(0, ordered[6].ChangedRefs);
        Assert.Null(ordered[0].DependsOnOrdinal);
        Assert.Equal(1, ordered[1].DependsOnOrdinal);
        Assert.Null(ordered[6].DependsOnOrdinal);
        Assert.Equal(7, ordered[7].DependsOnOrdinal);
        Assert.Equal("ReviewTargetName", ordered[0].Kind);
        Assert.Equal("ReviewSecurityMapping", ordered[1].Kind);
        Assert.Equal("TransferGitHistory", ordered[2].Kind);
        Assert.Equal("ReconcileGitRefs", ordered[3].Kind);
        Assert.Equal("VerifyTransferredRefs", ordered[4].Kind);
        Assert.Equal("FinalizeMigrationAudit", ordered[5].Kind);
    }

    [Fact]
    public void Any_change_to_evidence_dependency_or_mapping_changes_SHA()
    {
        var repo = new IncrementalTargetProbe(
            "source", "destination", "GitMirror", "NotVisibleOrMissing");
        var original = IncrementalOperationPlanIntegrity.BuildSteps(
            [repo], new Dictionary<string, int> { ["source"] = 1 });
        var prep = Guid.NewGuid(); var snapshot = Guid.NewGuid();
        var source = Guid.NewGuid(); var destination = Guid.NewGuid();
        const string v1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string v2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        string Hash(string proof, IReadOnlyList<IncrementalOperationStep> steps) =>
            IncrementalOperationPlanIntegrity.Hash(
                prep, snapshot, source, destination, "org",
                v1, v2, proof, 0, steps);

        var h = Hash(v1, original);
        Assert.Equal(64, h.Length);
        Assert.Equal(h, Hash(v1, original));
        Assert.NotEqual(h, Hash(v2, original));
        Assert.NotEqual(h, Hash(v1,
            IncrementalOperationPlanIntegrity.BuildSteps(
                [repo], new Dictionary<string, int> { ["source"] = 2 })));
        var changed = original.ToArray();
        changed[3] = changed[3] with { DependsOnOrdinal = null };
        Assert.NotEqual(h, Hash(v1, changed));
        Assert.NotEqual(h, IncrementalOperationPlanIntegrity.Hash(
            prep, snapshot, source, destination, "another-org",
            v1, v2, v1, 0, original));
        Assert.NotEqual(h, IncrementalOperationPlanIntegrity.Hash(
            prep, snapshot, source, destination, "org",
            v1, v2, v1, 1, original));
    }

    [Fact]
    public void Rejects_unsupported_history_strategies_duplicate_targets_and_unbounded_selection()
    {
        var first = new IncrementalTargetProbe("repo1", "target", "TipOnly", "NotVisibleOrMissing");
        var ex = Assert.Throws<IncrementalOperationPlanException>(() =>
            IncrementalOperationPlanIntegrity.BuildSteps([first],
                new Dictionary<string, int>()));
        Assert.Equal("OperationHistoryStrategyUnsupported", ex.Code);

        var a = first with { HistoryStrategy = "GitMirror" };
        var b = new IncrementalTargetProbe("repo2", "TARGET", "GitMirror", "NotVisibleOrMissing");
        Assert.Equal("OperationMappingInvalid",
            Assert.Throws<IncrementalOperationPlanException>(() =>
                IncrementalOperationPlanIntegrity.BuildSteps([a, b],
                    new Dictionary<string, int>())).Code);
        Assert.Equal("OperationMappingInvalid",
            Assert.Throws<IncrementalOperationPlanException>(() =>
                IncrementalOperationPlanIntegrity.BuildSteps(
                    Enumerable.Range(0, 11)
                        .Select(i => new IncrementalTargetProbe(
                            $"repo{i}", $"target{i}", "GitMirror", "NotVisibleOrMissing"))
                        .ToArray(), new Dictionary<string, int>())).Code);
    }

    [Fact]
    public void Even_all_hidden_or_missing_repositories_never_count_as_write_authorization()
    {
        var preflight = new IncrementalTargetPreflightView(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "org",
            new string('a', 64), new string('b', 64), DateTimeOffset.UtcNow,
            1, "Unverified", "Active", "Owner", "AllowedForMembers",
            1, 0, 1, true, false, false, false, true,
            [new IncrementalTargetProbe("source", "maybe-free",
                "GitMirror", "NotVisibleOrMissing")]);
        var blockers = IncrementalOperationPlanIntegrity.Blockers(preflight, 0);
        Assert.Contains("TargetAvailabilityUnverified", blockers);
        Assert.Contains("DestinationWritePermissionUnverified", blockers);
        Assert.Contains("ExecutionAdapterNotImplemented", blockers);
    }
}
