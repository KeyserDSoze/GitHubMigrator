using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitHubMigrator.Business;

// F-094: offline, read-only ancestry inspection for two deployment-owned
// local *bare* repositories. This is not a GitHub remote object attestation
// and does not provide any write or retry authority.
public sealed record GitIncrementalAncestryRequest(
    Guid SourceSnapshotId, Guid TargetSandboxId, Guid SourceCheckpointId,
    string SourceCheckpointAttestationSha256, Guid TargetObservationId,
    string TargetObservationSha256,
    IReadOnlyList<GitMirrorRef> ExpectedSourceRefs,
    IReadOnlyList<GitMirrorRef> ExpectedTargetRefs,
    // F-096 binds F-094 to the F-060 physical, workpack-scoped mirror.
    // Two optional fields stay null for historical F-094 v1 receipts.
    Guid? SourcePhysicalMirrorId = null,
    string? SourcePhysicalMirrorProofSha256 = null,
    // F-097c: only for F-060-bound source mirrors; null preserves the
    // exact legacy F-094/F-096 v1/v2 payload and historical receipt hash.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceCheckpointGitRefProofSha256 = null);

public enum GitIncrementalAncestryStatus
{
    MatchingObjectId,
    CreateCandidateBlocked,
    TargetOnlyPreserved,
    FastForwardCandidateBlocked,
    TargetAheadPreserved,
    DivergedBlocked,
    HistoryUnavailableBlocked,
    TagChangeBlocked
}

public sealed record GitIncrementalAncestryItem(
    string Name, string? SourceObjectId, string? TargetObjectId,
    GitIncrementalAncestryStatus Status);

public sealed record GitIncrementalAncestryReceipt(
    GitIncrementalAncestryRequest Request, string GitObjectFormat,
    string SourceObjectSetSha256, string TargetObjectSetSha256,
    int SourceReachableObjectCount, int TargetReachableObjectCount,
    IReadOnlyList<GitIncrementalAncestryItem> Items,
    string EvidenceSha256, DateTimeOffset VerifiedAtUtc,
    bool RemoteTransportInvoked, bool CanPush,
    bool CanRetry, bool MigrationComplete);

public sealed class GitIncrementalAncestryException(string code)
    : Exception("Offline incremental Git ancestry inspection refused")
{
    public string Code { get; } = code;
}

public interface ILocalGitIncrementalAncestryAuditor
{
    Task<GitIncrementalAncestryReceipt> AuditAsync(
        GitIncrementalAncestryRequest request, CancellationToken ct);
}

public static class GitIncrementalAncestryIntegrity
{
    // Deliberately much smaller than F-056's 25k ref audit: this first
    // local status engine invokes bounded Git merge-base per changed branch.
    public const int MaxRefsPerSide = 128;

    private static GitIncrementalAncestryException Invalid() =>
        new("GitAncestryEvidenceInvalid");

    private static bool Sha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static (IReadOnlyList<GitMirrorRef> Source,
        IReadOnlyList<GitMirrorRef> Target, string Format) VerifyRequest(
        GitIncrementalAncestryRequest r)
    {
        if (r is null || r.SourceSnapshotId == Guid.Empty ||
            r.TargetSandboxId == Guid.Empty || r.SourceCheckpointId == Guid.Empty ||
            r.TargetObservationId == Guid.Empty ||
            r.SourceSnapshotId == r.TargetSandboxId ||
            (r.SourcePhysicalMirrorId is not null &&
                (r.SourcePhysicalMirrorId == Guid.Empty ||
                 r.SourcePhysicalMirrorId == r.TargetSandboxId ||
                 r.SourcePhysicalMirrorId == r.SourceSnapshotId)) ||
            (r.SourcePhysicalMirrorId is null) !=
                (r.SourcePhysicalMirrorProofSha256 is null) ||
            (r.SourcePhysicalMirrorProofSha256 is not null &&
                !Sha(r.SourcePhysicalMirrorProofSha256)) ||
            (r.SourceCheckpointGitRefProofSha256 is not null &&
                (r.SourcePhysicalMirrorId is null ||
                 !Sha(r.SourceCheckpointGitRefProofSha256))) ||
            !Sha(r.SourceCheckpointAttestationSha256) ||
            !Sha(r.TargetObservationSha256) ||
            r.ExpectedSourceRefs is null || r.ExpectedTargetRefs is null ||
            r.ExpectedSourceRefs.Count is < 1 or > MaxRefsPerSide ||
            r.ExpectedTargetRefs.Count is < 1 or > MaxRefsPerSide)
            throw Invalid();

        IReadOnlyList<GitMirrorRef> source;
        IReadOnlyList<GitMirrorRef> target;
        try
        {
            source = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                r.ExpectedSourceRefs);
            target = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                r.ExpectedTargetRefs);
        }
        catch (GitMirrorRefReconciliationException)
        { throw Invalid(); }

        if (!source.SequenceEqual(r.ExpectedSourceRefs) ||
            !target.SequenceEqual(r.ExpectedTargetRefs))
            throw Invalid();

        var formatLength = source[0].ObjectId.Length;
        if (formatLength is not (40 or 64) ||
            source.Concat(target).Any(x => x.ObjectId.Length != formatLength))
            throw Invalid();

        var names = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var reference in source.Concat(target))
        {
            if (names.TryGetValue(reference.Name, out var previous) &&
                previous != reference.Name)
                throw Invalid();
            names[reference.Name] = reference.Name;
        }
        return (source, target, formatLength == 40 ? "sha1" : "sha256");
    }

    // Seal only metadata returned by the physical read-only auditor.
    // Deterministic across repeated local inspections of the same refs.
    public static GitIncrementalAncestryReceipt Seal(
        GitIncrementalAncestryRequest request, string format,
        string sourceObjectSha, string targetObjectSha,
        int sourceObjectCount, int targetObjectCount,
        IReadOnlyList<GitIncrementalAncestryItem> items,
        DateTimeOffset verifiedAtUtc)
    {
        var (source, target, expectedFormat) = VerifyRequest(request);
        if (format != expectedFormat || !Sha(sourceObjectSha) ||
            !Sha(targetObjectSha) ||
            sourceObjectCount is < 1 or > GitObjectClosureAuditIntegrity.MaxObjects ||
            targetObjectCount is < 1 or > GitObjectClosureAuditIntegrity.MaxObjects ||
            verifiedAtUtc == default || items is null)
            throw Invalid();

        var sourceByName = source.ToDictionary(x => x.Name,
            x => x.ObjectId, StringComparer.Ordinal);
        var targetByName = target.ToDictionary(x => x.Name,
            x => x.ObjectId, StringComparer.Ordinal);
        var names = sourceByName.Keys.Concat(targetByName.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (names.Length != items.Count)
            throw Invalid();

        for (var i = 0; i < names.Length; i++)
        {
            var item = items[i];
            sourceByName.TryGetValue(names[i], out var src);
            targetByName.TryGetValue(names[i], out var dst);
            if (item is null || item.Name != names[i] ||
                item.SourceObjectId != src || item.TargetObjectId != dst)
                throw Invalid();
            if (src is null && item.Status !=
                GitIncrementalAncestryStatus.TargetOnlyPreserved ||
                dst is null && item.Status !=
                    GitIncrementalAncestryStatus.CreateCandidateBlocked ||
                src is not null && dst is not null &&
                    (src == dst && item.Status !=
                        GitIncrementalAncestryStatus.MatchingObjectId ||
                     src != dst &&
                        (names[i].StartsWith("refs/tags/",
                            StringComparison.Ordinal)
                            ? item.Status != GitIncrementalAncestryStatus.TagChangeBlocked
                            : item.Status is not (
                                GitIncrementalAncestryStatus.FastForwardCandidateBlocked or
                                GitIncrementalAncestryStatus.TargetAheadPreserved or
                                GitIncrementalAncestryStatus.DivergedBlocked or
                                GitIncrementalAncestryStatus.HistoryUnavailableBlocked))))
                throw Invalid();
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(new object?[] {
            request.SourceCheckpointGitRefProofSha256 is not null
                ? "f097c-offline-git-ancestry-checkpoint-mirror-bound-v3"
                : request.SourcePhysicalMirrorId is null
                    ? "f094-offline-git-ancestry-v1"
                    : "f096-offline-git-ancestry-mirror-bound-v2",
            request.SourceSnapshotId.ToString("D"),
            request.TargetSandboxId.ToString("D"),
            request.SourceCheckpointId.ToString("D"),
            request.SourceCheckpointAttestationSha256,
            request.TargetObservationId.ToString("D"),
            request.TargetObservationSha256,
            format, sourceObjectSha, targetObjectSha,
            sourceObjectCount, targetObjectCount, names.Length,
            false, false, false, false
        });
        if (request.SourcePhysicalMirrorId is Guid mirrorId)
            Append(new object?[] { "f060-verified-source-mirror",
                mirrorId.ToString("D"), request.SourcePhysicalMirrorProofSha256 });
        if (request.SourceCheckpointGitRefProofSha256 is string fullRefProof)
            Append(new object?[] { "f097-immutable-full-ref-oid-proof",
                fullRefProof });
        foreach (var reference in source)
            Append(new object?[] { "source", reference.Name, reference.ObjectId });
        foreach (var reference in target)
            Append(new object?[] { "target", reference.Name, reference.ObjectId });
        foreach (var item in items)
            Append(new object?[] { item.Name, item.SourceObjectId,
                item.TargetObjectId, item.Status.ToString() });

        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        return new(request, format, sourceObjectSha, targetObjectSha,
            sourceObjectCount, targetObjectCount, items.ToArray(),
            digest, verifiedAtUtc, false, false, false, false);

        void Append(object?[] row) =>
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row) + "\n"));
    }

    public static void VerifyReceipt(GitIncrementalAncestryReceipt receipt)
    {
        if (receipt is null || receipt.RemoteTransportInvoked ||
            receipt.CanPush || receipt.CanRetry || receipt.MigrationComplete ||
            !Sha(receipt.EvidenceSha256))
            throw Invalid();
        var sealedReceipt = Seal(receipt.Request, receipt.GitObjectFormat,
            receipt.SourceObjectSetSha256, receipt.TargetObjectSetSha256,
            receipt.SourceReachableObjectCount, receipt.TargetReachableObjectCount,
            receipt.Items, receipt.VerifiedAtUtc);
        if (sealedReceipt.EvidenceSha256 != receipt.EvidenceSha256)
            throw Invalid();
    }
}
