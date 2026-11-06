using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-056: an independent OFFLINE proof for two trusted local bare repositories.
// It verifies reachable Git objects, not GitHub's remote object store.
public sealed record GitObjectClosureAuditRequest(
    Guid SourceSnapshotId, Guid TargetSandboxId,
    IReadOnlyList<GitMirrorRef> ExpectedSourceRefs,
    IReadOnlyList<GitMirrorRef> ExpectedTargetRefs);

public sealed record GitObjectClosureAuditReceipt(
    Guid SourceSnapshotId, Guid TargetSandboxId,
    string GitObjectFormat, string SourceRefSetSha256,
    string TargetRefSetSha256, string SourceObjectSetSha256,
    string TargetObjectSetSha256, int ReachableObjectCount,
    string EvidenceSha256, DateTimeOffset VerifiedAtUtc,
    bool TransportInvoked, bool RemoteWritesAuthorized,
    bool RemoteMigrationVerified);

public sealed class GitObjectClosureAuditException(string code)
    : Exception("Offline Git object closure verification refused")
{
    public string Code { get; } = code;
}

public interface ILocalGitObjectClosureAuditor
{
    Task<GitObjectClosureAuditReceipt> AuditAsync(
        GitObjectClosureAuditRequest request, CancellationToken ct);
}

public static class GitObjectClosureAuditIntegrity
{
    // Hard resource bound. A paginated/streamed scalable implementation
    // must be added for repositories exceeding this local proof limit.
    public const int MaxObjects = 200_000;
    public const int MaxRefs = 25_000;

    public static (IReadOnlyList<GitMirrorRef> Source,
        IReadOnlyList<GitMirrorRef> Target, string Format) Verify(
        GitObjectClosureAuditRequest request)
    {
        if (request is null || request.SourceSnapshotId == Guid.Empty ||
            request.TargetSandboxId == Guid.Empty ||
            request.SourceSnapshotId == request.TargetSandboxId ||
            request.ExpectedSourceRefs is null ||
            request.ExpectedTargetRefs is null ||
            request.ExpectedSourceRefs.Count is < 1 or > MaxRefs ||
            request.ExpectedTargetRefs.Count is < 1 or > MaxRefs)
            throw new GitObjectClosureAuditException("GitClosureRequestInvalid");

        IReadOnlyList<GitMirrorRef> source;
        IReadOnlyList<GitMirrorRef> target;
        try
        {
            source = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                request.ExpectedSourceRefs);
            target = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                request.ExpectedTargetRefs);
        }
        catch (GitMirrorRefReconciliationException)
        {
            throw new GitObjectClosureAuditException("GitClosureRefInvalid");
        }
        if (!source.SequenceEqual(request.ExpectedSourceRefs) ||
            !target.SequenceEqual(request.ExpectedTargetRefs))
            throw new GitObjectClosureAuditException("GitClosureRefOrderInvalid");
        var len = source[0].ObjectId.Length;
        if (len is not (40 or 64) ||
            source.Concat(target).Any(x => x.ObjectId.Length != len))
            throw new GitObjectClosureAuditException("GitClosureObjectFormatInvalid");

        var targetByName = target.ToDictionary(x => x.Name,
            x => x.ObjectId, StringComparer.Ordinal);
        // The target may retain unrelated refs. Every selected source ref
        // must have exactly the same OID in the target, never force-update.
        if (source.Any(x => !targetByName.TryGetValue(x.Name, out var oid) ||
            oid != x.ObjectId))
            throw new GitObjectClosureAuditException("GitClosureRefNotTransferred");

        return (source, target, len == 40 ? "sha1" : "sha256");
    }

    public static GitObjectClosureAuditReceipt Seal(
        GitObjectClosureAuditRequest request, string format,
        string sourceObjectSetSha, string targetObjectSetSha,
        int sourceCount, int targetCount, DateTimeOffset observedAtUtc)
    {
        var (source, target, expectedFormat) = Verify(request);
        if (format != expectedFormat ||
            !GitSourceBundleStorageCheck.ValidSha(sourceObjectSetSha) ||
            !GitSourceBundleStorageCheck.ValidSha(targetObjectSetSha) ||
            sourceCount <= 0 || sourceCount > MaxObjects ||
            targetCount <= 0 || targetCount > MaxObjects ||
            sourceCount != targetCount ||
            sourceObjectSetSha != targetObjectSetSha ||
            observedAtUtc == default)
            throw new GitObjectClosureAuditException("GitClosureMismatch");

        var sourceRefsSha = GitImportRehearsalIntegrity.SealRefs(
            "f056-source-refs-v1", source);
        var targetRefsSha = GitImportRehearsalIntegrity.SealRefs(
            "f056-target-refs-v1", target);
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] {
                "f056-offline-object-closure-v1",
                request.SourceSnapshotId.ToString("D"),
                request.TargetSandboxId.ToString("D"),
                format, sourceRefsSha, targetRefsSha,
                sourceObjectSetSha, targetObjectSetSha,
                sourceCount, false, false, false
            }))));
        return new(request.SourceSnapshotId, request.TargetSandboxId,
            format, sourceRefsSha, targetRefsSha,
            sourceObjectSetSha, targetObjectSetSha, sourceCount,
            digest, observedAtUtc, false, false, false);
    }
}
