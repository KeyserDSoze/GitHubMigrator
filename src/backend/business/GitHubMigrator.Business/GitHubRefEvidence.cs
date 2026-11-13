using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-031: immutable, read-only GitHub Git-data observations. These records
// are not a repository-creation decision or proof of a write-capable token.
public sealed record GitHubObservedRef(
    string Name, string ObjectId, string ObjectType);

public sealed record GitHubRefObservation(
    string Organization, string Repository, DateTimeOffset ObservedAtUtc,
    string EvidenceSha256, IReadOnlyList<GitHubObservedRef> Refs)
{
    public IReadOnlyList<GitMirrorRef> AsMirrorRefs() =>
        Refs.Select(x => new GitMirrorRef(x.Name, x.ObjectId)).ToArray();
}

public sealed class GitHubRefObservationException(string code)
    : Exception("GitHub target ref observation rejected")
{
    public string Code { get; } = code;
}

public interface IGitHubRefObservationReader
{
    Task<GitHubRefObservation> ReadAsync(
        string organization, string repository, string token, CancellationToken ct);
}

public static class GitHubRefObservationIntegrity
{
    public static GitHubRefObservation Seal(
        string organization, string repository, DateTimeOffset timestamp,
        IReadOnlyList<GitHubObservedRef> refs)
    {
        if (!ValidOwner(organization) || !ValidRepository(repository) ||
            refs is null || refs.Count > GitMirrorRefReconciler.MaxRefsPerSide ||
            timestamp == default)
            throw new GitHubRefObservationException("InvalidTargetRefObservation");

        if (refs.Any(x => x is null ||
            x.ObjectType is not ("commit" or "tag") ||
            (x.Name.StartsWith("refs/heads/", StringComparison.Ordinal) &&
                x.ObjectType != "commit")))
            throw new GitHubRefObservationException("InvalidTargetRefObservation");

        IReadOnlyList<GitMirrorRef> canonical;
        try
        {
            canonical = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                refs.Select(x => new GitMirrorRef(x.Name, x.ObjectId)).ToArray());
        }
        catch (GitMirrorRefReconciliationException)
        {
            throw new GitHubRefObservationException("InvalidTargetRefObservation");
        }

        var types = refs.ToDictionary(x => x.Name, x => x.ObjectType,
            StringComparer.Ordinal);
        var safe = canonical.Select(x =>
            new GitHubObservedRef(x.Name, x.ObjectId, types[x.Name])).ToArray();

        // The exact SHA of an annotated tag (object.type=tag) is retained:
        // never substitute the peeled commit SHA from the Tags API.
        var when = DateTimeOffset.FromUnixTimeMilliseconds(
            timestamp.ToUnixTimeMilliseconds());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(new object?[] {
            "github-target-refs-v1", organization.ToLowerInvariant(),
            repository.ToLowerInvariant(), when.ToUnixTimeMilliseconds(),
            safe.Length
        });
        foreach (var entry in safe)
            Append(new object?[] { entry.Name, entry.ObjectId, entry.ObjectType });
        return new(organization, repository, when,
            Convert.ToHexStringLower(hash.GetHashAndReset()), safe);

        void Append(object?[] values)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(values) + "\n"));
        }
    }

    public static bool ValidOwner(string? owner) =>
        owner is { Length: >= 1 and <= 100 } &&
        owner[0] != '-' && owner[^1] != '-' &&
        owner.All(x => char.IsAsciiLetterOrDigit(x) || x == '-');

    public static bool ValidRepository(string? name) =>
        name is { Length: >= 1 and <= 100 } &&
        name is not "." and not ".." &&
        name.All(x => char.IsAsciiLetterOrDigit(x) ||
            x is '.' or '-' or '_');
}
