using System.Net;

namespace GitHubMigrator.Business;

// Egress policy for self-hosted providers. A user-supplied URL is the main SSRF risk of
// on-premises support, so only explicitly allow-listed hosts are ever contacted.
public sealed class ServerEndpointPolicy(IEnumerable<string> allowedHosts, bool allowHttp = false)
{
    private readonly string[] _hosts = allowedHosts
        .Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).ToArray();

    public bool Enabled => _hosts.Length > 0;
    public bool AllowHttp => allowHttp;

    public static ServerEndpointPolicy FromConfiguration(string? hosts, bool allowHttp) =>
        new((hosts ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries), allowHttp);

    // Returns the canonical server root without trailing slash, or throws ArgumentException.
    public string Normalize(string? value)
    {
        if (!Enabled) throw new ArgumentException("Self-hosted providers are disabled: no allowed hosts configured.");
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("Invalid server URL.");
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp))
            throw new ArgumentException("Server URL must use https.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Server URL must not contain credentials, query or fragment.");
        var host = uri.IdnHost.ToLowerInvariant();
        if (IsBlockedAddress(host) || !_hosts.Any(allowed => Matches(allowed, host)))
            throw new ArgumentException("Server host is not allowed.");
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static bool Matches(string allowed, string host) =>
        allowed.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(allowed[1..], StringComparison.Ordinal)
            : host == allowed;

    // Link-local ranges include cloud metadata endpoints and are never legitimate servers.
    private static bool IsBlockedAddress(string host)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        if (IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip)) return true;
        if (ip.IsIPv6LinkLocal) return true;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}
