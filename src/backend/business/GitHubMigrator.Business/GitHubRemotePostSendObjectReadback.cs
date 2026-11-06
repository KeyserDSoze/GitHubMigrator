using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-053: provider Git-data object tips are readback evidence, never execution
// authority, a full-history closure proof or a safe automatic retry signal.
public sealed record GitHubRemoteTipObject(
    string RefName, string RefObjectId, string RefObjectType,
    string CommitObjectId, string CommitTreeId, int TagObjectsTraversed);

public sealed record GitHubRemoteTipReadback(
    string Organization, string Repository,
    string RefSetEvidenceSha256, string RefSetFingerprintSha256,
    DateTimeOffset ObservedAtUtc, string EvidenceSha256,
    IReadOnlyList<GitHubRemoteTipObject> Tips,
    bool MayAutomaticallyRetry, bool RemoteWritesAuthorized);

public sealed record GitHubRemoteTipSnapshot(
    GitHubRefObservation RemoteRefs, GitHubRemoteTipReadback TipReadback);

public sealed record GitHubRemotePostSendObjectAssessment(
    string State, string RefSetSha256, string TipEvidenceSha256,
    string EvidenceSha256, bool MayAutomaticallyRetry,
    bool RemoteWritesAuthorized);

public sealed class GitHubRemoteTipException(string code)
    : Exception("GitHub remote tip readback rejected")
{
    public string Code { get; } = code;
}

public interface IGitHubRemoteTipObjectReader
{
    Task<GitHubRemoteTipSnapshot> ReadAsync(
        string organization, string repository, string token,
        IReadOnlyList<GitMirrorRef> createCandidates, CancellationToken ct);
}

public static class GitHubRemoteTipIntegrity
{
    public const int MaxTagDepth = 4;

    public static bool ValidObjectId(string? oid) =>
        oid is { Length: 40 or 64 } &&
        oid.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static IReadOnlyList<GitMirrorRef> CanonicalCandidates(
        IReadOnlyList<GitMirrorRef> candidates)
    {
        if (candidates is null ||
            candidates.Count is < 1 or >
                GitHubCandidatePolicyIntegrity.MaxOnlineCandidates)
            throw new GitHubRemoteTipException("RemoteTipCandidatesInvalid");
        try
        {
            var canonical = GitMirrorRefReconciler.CanonicalizeObservedRefs(candidates);
            if (!canonical.SequenceEqual(candidates))
                throw new GitHubRemoteTipException("RemoteTipCandidatesInvalid");
            return canonical;
        }
        catch (GitMirrorRefReconciliationException)
        {
            throw new GitHubRemoteTipException("RemoteTipCandidatesInvalid");
        }
    }

    public static GitHubRemoteTipReadback Seal(
        GitHubRefObservation remote, IReadOnlyList<GitMirrorRef> candidates,
        DateTimeOffset observedAtUtc, IReadOnlyList<GitHubRemoteTipObject> tips)
    {
        if (remote is null || remote.Refs is null || tips is null ||
            observedAtUtc == default)
            throw new GitHubRemoteTipException("RemoteTipEvidenceInvalid");

        GitHubRefObservation checkedRefs;
        try
        {
            checkedRefs = GitHubRefObservationIntegrity.Seal(
                remote.Organization, remote.Repository,
                remote.ObservedAtUtc, remote.Refs);
        }
        catch (GitHubRefObservationException)
        {
            throw new GitHubRemoteTipException("RemoteTipEvidenceInvalid");
        }

        var requested = CanonicalCandidates(candidates);
        var when = DateTimeOffset.FromUnixTimeMilliseconds(
            observedAtUtc.ToUnixTimeMilliseconds());
        if (checkedRefs.EvidenceSha256 != remote.EvidenceSha256 ||
            !checkedRefs.Refs.SequenceEqual(remote.Refs) ||
            when < remote.ObservedAtUtc ||
            when > remote.ObservedAtUtc.AddMinutes(1) ||
            tips.Count != requested.Count)
            throw new GitHubRemoteTipException("RemoteTipEvidenceInvalid");

        var known = remote.Refs.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var provided = new Dictionary<string, GitHubRemoteTipObject>(
            StringComparer.Ordinal);
        foreach (var tip in tips)
            if (tip is null || string.IsNullOrEmpty(tip.RefName) ||
                !provided.TryAdd(tip.RefName, tip))
                throw new GitHubRemoteTipException("RemoteTipEvidenceInvalid");

        var safe = new List<GitHubRemoteTipObject>(requested.Count);
        foreach (var candidate in requested)
        {
            if (!known.TryGetValue(candidate.Name, out var reference) ||
                reference.ObjectId != candidate.ObjectId ||
                !provided.TryGetValue(candidate.Name, out var tip) ||
                tip.RefObjectId != reference.ObjectId ||
                tip.RefObjectType != reference.ObjectType ||
                !ValidObjectId(tip.CommitObjectId) ||
                !ValidObjectId(tip.CommitTreeId) ||
                tip.CommitObjectId.Length != candidate.ObjectId.Length ||
                tip.CommitTreeId.Length != candidate.ObjectId.Length ||
                (tip.RefObjectType == "commit" &&
                    (tip.CommitObjectId != candidate.ObjectId ||
                     tip.TagObjectsTraversed != 0)) ||
                (tip.RefObjectType == "tag" &&
                    tip.TagObjectsTraversed is < 1 or > MaxTagDepth))
                throw new GitHubRemoteTipException("RemoteTipEvidenceInvalid");
            safe.Add(tip);
        }

        var fingerprint = GitHubRemoteRefFenceIntegrity.Fingerprint(remote.Refs);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(new object?[] {
            "f053-github-remote-tip-readback-v1",
            remote.Organization.ToLowerInvariant(),
            remote.Repository.ToLowerInvariant(),
            remote.EvidenceSha256, fingerprint,
            when.ToUnixTimeMilliseconds(), safe.Count, false, false
        });
        foreach (var tip in safe)
            Append(new object?[] {
                tip.RefName, tip.RefObjectId, tip.RefObjectType,
                tip.CommitObjectId, tip.CommitTreeId, tip.TagObjectsTraversed
            });
        return new(remote.Organization, remote.Repository,
            remote.EvidenceSha256, fingerprint, when,
            Convert.ToHexStringLower(hash.GetHashAndReset()), safe,
            false, false);

        void Append(object?[] fields) =>
            hash.AppendData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(fields) + "\n"));
    }

    // Recompute BOTH proofs. Even valid object tips cannot override a
    // divergent or no-created F-052 complete remote ref-set result.
    public static GitHubRemotePostSendObjectAssessment Inspect(
        IReadOnlyList<GitMirrorRef> baseline,
        IReadOnlyList<GitMirrorRef> creates,
        GitHubRefObservation remote,
        GitHubRemoteTipReadback readback)
    {
        if (readback is null || readback.MayAutomaticallyRetry ||
            readback.RemoteWritesAuthorized)
            throw new GitHubRemoteTipException("RemoteTipEvidenceInvalid");

        var proof = Seal(remote, creates,
            readback.ObservedAtUtc, readback.Tips);
        if (proof.EvidenceSha256 != readback.EvidenceSha256 ||
            proof.RefSetEvidenceSha256 != readback.RefSetEvidenceSha256 ||
            proof.RefSetFingerprintSha256 != readback.RefSetFingerprintSha256 ||
            proof.Organization != readback.Organization ||
            proof.Repository != readback.Repository ||
            !proof.Tips.SequenceEqual(readback.Tips))
            throw new GitHubRemoteTipException("RemoteTipEvidenceInvalid");

        var refs = GitHubRemotePostSendIntegrity.Inspect(baseline, creates, remote);
        var state = refs.State == "ExactRemoteRefsObserved"
            ? "ExactRemoteRefsAndTipObjectsObserved"
            : "RemoteTipReadbackRequiresOperator";
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] {
                "f053-github-remote-postsend-assessment-v1",
                state, refs.EvidenceSha256,
                proof.EvidenceSha256, false, false
            }))));
        return new(state, refs.RefSetSha256, proof.EvidenceSha256,
            digest, false, false);
    }
}
