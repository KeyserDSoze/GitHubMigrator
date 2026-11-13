using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GhSim;

public enum PrincipalKind { Anonymous, User, Installation, App }

public sealed class Principal(PrincipalKind kind)
{
    public PrincipalKind Kind { get; } = kind;
    public GhUser? User { get; init; }
    public GhApp? App { get; init; }
    public long InstallationId { get; init; }
    public string? InstallationOrg { get; init; }
    public HashSet<long> RepositoryIds { get; init; } = [];
    public string Contents { get; init; } = "none";
    public string Name => User?.Login ?? (App is null ? "anonymous" : $"app:{App.Slug}");
}

public sealed class InstallationToken
{
    public required string Token { get; init; }
    public required GhApp App { get; init; }
    public required GhInstallation Installation { get; init; }
    public required HashSet<long> RepositoryIds { get; init; }
    public required string Contents { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed record RequestLogEntry(DateTimeOffset At, string Method, string Path, int Status, string Principal);

public sealed class Fault
{
    public string PathRegex { get; set; } = "";
    public string? Method { get; set; }
    public int Status { get; set; } = 500;
    public string? Message { get; set; }
    public bool RateLimited { get; set; }
    public int Remaining { get; set; } = int.MaxValue;
}

// Everything the endpoint handlers share: state, auth, clock, diagnostics.
public sealed class GhSim(GhSimOptions options, GhStore store)
{
    public GhSimOptions Options { get; } = options;
    public GhStore Store { get; } = store;
    public TimeProvider Clock { get; } = options.Clock ?? TimeProvider.System;
    public ConcurrentDictionary<string, InstallationToken> InstallationTokens { get; } = new();
    public ConcurrentQueue<RequestLogEntry> Requests { get; } = new();
    public List<Fault> Faults { get; } = [];
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _repoLocks = new();

    public DateTimeOffset Now => Clock.GetUtcNow();
    public SemaphoreSlim RepoLock(GhRepo repo) => _repoLocks.GetOrAdd(repo.Id, _ => new SemaphoreSlim(1, 1));

    public void Log(HttpContext ctx, int status)
    {
        var path = ctx.Request.Path.Value ?? "";
        Requests.Enqueue(new(Now, ctx.Request.Method, path, status, Auth(ctx).Name));
        while (Requests.Count > 2000) Requests.TryDequeue(out _);
    }

    public const string PrincipalKey = "ghsim.principal";
    public static Principal Auth(HttpContext ctx) =>
        ctx.Items[PrincipalKey] as Principal ?? new Principal(PrincipalKind.Anonymous);

    // Accepts "Bearer x", "token x" and HTTP Basic (git: password carries the token).
    public Principal Authenticate(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        string? secret = null;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) secret = header[7..].Trim();
        else if (header.StartsWith("token ", StringComparison.OrdinalIgnoreCase)) secret = header[6..].Trim();
        else if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = decoded.IndexOf(':');
                secret = colon < 0 ? decoded : decoded[(colon + 1)..];
            }
            catch (FormatException) { return new(PrincipalKind.Anonymous); }
        }
        if (string.IsNullOrEmpty(secret)) return new(PrincipalKind.Anonymous);

        lock (Store.Lock)
        {
            var user = Store.State.Users.FirstOrDefault(u => u.Token == secret);
            if (user is not null) return new(PrincipalKind.User) { User = user };
        }
        if (InstallationTokens.TryGetValue(secret, out var minted))
        {
            if (minted.ExpiresAt <= Now) { InstallationTokens.TryRemove(secret, out _); return new(PrincipalKind.Anonymous); }
            return new(PrincipalKind.Installation)
            {
                App = minted.App, InstallationId = minted.Installation.Id, InstallationOrg = minted.Installation.Org,
                RepositoryIds = minted.RepositoryIds, Contents = minted.Contents
            };
        }
        if (secret.Count(c => c == '.') == 2 && TryVerifyJwt(secret, out var app))
            return new(PrincipalKind.App) { App = app };
        return new(PrincipalKind.Anonymous);
    }

    private bool TryVerifyJwt(string jwt, out GhApp? app)
    {
        app = null;
        try
        {
            var parts = jwt.Split('.');
            using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "RS256") return false;
            var issuer = payload.RootElement.GetProperty("iss");
            var appId = issuer.ValueKind == JsonValueKind.Number ? issuer.GetInt64() : long.Parse(issuer.GetString()!);
            var now = Now.ToUnixTimeSeconds();
            if (payload.RootElement.GetProperty("exp").GetInt64() <= now ||
                payload.RootElement.GetProperty("iat").GetInt64() > now + 60) return false;
            GhApp? candidate;
            lock (Store.Lock) candidate = Store.State.Apps.FirstOrDefault(a => a.Id == appId);
            if (candidate is null) return false;
            if (candidate.PublicKeyPem is not null)
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(candidate.PublicKeyPem);
                if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64UrlDecode(parts[2]),
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return false;
            }
            app = candidate;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or CryptographicException
            or InvalidOperationException or ArgumentException or OverflowException) { return false; }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    // ---- authorization -------------------------------------------------------------------

    public bool IsOrgMember(GhUser user, GhOrg org) => org.Members.ContainsKey(user.Login);
    public bool IsOrgAdmin(GhUser user, GhOrg org) =>
        org.Members.TryGetValue(user.Login, out var role) && role.Equals("admin", StringComparison.OrdinalIgnoreCase);

    // Hidden repositories answer 404, never 403, exactly like github.com.
    public bool CanRead(Principal who, GhRepo repo)
    {
        if (who.Kind == PrincipalKind.User)
        {
            var org = Store.Org(repo.Org);
            return org is not null && IsOrgMember(who.User!, org);
        }
        return who.Kind == PrincipalKind.Installation && who.RepositoryIds.Contains(repo.Id) && who.Contents is "read" or "write";
    }

    public bool CanWrite(Principal who, GhRepo repo)
    {
        if (!CanRead(who, repo) || repo.Archived || repo.Disabled) return false;
        return who.Kind == PrincipalKind.User ? !who.User!.ReadOnly : who.Contents == "write";
    }

    public bool CanAdmin(Principal who, GhRepo repo)
    {
        var org = Store.Org(repo.Org);
        return who.Kind == PrincipalKind.User && org is not null && IsOrgAdmin(who.User!, org) && !repo.Archived;
    }

    // ---- rulesets ------------------------------------------------------------------------

    public static bool RefMatches(GhRepo repo, string pattern, string refName)
    {
        if (pattern == "~ALL") return true;
        if (pattern == "~DEFAULT_BRANCH") return refName == "refs/heads/" + repo.DefaultBranch;
        if (!pattern.Contains('*')) return pattern == refName;
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "$";
        return Regex.IsMatch(refName, regex);
    }

    public static IEnumerable<GhRuleset> RulesetsFor(GhRepo repo, string refName) =>
        repo.Rulesets.Where(r => r.Enforcement == "active" &&
            r.Target == (refName.StartsWith("refs/tags/") ? "tag" : "branch") &&
            r.Include.Any(p => RefMatches(repo, p, refName)));
}
