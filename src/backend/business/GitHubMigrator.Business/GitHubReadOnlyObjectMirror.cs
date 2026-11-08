using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-057: independently download a fresh GitHub read-only mirror and invoke
// F-056's full local object audit. There is no remote Git sender.
public sealed record GitHubReadOnlyObjectMirrorOptions(
    bool Enabled, long AllowedRepositoryId, string AllowedRepository);

public sealed record GitHubReadOnlyObjectMirrorRequest(
    Guid SourceSnapshotId, long RepositoryId, string Organization,
    string Repository, IReadOnlyList<GitMirrorRef> ExpectedSourceRefs,
    GitHubRefObservation ExpectedRemoteRefs, DateTimeOffset TokenExpiresAtUtc);

public sealed record GitHubReadOnlyObjectMirrorReceipt(
    Guid SourceSnapshotId, long RepositoryId, string Organization,
    string Repository, string GitObjectFormat, int VerifiedReachableObjects,
    int ObservedRemoteRefs, string SourceObjectSetSha256,
    string RemoteObjectSetSha256, string RemoteRefFingerprintSha256,
    string OfflineClosureEvidenceSha256, string EvidenceSha256,
    DateTimeOffset VerifiedAtUtc, bool GitHubFetchPerformed,
    bool RemotePushInvoked, bool RemoteWritesAuthorized,
    bool MigrationComplete);

public sealed class GitHubReadOnlyObjectMirrorException(string code)
    : Exception("Independent GitHub read-only object proof refused")
{
    public string Code { get; } = code;
}

public interface IGitHubReadOnlyObjectMirrorRunner
{
    // The installation credential is EPHEMERAL. It must not be stored in
    // a DTO, database, event, receipt, error, Git config file or log.
    Task<GitHubReadOnlyObjectMirrorReceipt> VerifyAsync(
        GitHubReadOnlyObjectMirrorRequest request,
        string installationToken, CancellationToken ct);
}

public static class GitHubReadOnlyObjectMirrorIntegrity
{
    public static readonly TimeSpan MaxRemoteObservationAge =
        TimeSpan.FromSeconds(90);

    public static string RemoteUrl(
        GitHubReadOnlyObjectMirrorRequest request,
        GitHubReadOnlyObjectMirrorOptions options,
        DateTimeOffset now)
    {
        Verify(request, options, now);
        return "https://github.com/" + request.Organization + "/" +
            request.Repository + ".git";
    }

    public static void Verify(
        GitHubReadOnlyObjectMirrorRequest request,
        GitHubReadOnlyObjectMirrorOptions options,
        DateTimeOffset now)
    {
        if (request is null || options is null ||
            !options.Enabled || options.AllowedRepositoryId <= 0 ||
            request.RepositoryId <= 0 ||
            request.RepositoryId != options.AllowedRepositoryId)
            throw new GitHubReadOnlyObjectMirrorException(
                "RemoteMirrorDisabledOrWrongRepository");

        if (request.SourceSnapshotId == Guid.Empty ||
            !GitHubRefObservationIntegrity.ValidOwner(request.Organization) ||
            !GitHubRefObservationIntegrity.ValidRepository(request.Repository) ||
            !string.Equals(options.AllowedRepository,
                request.Organization + "/" + request.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            request.ExpectedRemoteRefs is null ||
            request.ExpectedSourceRefs is null ||
            request.ExpectedRemoteRefs.Refs is null ||
            !string.Equals(request.ExpectedRemoteRefs.Organization,
                request.Organization, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.ExpectedRemoteRefs.Repository,
                request.Repository, StringComparison.OrdinalIgnoreCase) ||
            request.ExpectedRemoteRefs.ObservedAtUtc <
                now - MaxRemoteObservationAge ||
            request.ExpectedRemoteRefs.ObservedAtUtc > now.AddSeconds(5) ||
            request.TokenExpiresAtUtc <= now.AddMinutes(2) ||
            request.TokenExpiresAtUtc > now.AddMinutes(65))
            throw new GitHubReadOnlyObjectMirrorException("RemoteMirrorInputInvalid");

        GitHubRefObservation sealedRefs;
        try
        {
            sealedRefs = GitHubRefObservationIntegrity.Seal(
                request.ExpectedRemoteRefs.Organization,
                request.ExpectedRemoteRefs.Repository,
                request.ExpectedRemoteRefs.ObservedAtUtc,
                request.ExpectedRemoteRefs.Refs);
        }
        catch (GitHubRefObservationException)
        {
            throw new GitHubReadOnlyObjectMirrorException(
                "RemoteMirrorObservationInvalid");
        }

        if (request.ExpectedRemoteRefs.EvidenceSha256 !=
                sealedRefs.EvidenceSha256 ||
            !sealedRefs.Refs.SequenceEqual(request.ExpectedRemoteRefs.Refs))
            throw new GitHubReadOnlyObjectMirrorException(
                "RemoteMirrorObservationInvalid");

        try
        {
            GitObjectClosureAuditIntegrity.Verify(new(
                request.SourceSnapshotId, Guid.NewGuid(),
                request.ExpectedSourceRefs,
                request.ExpectedRemoteRefs.AsMirrorRefs()));
        }
        catch (GitObjectClosureAuditException)
        {
            throw new GitHubReadOnlyObjectMirrorException(
                "RemoteMirrorSourceNotMatching");
        }
    }

    public static GitHubReadOnlyObjectMirrorReceipt Seal(
        GitHubReadOnlyObjectMirrorRequest request,
        GitHubRemoteReadOnlyObjectProof verified,
        GitHubReadOnlyObjectMirrorOptions options,
        DateTimeOffset now)
    {
        Verify(request, options, now);
        if (verified is null || verified.Closure is null ||
            verified.PhysicalRemoteRefs is null ||
            verified.Closure.SourceSnapshotId != request.SourceSnapshotId ||
            verified.Closure.TargetSandboxId != verified.FetchedSnapshotId ||
            verified.Closure.TransportInvoked ||
            verified.Closure.RemoteWritesAuthorized ||
            verified.Closure.RemoteMigrationVerified ||
            !GitSourceBundleStorageCheck.ValidSha(
                verified.Closure.EvidenceSha256) ||
            verified.PhysicalRemoteRefs.Count !=
                request.ExpectedRemoteRefs.Refs.Count ||
            !verified.PhysicalRemoteRefs.SequenceEqual(
                request.ExpectedRemoteRefs.AsMirrorRefs()) ||
            verified.Closure.SourceRefSetSha256 !=
                GitImportRehearsalIntegrity.SealRefs(
                    "f056-source-refs-v1", request.ExpectedSourceRefs) ||
            verified.Closure.TargetRefSetSha256 !=
                GitImportRehearsalIntegrity.SealRefs(
                    "f056-target-refs-v1",
                    request.ExpectedRemoteRefs.AsMirrorRefs()) ||
            verified.Closure.SourceObjectSetSha256 !=
                verified.Closure.TargetObjectSetSha256 ||
            verified.Closure.ReachableObjectCount <= 0)
            throw new GitHubReadOnlyObjectMirrorException(
                "RemoteMirrorProofInvalid");

        var refFingerprint = GitHubRemoteRefFenceIntegrity.Fingerprint(
            request.ExpectedRemoteRefs.Refs);
        var hash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] {
                "f057-github-readonly-remote-full-closure-v1",
                request.SourceSnapshotId.ToString("D"),
                request.RepositoryId, request.Organization.ToLowerInvariant(),
                request.Repository.ToLowerInvariant(),
                request.ExpectedRemoteRefs.EvidenceSha256,
                refFingerprint, verified.Closure.EvidenceSha256,
                verified.Closure.SourceObjectSetSha256,
                verified.Closure.TargetObjectSetSha256,
                verified.Closure.ReachableObjectCount,
                true, false, false, false
            }))));
        return new(request.SourceSnapshotId, request.RepositoryId,
            request.Organization, request.Repository,
            verified.Closure.GitObjectFormat,
            verified.Closure.ReachableObjectCount,
            verified.PhysicalRemoteRefs.Count,
            verified.Closure.SourceObjectSetSha256,
            verified.Closure.TargetObjectSetSha256, refFingerprint,
            verified.Closure.EvidenceSha256, hash, now,
            true, false, false, false);
    }
}

// Only produced after a fresh Git read-only fetch, complete ref comparison
// and F-056's actual SHA-validated fsck + reachable-closure audit.
public sealed record GitHubRemoteReadOnlyObjectProof(
    Guid FetchedSnapshotId, IReadOnlyList<GitMirrorRef> PhysicalRemoteRefs,
    GitObjectClosureAuditReceipt Closure);
