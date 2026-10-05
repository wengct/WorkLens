using System.Net;

namespace WorkLens.Services;

public static class McpLocalAccess
{
    public static bool IsAllowed(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null || !IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)) return false;
        if (!IsLocalHost(context.Request.Host.Host)) return false;
        if (!context.Request.Headers.TryGetValue("Origin", out var origin)) return true;
        return Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri)
            && uri.Scheme == context.Request.Scheme && IsLocalHost(uri.Host)
            && uri.Port == (context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80));
    }

    private static bool IsLocalHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
}
