using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-048 executes real Git object transport to a trusted LOCAL bare repository.
// It never accepts a URL, path, credential, Git argument or caller-supplied
// execution grant. This contract cannot be used for a GitHub remote push.
public sealed record LocalGitCreateOnlyTransferRequest(
    GitHubCreateOnlyPacket Packet, GitSourceBundleView Bundle,
    Guid SandboxId, IReadOnlyList<GitMirrorRef> SourceRefs,
    IReadOnlyList<GitMirrorRef> ExpectedTargetRefs,
    IReadOnlyList<GitMirrorRef> CreateRefs);

public sealed record LocalGitCreateOnlyTransferReceipt(
    Guid SandboxId, long GitHubRepositoryId, string PacketSha256,
    string GitObjectFormat, int CreatedRefs, int PreservedRefs,
    string SourceRefsSha256, string BeforeRefsSha256,
    string CreatedRefsSha256, string AfterRefsSha256,
    DateTimeOffset VerifiedAtUtc, string EvidenceSha256,
    string State, bool LocalSandboxWritesPerformed,
    bool RemoteWritesPerformed, bool ExecutionAuthorized,
    bool ReadyForExecution);

public sealed record LocalGitCreateOnlyTransferInspection(
    Guid SandboxId, string State, int RemoteRefs, int MatchingCreatedRefs,
    string ActualRefsSha256, bool MayAutomaticallyRetry,
    bool ExecutionAuthorized, bool RemoteWritesPerformed);

public sealed class LocalGitCreateOnlyTransferException(string code)
    : Exception("Local Git create-only transfer refused")
{
    public string Code { get; } = code;
}

public static class LocalGitCreateOnlyTransferIntegrity
{
    public const int MaxCandidates = GitHubCandidatePolicyIntegrity.MaxOnlineCandidates;
    public const string VerifiedState = "LocalSandboxObjectsVerified";
    public const string DivergedState = "LocalSandboxReconciliationRequired";

    public static void Verify(LocalGitCreateOnlyTransferRequest request)
    {
        if (request is null || request.Packet is null || request.Bundle is null ||
            request.SourceRefs is null || request.ExpectedTargetRefs is null ||
            request.CreateRefs is null || request.SandboxId == Guid.Empty)
            throw new LocalGitCreateOnlyTransferException("SandboxRequestInvalid");

        var packet = request.Packet;
        var bundle = request.Bundle;
        if (packet.State != GitHubCreateOnlyPacketIntegrity.State ||
            packet.RepositoryId <= 0 || packet.WorkpackId != bundle.WorkpackId ||
            packet.PlanId != bundle.PlanId ||
            packet.BundleSha256 != bundle.BundleSha256 ||
            packet.ReconciliationSha256 != bundle.ReconciliationSha256 ||
            !bundle.IsCurrent || bundle.ReadyForExecution ||
            packet.ExecutionAuthorized || packet.RemoteWritesPerformed ||
            packet.WriteCapabilityVerified || packet.ReadyForExecution ||
            packet.CreateCandidates is < 1 or > MaxCandidates ||
            request.CreateRefs.Count != packet.CreateCandidates ||
            request.SourceRefs.Count != bundle.RefCount ||
            !GitSourceBundleStorageCheck.ValidSha(packet.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(packet.CandidateSetSha256))
            throw new LocalGitCreateOnlyTransferException("SandboxPacketInvalid");

        try
        {
            var source = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                request.SourceRefs);
            var target = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                request.ExpectedTargetRefs);
            var candidates = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                request.CreateRefs);
            if (!source.SequenceEqual(request.SourceRefs) ||
                !target.SequenceEqual(request.ExpectedTargetRefs) ||
                !candidates.SequenceEqual(request.CreateRefs) ||
                GitImportRehearsalIntegrity.SealRefs(
                    "f044-create-only-candidate-refs-v1", candidates) !=
                packet.CandidateSetSha256)
                throw new LocalGitCreateOnlyTransferException("SandboxRefPlanInvalid");

            var sourceMap = source.ToDictionary(x => x.Name,
                StringComparer.Ordinal);
            var targetMap = target.ToDictionary(x => x.Name,
                StringComparer.Ordinal);
            var missing = source.Where(x => !targetMap.ContainsKey(x.Name)).ToArray();
            if (!missing.SequenceEqual(candidates) ||
                source.Count(x => targetMap.ContainsKey(x.Name)) !=
                    packet.AlreadyMatching ||
                target.Count(x => !sourceMap.ContainsKey(x.Name)) !=
                    packet.TargetOnlyPreserved ||
                source.Any(x => targetMap.TryGetValue(x.Name, out var old) &&
                    old.ObjectId != x.ObjectId))
                throw new LocalGitCreateOnlyTransferException("SandboxRefPlanInvalid");

            var formats = source.Concat(target).Select(x => x.ObjectId.Length)
                .Distinct().ToArray();
            if (formats.Length != 1 || formats[0] is not (40 or 64))
                throw new LocalGitCreateOnlyTransferException("SandboxObjectFormatInvalid");
        }
        catch (GitMirrorRefReconciliationException)
        {
            throw new LocalGitCreateOnlyTransferException("SandboxRefPlanInvalid");
        }
    }

    public static string RefsDigest(IReadOnlyList<GitMirrorRef> refs) =>
        GitImportRehearsalIntegrity.SealRefs("f048-local-ref-set-v1", refs);

    public static LocalGitCreateOnlyTransferReceipt Seal(
        LocalGitCreateOnlyTransferRequest request,
        IReadOnlyList<GitMirrorRef> before,
        IReadOnlyList<GitMirrorRef> after,
        DateTimeOffset verifiedAtUtc)
    {
        Verify(request);
        var expected = GitMirrorRefReconciler.CanonicalizeObservedRefs(
            request.ExpectedTargetRefs.Concat(request.CreateRefs).ToArray());
        if (!before.SequenceEqual(request.ExpectedTargetRefs) ||
            !after.SequenceEqual(expected) ||
            after.Count != before.Count + request.CreateRefs.Count ||
            verifiedAtUtc == default)
            throw new LocalGitCreateOnlyTransferException(
                "SandboxDestinationReconciliationRequired");
        var format = request.SourceRefs[0].ObjectId.Length == 40 ?
            "sha1" : "sha256";
        var sourceDigest = RefsDigest(request.SourceRefs);
        var beforeDigest = RefsDigest(before);
        var createdDigest = RefsDigest(request.CreateRefs);
        var afterDigest = RefsDigest(after);
        var canonical = JsonSerializer.Serialize(new object[] {
            "f048-local-create-only-transfer-v1",
            request.SandboxId.ToString("D"),
            request.Packet.RepositoryId, request.Packet.EvidenceSha256,
            request.Bundle.BundleSha256, request.Packet.CandidateSetSha256,
            format, request.CreateRefs.Count, before.Count,
            sourceDigest, beforeDigest, createdDigest, afterDigest,
            verifiedAtUtc.ToUnixTimeMilliseconds(),
            VerifiedState, true, false, false, false
        });
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(request.SandboxId, request.Packet.RepositoryId,
            request.Packet.EvidenceSha256, format,
            request.CreateRefs.Count, before.Count, sourceDigest, beforeDigest,
            createdDigest, afterDigest, verifiedAtUtc, digest,
            VerifiedState, true, false, false, false);
    }

    public static LocalGitCreateOnlyTransferInspection Inspect(
        LocalGitCreateOnlyTransferRequest request,
        IReadOnlyList<GitMirrorRef> actual)
    {
        Verify(request);
        var expected = GitMirrorRefReconciler.CanonicalizeObservedRefs(
            request.ExpectedTargetRefs.Concat(request.CreateRefs).ToArray());
        var canonical = GitMirrorRefReconciler.CanonicalizeObservedRefs(actual);
        var lookup = canonical.ToDictionary(x => x.Name,
            StringComparer.Ordinal);
        return new(request.SandboxId,
            canonical.SequenceEqual(expected) ? "ExpectedLocalRefsObserved" :
                DivergedState,
            canonical.Count,
            request.CreateRefs.Count(x => lookup.TryGetValue(
                x.Name, out var observed) && observed.ObjectId == x.ObjectId),
            RefsDigest(canonical), false, false, false);
    }
}

public interface ILocalGitCreateOnlyTransferRunner
{
    Task<LocalGitCreateOnlyTransferReceipt> RunAsync(
        LocalGitCreateOnlyTransferRequest request, CancellationToken ct);
    Task<LocalGitCreateOnlyTransferInspection> InspectAsync(
        LocalGitCreateOnlyTransferRequest request, CancellationToken ct);
}
