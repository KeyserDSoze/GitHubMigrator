using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Identity;

// #129: revalidate the CURRENT identity store, not just the signed,
// potentially stale JWT role/password_changed claims. Read-only guard
// before AND after the POSIX-locked physical inspection. A session ID
// is available to a future one-time SQL writer but no such writer exists.
public sealed class GitIncrementalLaterCheckpointMirrorSecondReviewActorPreflight(
    UserManager<ApplicationUser> users,
    TimeProvider clock)
{
    public async Task<Guid> RequireAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(principal.FindFirstValue(
                ClaimTypes.NameIdentifier), out var actor) ||
            actor == Guid.Empty ||
            !principal.IsInRole("superadmin") ||
            principal.FindFirstValue("password_changed") != "true" ||
            !Guid.TryParseExact(
                principal.FindFirstValue("f061_review_session_id"), "N",
                out var sessionId) || sessionId == Guid.Empty ||
            !long.TryParse(principal.FindFirstValue("f061_review_issued_at"),
                NumberStyles.None, CultureInfo.InvariantCulture,
                out var issuedSeconds))
            throw new UnauthorizedAccessException();

        DateTimeOffset issued;
        try { issued = DateTimeOffset.FromUnixTimeSeconds(issuedSeconds); }
        catch (ArgumentOutOfRangeException)
        {
            throw new UnauthorizedAccessException();
        }
        var now = clock.GetUtcNow();
        if (issued > now.AddSeconds(30) ||
            now - issued > TimeSpan.FromMinutes(5))
            throw new UnauthorizedAccessException();

        var account = await users.FindByIdAsync(actor.ToString("D"));
        if (account is null || account.MustChangePassword ||
            await users.IsLockedOutAsync(account) ||
            !await users.IsInRoleAsync(account, "superadmin"))
            throw new UnauthorizedAccessException();

        // Changing password, revoking the security stamp, disabling
        // the account or removing the role must invalidate an already
        // minted reviewer token, even if its 30-minute JWT is valid.
        var presented = principal.FindFirstValue(
            "f061_review_security_stamp");
        var current = await users.GetSecurityStampAsync(account);
        if (string.IsNullOrEmpty(presented) ||
            string.IsNullOrEmpty(current))
            throw new UnauthorizedAccessException();
        var expected = Encoding.UTF8.GetBytes(current);
        var actual = Encoding.UTF8.GetBytes(presented);
        if (expected.Length != actual.Length ||
            !CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new UnauthorizedAccessException();
        return actor;
    }
}
