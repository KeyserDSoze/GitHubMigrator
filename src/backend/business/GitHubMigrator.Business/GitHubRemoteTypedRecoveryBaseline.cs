using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-086 persists the missing F-083 typed pre-send snapshot so a different
// process can safely reconstruct the exact F-054/F-059 inputs AFTER a crash.
// There is no GitHub transport authority, credential or remote mutation here.
public sealed record GitHubRemoteTypedRecoveryBaselineOptions(
    bool Enabled, long AllowedRepositoryId, string AllowedRepository);

public sealed class GitHubRemoteTypedRecoveryBaselineException(string code)
    : Exception("Historical typed GitHub baseline refused")
{
    public string Code { get; } = code;
}

public sealed record GitHubRemoteTypedRecoveryBaselineReceipt(
    Guid Id, Guid ConfirmationId, Guid IntentId, Guid HazardId,
    Guid GrantId, Guid PreparationId, Guid PlanId, Guid ScopeId,
    long RepositoryId, string Organization, string Repository,
    Guid CapturedBy, string PacketSha256, string CandidateSetSha256,
    string BaselineSha256, GitHubRefObservation Baseline,
    IReadOnlyList<GitMirrorRef> CreateCandidates,
    DateTimeOffset RecordedAtUtc, string State, string EvidenceSha256,
    bool CanSend, bool MayAutomaticallyRetry, bool MigrationComplete);

public sealed record GitHubRemoteTypedRecoveryBaselineStatus(
    Guid Id, Guid ConfirmationId, Guid IntentId, Guid HazardId,
    Guid GrantId, long RepositoryId, Guid CapturedBy,
    string PacketSha256, string CandidateSetSha256,
    string BaselineSha256, string BaselineEvidenceSha256,
    int BaselineRefCount, int CreateCandidateCount,
    DateTimeOffset RecordedAtUtc, bool HasPossibleSendBarrier,
    bool CanSend, bool CanRetry, bool MigrationComplete);

// The snapshot is captured from the server-side F-044 packet + the LAST
// actual, sealed GitHub GET observation, before F-082 commits. No ref,
// OID, repository URL or execution flag may come from HTTP input.
public interface IGitHubRemoteTypedRecoveryBaselineJournal
{
    Task<GitHubRemoteTypedRecoveryBaselineReceipt> CaptureAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, Guid independentOperator,
        GitHubCreateOnlyCandidatePacket source,
        GitHubRefObservation latestBaseline, CancellationToken ct);

    // Server-only recovery read. Never returns an uncommitted F-086
    // snapshot: F-082's matching irreversible barrier is mandatory.
    Task<GitHubRemoteTypedRecoveryBaselineReceipt?> ReadAfterBarrierAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, CancellationToken ct);
}

public static class GitHubRemoteTypedRecoveryBaselineIntegrity
{
    public const string Captured = "TypedPreSendEvidenceOnly";
    public const int MaxBaselineRefs = 4096;
    public const int MaxPayloadChars = 512_000;

    public static string Digest(GitHubRemoteTypedRecoveryBaselineReceipt x)
    {
        if (x is null || x.Id == Guid.Empty ||
            x.ConfirmationId == Guid.Empty || x.IntentId == Guid.Empty ||
            x.HazardId == Guid.Empty || x.GrantId == Guid.Empty ||
            x.PreparationId == Guid.Empty || x.PlanId == Guid.Empty ||
            x.ScopeId == Guid.Empty || x.RepositoryId <= 0 ||
            x.CapturedBy == Guid.Empty || x.RecordedAtUtc == default ||
            x.Baseline is null || x.Baseline.Refs is null ||
            x.CreateCandidates is null ||
            x.Baseline.Refs.Count > MaxBaselineRefs ||
            x.CreateCandidates.Count is < 1 or >
                GitHubCandidatePolicyIntegrity.MaxOnlineCandidates ||
            !GitHubRefObservationIntegrity.ValidOwner(x.Organization) ||
            !GitHubRefObservationIntegrity.ValidRepository(x.Repository) ||
            !string.Equals(x.Baseline.Organization, x.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(x.Baseline.Repository, x.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            x.Baseline.ObservedAtUtc > x.RecordedAtUtc.AddSeconds(5) ||
            x.Baseline.ObservedAtUtc <
                x.RecordedAtUtc.AddMinutes(-2) ||
            !GitSourceBundleStorageCheck.ValidSha(x.PacketSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(x.CandidateSetSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(x.BaselineSha256) ||
            x.State != Captured || x.CanSend ||
            x.MayAutomaticallyRetry || x.MigrationComplete)
            throw Refused("TypedRecoveryBaselineInvalid");

        GitHubRefObservation baseline;
        IReadOnlyList<GitMirrorRef> candidates;
        try
        {
            baseline = GitHubRefObservationIntegrity.Seal(
                x.Organization, x.Repository,
                x.Baseline.ObservedAtUtc, x.Baseline.Refs);
            candidates = GitHubRemoteTipIntegrity.CanonicalCandidates(
                x.CreateCandidates);
        }
        catch (Exception ex) when (ex is GitHubRefObservationException or
            GitHubRemoteTipException or GitMirrorRefReconciliationException)
        { throw Refused("TypedRecoveryBaselineInvalid"); }
        if (baseline.EvidenceSha256 != x.Baseline.EvidenceSha256 ||
            !baseline.Refs.SequenceEqual(x.Baseline.Refs) ||
            !candidates.SequenceEqual(x.CreateCandidates) ||
            GitHubRemoteRefFenceIntegrity.Fingerprint(
                baseline.Refs) != x.BaselineSha256 ||
            GitImportRehearsalIntegrity.SealRefs(
                "f044-create-only-candidate-refs-v1", candidates) !=
                x.CandidateSetSha256 ||
            baseline.Refs.Any(x => candidates.Any(c => c.Name == x.Name)))
            throw Refused("TypedRecoveryBaselineInvalid");

        var canonical = JsonSerializer.Serialize(new object[] {
            "f086-persisted-typed-presend-baseline-v1",
            x.Id.ToString("D"), x.ConfirmationId.ToString("D"),
            x.IntentId.ToString("D"), x.HazardId.ToString("D"),
            x.GrantId.ToString("D"), x.PreparationId.ToString("D"),
            x.PlanId.ToString("D"), x.ScopeId.ToString("D"),
            x.RepositoryId, x.Organization.ToLowerInvariant(),
            x.Repository.ToLowerInvariant(), x.CapturedBy.ToString("D"),
            x.PacketSha256, x.CandidateSetSha256, x.BaselineSha256,
            x.Baseline.EvidenceSha256,
            x.Baseline.ObservedAtUtc.ToUnixTimeMilliseconds(),
            x.Baseline.Refs.Select(r => new[] {
                r.Name, r.ObjectId, r.ObjectType
            }).ToArray(),
            candidates.Select(c => new[] { c.Name, c.ObjectId }).ToArray(),
            x.RecordedAtUtc.ToUnixTimeMilliseconds(),
            Captured, false, false, false
        });
        if (canonical.Length > MaxPayloadChars)
            throw Refused("TypedRecoveryBaselineTooLarge");
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical)));
    }

    public static void Verify(GitHubRemoteTypedRecoveryBaselineReceipt x)
    {
        if (Digest(x) != x.EvidenceSha256)
            throw Refused("TypedRecoveryBaselineHistoryCorrupt");
    }

    private static GitHubRemoteTypedRecoveryBaselineException Refused(string code)
        => new(code);
}
