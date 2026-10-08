using System.Net;
using System.Net.Sockets;

namespace Cuelight.Core;

/// <summary>
/// Checks the address of an OpenAI-style service before the conversation (and an API key) is sent to it.
/// https:// is always fine. Plain http:// is only accepted for a service on this PC or on your own network
/// (Ollama and the like), because anywhere else it would send the transcript and the key across the
/// internet unencrypted.
/// </summary>
public static class ServiceAddress
{
    public const string DoesntLookRight =
        "That address doesn't look right. It should start with https:// (or http:// for a service on this PC or your own network).";

    public const string PlainHttp =
        "That address starts with http://, which would send your key and the conversation unencrypted. Use https:// instead (http:// is only accepted for a service on this PC or your own network).";

    /// <summary>Null when the address is fine to use; otherwise a plain-language reason it isn't.</summary>
    public static string? Problem(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return DoesntLookRight;
        if (uri.Scheme == "http" && !IsOnThisPcOrNetwork(uri.Host)) return PlainHttp;
        return null;
    }

    /// <summary>This PC, or a machine on a home or office network: not reachable from the open internet.</summary>
    public static bool IsOnThisPcOrNetwork(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return false;
        if (IPAddress.TryParse(host, out var ip)) return IsPrivate(ip);
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        if (!host.Contains('.')) return true;   // a bare machine name such as "gaming-pc" only resolves on a local network
        return host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".lan", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal) || host.EndsWith(".home.arpa", StringComparison.Ordinal);
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;   // fc00::/7, the IPv6 private range
        return false;
    }
}
