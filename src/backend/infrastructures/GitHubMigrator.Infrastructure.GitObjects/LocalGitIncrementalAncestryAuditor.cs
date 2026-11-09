using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// F-094 is strictly OFFLINE and opt-in. The repositories must be
// deployment-owned GUID-addressed, standalone local bare mirrors. This
// adapter cannot access GitHub credentials, remote URLs or push transports.
public sealed class LocalGitIncrementalAncestryAuditor(
    string sourceRoot, string targetRoot, bool enabled = false,
    TimeProvider? clock = null) : ILocalGitIncrementalAncestryAuditor
{
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<GitIncrementalAncestryReceipt> AuditAsync(
        GitIncrementalAncestryRequest request, CancellationToken ct)
    {
        if (!enabled)
            throw new GitIncrementalAncestryException("GitAncestryDisabled");
        var (sourceRefs, targetRefs, format) =
            GitIncrementalAncestryIntegrity.VerifyRequest(request);

        await Gate.WaitAsync(ct);
        try
        {
            // SafeBarePath rejects symlinked repository roots, refs and
            // objects, alternate storage, grafts, shallow, and promisor.
            var source = LocalGitObjectClosureAuditor.SafeBarePath(
                sourceRoot, request.SourceSnapshotId);
            var target = LocalGitObjectClosureAuditor.SafeBarePath(
                targetRoot, request.TargetSandboxId);
            if (string.Equals(source, target, StringComparison.Ordinal))
                throw new GitIncrementalAncestryException(
                    "GitAncestrySameRepository");

            // F-056 complete fsck + SHA/object-root + exact ref/OID checks
            // on BOTH repos. Never infer an absent target commit is divergent:
            // it may exist only in the target's independent history.
            var sourceSet = await LocalGitObjectClosureAuditor.VerifyOneAsync(
                sourceRoot, request.SourceSnapshotId, sourceRefs, ct);
            var targetSet = await LocalGitObjectClosureAuditor.VerifyOneAsync(
                targetRoot, request.TargetSandboxId, targetRefs, ct);
            var bySource = sourceRefs.ToDictionary(x => x.Name,
                x => x.ObjectId, StringComparer.Ordinal);
            var byTarget = targetRefs.ToDictionary(x => x.Name,
                x => x.ObjectId, StringComparer.Ordinal);

            var decisions = new List<GitIncrementalAncestryItem>(
                sourceRefs.Count + targetRefs.Count);
            foreach (var name in bySource.Keys.Concat(byTarget.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                bySource.TryGetValue(name, out var src);
                byTarget.TryGetValue(name, out var dst);
                var status = GitIncrementalAncestryStatus.MatchingObjectId;
                if (src is not null &&
                    name.StartsWith("refs/heads/", StringComparison.Ordinal) &&
                    !await LocalGitObjectClosureAuditor.HasCommitAsync(
                        source, src, ct))
                    throw new GitIncrementalAncestryException(
                        "GitAncestryBranchNotCommit");
                if (dst is not null &&
                    name.StartsWith("refs/heads/", StringComparison.Ordinal) &&
                    !await LocalGitObjectClosureAuditor.HasCommitAsync(
                        target, dst, ct))
                    throw new GitIncrementalAncestryException(
                        "GitAncestryBranchNotCommit");

                if (src is null)
                    status = GitIncrementalAncestryStatus.TargetOnlyPreserved;
                else if (dst is null)
                    status = GitIncrementalAncestryStatus.CreateCandidateBlocked;
                else if (src == dst)
                    status = GitIncrementalAncestryStatus.MatchingObjectId;
                else if (name.StartsWith("refs/tags/", StringComparison.Ordinal))
                    status = GitIncrementalAncestryStatus.TagChangeBlocked;
                else if (!await LocalGitObjectClosureAuditor.HasCommitAsync(
                    source, dst, ct))
                    status = GitIncrementalAncestryStatus.HistoryUnavailableBlocked;
                else if (await LocalGitObjectClosureAuditor.IsAncestorAsync(
                    source, dst, src, ct))
                    status = GitIncrementalAncestryStatus.FastForwardCandidateBlocked;
                else if (await LocalGitObjectClosureAuditor.IsAncestorAsync(
                    source, src, dst, ct))
                    status = GitIncrementalAncestryStatus.TargetAheadPreserved;
                else
                    status = GitIncrementalAncestryStatus.DivergedBlocked;
                decisions.Add(new(name, src, dst, status));
            }

            // Repeat all ref/closure checks so an in-flight local writer
            // invalidates the evidence, even when the final name set is
            // unchanged but a referenced object disappears or is replaced.
            var sourceAfter = await LocalGitObjectClosureAuditor.VerifyOneAsync(
                sourceRoot, request.SourceSnapshotId, sourceRefs, ct);
            var targetAfter = await LocalGitObjectClosureAuditor.VerifyOneAsync(
                targetRoot, request.TargetSandboxId, targetRefs, ct);
            if (sourceSet != sourceAfter || targetSet != targetAfter)
                throw new GitIncrementalAncestryException(
                    "GitAncestryChangedDuringAudit");

            var receipt = GitIncrementalAncestryIntegrity.Seal(
                request, format, sourceSet.Digest, targetSet.Digest,
                sourceSet.Count, targetSet.Count, decisions, _clock.GetUtcNow());
            GitIncrementalAncestryIntegrity.VerifyReceipt(receipt);
            return receipt;
        }
        catch (GitObjectClosureAuditException)
        {
            throw new GitIncrementalAncestryException(
                "GitAncestryLocalEvidenceRejected");
        }
        catch (IOException)
        {
            throw new GitIncrementalAncestryException(
                "GitAncestryLocalIoFailure");
        }
        catch (UnauthorizedAccessException)
        {
            throw new GitIncrementalAncestryException(
                "GitAncestryLocalIoFailure");
        }
        finally { Gate.Release(); }
    }
}
