using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-030 is a bounded, read-only comparison of proven ref observations.
// It does not fetch Git objects, establish fast-forward ancestry, create
// a GitHub repository, produce a push command or grant execution authority.
public sealed record GitMirrorRef(string Name, string ObjectId);

public enum GitMirrorRefDisposition
{
    MatchingObjectId,
    CreateCandidate,
    DivergedBlocked,
    TargetOnlyPreserved
}

public sealed record GitMirrorRefComparison(
    string Name, string? SourceObjectId, string? TargetObjectId,
    GitMirrorRefDisposition Disposition, string BlockerCode);

public sealed record GitMirrorRefReconciliationRequest(
    Guid OperationPlanId, string OperationManifestSha256,
    string SourceRefEvidenceSha256, string TargetRefEvidenceSha256,
    IReadOnlyList<GitMirrorRef> SourceRefs,
    IReadOnlyList<GitMirrorRef> TargetRefs);

public sealed record GitMirrorRefReconciliation(
    Guid OperationPlanId, string ReconciliationSha256,
    int SourceRefCount, int TargetRefCount,
    int MatchingCount, int CreateCandidateCount,
    int DivergedCount, int TargetOnlyCount,
    bool ReadyForExecution,
    IReadOnlyList<GitMirrorRefComparison> Items);

public sealed class GitMirrorRefReconciliationException(string code)
    : Exception("Git mirror ref comparison rejected")
{
    public string Code { get; } = code;
}

public static class GitMirrorRefReconciler
{
    // An explicit resource bound; a later paged persistence/streaming adapter
    // is required for repositories larger than this in-memory comparison.
    public const int MaxRefsPerSide = 25_000;

    public static GitMirrorRefReconciliation Compare(
        GitMirrorRefReconciliationRequest request)
    {
        if (request is null || request.OperationPlanId == Guid.Empty ||
            !ValidDigest(request.OperationManifestSha256) ||
            !ValidDigest(request.SourceRefEvidenceSha256) ||
            !ValidDigest(request.TargetRefEvidenceSha256))
            throw new GitMirrorRefReconciliationException("RefEvidenceInvalid");

        if (request.SourceRefs is null || request.TargetRefs is null ||
            request.SourceRefs.Count > MaxRefsPerSide ||
            request.TargetRefs.Count > MaxRefsPerSide)
            throw new GitMirrorRefReconciliationException("RefSelectionLimit");

        var source = Validate(request.SourceRefs, "Source");
        var target = Validate(request.TargetRefs, "Target");

        // Git's ref namespace is case-sensitive, but target host/filesystem
        // behavior may differ. Never propose two distinct case aliases.
        var caseAliases = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var name in source.Keys.Concat(target.Keys))
        {
            if (caseAliases.TryGetValue(name, out var prior) &&
                !string.Equals(name, prior, StringComparison.Ordinal))
                throw new GitMirrorRefReconciliationException("RefCaseCollision");
            caseAliases[name] = name;
        }

        // SHA-1 and SHA-256 object IDs are not interchangeable. A mixed
        // object-format observation is not a safe migration comparison.
        if (source.Values.Concat(target.Values)
            .Select(oid => oid.Length).Distinct().Take(2).Count() > 1)
            throw new GitMirrorRefReconciliationException("RefObjectFormatMismatch");

        var allNames = source.Keys.Concat(target.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);
        var items = new List<GitMirrorRefComparison>(
            Math.Min(source.Count + target.Count, 2 * MaxRefsPerSide));

        var matching = 0;
        var creates = 0;
        var diverged = 0;
        var targetOnly = 0;
        foreach (var name in allNames)
        {
            source.TryGetValue(name, out var sourceOid);
            target.TryGetValue(name, out var targetOid);
            GitMirrorRefDisposition disposition;
            string blocker;
            if (sourceOid is null)
            {
                disposition = GitMirrorRefDisposition.TargetOnlyPreserved;
                blocker = "TargetRefDeletionProhibited";
                targetOnly++;
            }
            else if (targetOid is null)
            {
                disposition = GitMirrorRefDisposition.CreateCandidate;
                blocker = "DestinationWritePermissionUnverified";
                creates++;
            }
            else if (string.Equals(sourceOid, targetOid, StringComparison.Ordinal))
            {
                disposition = GitMirrorRefDisposition.MatchingObjectId;
                blocker = "None";
                matching++;
            }
            else
            {
                // Never interpret a changed object ID as fast-forward proof.
                disposition = GitMirrorRefDisposition.DivergedBlocked;
                blocker = "RefFastForwardUnverified";
                diverged++;
            }
            items.Add(new(name, sourceOid, targetOid, disposition, blocker));
        }

        // Incremental hashing avoids building a second, potentially large
        // serialized copy. Every proof, full ref name, object ID and decision
        // is bound to the deterministic, versioned SHA-256.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(new object?[] {
            "git-mirror-ref-reconciliation-v1",
            request.OperationPlanId.ToString("D"),
            request.OperationManifestSha256,
            request.SourceRefEvidenceSha256,
            request.TargetRefEvidenceSha256,
            source.Count, target.Count, matching, creates, diverged, targetOnly
        });
        foreach (var item in items)
            Append(new object?[] {
                item.Name, item.SourceObjectId, item.TargetObjectId,
                item.Disposition.ToString(), item.BlockerCode
            });
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        // All comparison results remain non-executable, including a perfect
        // match or an empty inventory. No API may use this flag as a permit.
        return new(request.OperationPlanId, digest,
            source.Count, target.Count, matching, creates, diverged, targetOnly,
            false, items);

        void Append(object?[] fields)
        {
            var json = JsonSerializer.Serialize(fields) + "\n";
            hash.AppendData(Encoding.UTF8.GetBytes(json));
        }
    }

    private static Dictionary<string, string> Validate(
        IReadOnlyList<GitMirrorRef> refs, string side)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var caseUnique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in refs)
        {
            if (reference is null || !ValidRefName(reference.Name) ||
                !ValidObjectId(reference.ObjectId))
                throw new GitMirrorRefReconciliationException("Invalid" + side + "Ref");
            if (!caseUnique.Add(reference.Name))
                throw new GitMirrorRefReconciliationException("Duplicate" + side + "Ref");
            result.Add(reference.Name, reference.ObjectId.ToLowerInvariant());
        }
        return result;
    }

    private static bool ValidDigest(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidObjectId(string? value) =>
        value is not null && (value.Length == 40 || value.Length == 64) &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'
            or >= 'A' and <= 'F');

    // Only fully qualified branch and tag refs are within F-030's declared
    // scope. Symbolic HEAD, remote-tracking refs, GitHub pull refs and notes
    // must not silently enter a branch/tag migration reconciliation.
    private static bool ValidRefName(string? name)
    {
        if (name is null || Encoding.UTF8.GetByteCount(name) > 255 ||
            !(name.StartsWith("refs/heads/", StringComparison.Ordinal) ||
              name.StartsWith("refs/tags/", StringComparison.Ordinal)) ||
            name.Contains("..", StringComparison.Ordinal) ||
            name.Contains("@{", StringComparison.Ordinal) ||
            name.EndsWith(".", StringComparison.Ordinal))
            return false;

        foreach (var segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment.StartsWith(".", StringComparison.Ordinal) ||
                segment.StartsWith("-", StringComparison.Ordinal) ||
                segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var c in name)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) ||
                char.IsSurrogate(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
                return false;
        }
        return true;
    }
}
