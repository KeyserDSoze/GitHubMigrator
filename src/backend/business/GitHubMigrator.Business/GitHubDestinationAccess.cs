using System.Text.RegularExpressions;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// Read-only evidence: membership and organization policy DO NOT prove the token may
// create repositories. An actual write permission check needs a separately approved design.
public sealed record GitHubDestinationAccessEvidence(
    string MembershipStatus, string MembershipRole, string PrivateRepositoryPolicy);

public interface IGitHubDestinationAccessReader
{
    Task<GitHubDestinationAccessEvidence> ReadAsync(
        string organization, string expectedLogin, string token, CancellationToken ct);
}

public sealed record DestinationAccessReview(
    Guid DestinationConnectionId, string TargetOrganization, string AuthenticatedAs,
    DateTimeOffset CheckedAtUtc, string MembershipStatus, string MembershipRole,
    string PrivateRepositoryPolicy, bool MembershipVerified,
    bool RepositoryCreatePermissionVerified, bool ReadyForApproval);

public sealed class DestinationAccessInputException(string code)
    : Exception("Invalid destination access review")
{
    public string Code { get; } = code;
}

public sealed class GitHubDestinationAccessService(
    IConnectionRepository connections, IConnectionSecretStore secrets,
    IGitHubDestinationAccessReader reader, TimeProvider clock)
{
    private static readonly Regex GitHubLogin = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task<DestinationAccessReview> CheckAsync(Guid destinationId, CancellationToken ct)
    {
        if (destinationId == Guid.Empty)
            throw new DestinationAccessInputException("InvalidDestinationId");
        var destination = await connections.GetAsync(destinationId, ct)
            ?? throw new KeyNotFoundException("DestinationNotFound");
        if (destination.Direction != ConnectionDirection.Destination ||
            !string.Equals(destination.ProviderKey, "github-enterprise-cloud",
                StringComparison.OrdinalIgnoreCase))
            throw new DestinationAccessInputException("UnsupportedDestination");
        if (destination.ValidationCode != "Verified")
            throw new DestinationAccessInputException("DestinationNotVerified");
        // The login is sourced from F-002 validation, not request-controlled input.
        if (string.IsNullOrWhiteSpace(destination.AuthenticatedAs) ||
            !GitHubLogin.IsMatch(destination.AuthenticatedAs) ||
            destination.AuthenticatedAs.Contains("--", StringComparison.Ordinal))
            throw new DestinationAccessInputException("AuthenticatedLoginMissing");

        var token = await secrets.ReadAsync(destination.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new DestinationAccessInputException("DestinationCredentialMissing");

        GitHubDestinationAccessEvidence evidence;
        try
        {
            evidence = await reader.ReadAsync(destination.Organization, destination.AuthenticatedAs,
                token, ct);
        }
        catch (HttpRequestException)
        {
            evidence = new("Unavailable", "Unknown", "Unverified");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            evidence = new("Unavailable", "Unknown", "Unverified");
        }

        // Repo creation needs effective organization policy AND adequate token write scope.
        // GET-only requests can never establish the latter; no optimistic approval.
        return new(destination.Id, destination.Organization, destination.AuthenticatedAs,
            clock.GetUtcNow(), evidence.MembershipStatus, evidence.MembershipRole,
            evidence.PrivateRepositoryPolicy, evidence.MembershipStatus == "Active",
            false, false);
    }
}
