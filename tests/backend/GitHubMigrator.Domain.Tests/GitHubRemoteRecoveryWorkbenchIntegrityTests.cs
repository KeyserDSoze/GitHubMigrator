using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteRecoveryWorkbenchIntegrityTests
{
    private static string Sha(char c) => new(c, 64);

    private static GitHubRemoteRecoveryWorkbenchItem Waiting(bool typed = true) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2468, "gh-org", "sandbox",
            DateTimeOffset.UtcNow,
            GitHubRemotePostSendRecoveryStates.AwaitingIndependentReadback,
            typed
                ? GitHubRemoteRecoveryWorkbenchActions.ObserveTips
                : GitHubRemoteRecoveryWorkbenchActions.OperatorReview,
            true, typed, typed ? 2 : null, typed ? 1 : null,
            Sha('a'), typed ? Sha('b') : null,
            null, null, null, null, null, null, null,
            false, false, false, false);

    [Fact]
    public void F088_all_states_have_constrained_next_actions()
    {
        var cases = new[] {
            (GitHubRemotePostSendRecoveryStates.AwaitingIndependentReadback,
                true, GitHubRemoteRecoveryWorkbenchActions.ObserveTips),
            (GitHubRemotePostSendRecoveryStates.AwaitingIndependentReadback,
                false, GitHubRemoteRecoveryWorkbenchActions.OperatorReview),
            (GitHubRemotePostSendRecoveryStates.TipsObservedNeedsClosure,
                true, GitHubRemoteRecoveryWorkbenchActions.VerifyFullObjects),
            (GitHubRemotePostSendRecoveryStates.OperatorReviewRequired,
                true, GitHubRemoteRecoveryWorkbenchActions.OperatorReview),
            (GitHubRemotePostSendRecoveryStates.IndependentClosureObserved,
                true, GitHubRemoteRecoveryWorkbenchActions.CertifyClosure),
            (GitHubRemotePostSendRecoveryStates.CheckpointedIndependentClosure,
                true, GitHubRemoteRecoveryWorkbenchActions.None)
        };
        foreach (var (state, hasTyped, action) in cases)
            Assert.Equal(action,
                GitHubRemoteRecoveryWorkbenchIntegrity.NextAction(state, hasTyped));
        Assert.Throws<GitHubRemoteRecoveryWorkbenchException>(() =>
            GitHubRemoteRecoveryWorkbenchIntegrity.NextAction(
                "RemotePushSucceeded", true));
    }

    [Fact]
    public void F088_does_not_treat_F082_as_sent_or_auto_retryable()
    {
        var row = Waiting();
        GitHubRemoteRecoveryWorkbenchIntegrity.RequireSafe(row);
        Assert.Equal(GitHubRemoteRecoveryWorkbenchActions.ObserveTips,
            row.RecommendedNextAction);
        foreach (var forged in new[] {
            row with { CanPush = true },
            row with { CanRetry = true },
            row with { CanDispatch = true },
            row with { MigrationComplete = true },
            row with { BarrierId = Guid.Empty },
            row with { RepositoryId = 0 },
            row with { RecommendedNextAction =
                GitHubRemoteRecoveryWorkbenchActions.CertifyClosure }
        })
            Assert.Throws<GitHubRemoteRecoveryWorkbenchException>(() =>
                GitHubRemoteRecoveryWorkbenchIntegrity.RequireSafe(forged));
    }

    [Fact]
    public void F088_full_closure_without_proof_and_checkpoint_without_sha_are_rejected()
    {
        var row = Waiting();
        var closure = row with {
            State = GitHubRemotePostSendRecoveryStates.IndependentClosureObserved,
            RecommendedNextAction =
                GitHubRemoteRecoveryWorkbenchActions.CertifyClosure
        };
        Assert.Throws<GitHubRemoteRecoveryWorkbenchException>(() =>
            GitHubRemoteRecoveryWorkbenchIntegrity.RequireSafe(closure));
        var checkedClosure = closure with {
            ReconciliationId = Guid.NewGuid(),
            FullObjectClosureSha256 = Sha('c')
        };
        GitHubRemoteRecoveryWorkbenchIntegrity.RequireSafe(checkedClosure);
        var checkpointed = checkedClosure with {
            State = GitHubRemotePostSendRecoveryStates.CheckpointedIndependentClosure,
            RecommendedNextAction = GitHubRemoteRecoveryWorkbenchActions.None,
            ClosureCheckpointId = Guid.NewGuid()
        };
        Assert.Throws<GitHubRemoteRecoveryWorkbenchException>(() =>
            GitHubRemoteRecoveryWorkbenchIntegrity.RequireSafe(checkpointed));
        GitHubRemoteRecoveryWorkbenchIntegrity.RequireSafe(
            checkpointed with { ClosureCheckpointEvidenceSha256 = Sha('d') });
    }
}
