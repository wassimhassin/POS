namespace Pos.Core.Bridge;

/// <summary>Origin helpers used to accept bridge messages and navigations only from the trusted UI.</summary>
public static class BridgeOrigin
{
    /// <summary>Returns the normalized "scheme://host[:port]" of an http(s) URI, or null.</summary>
    public static string? GetOrigin(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return null;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return null;
        }

        return parsed.GetLeftPart(UriPartial.Authority);
    }

    public static bool IsAllowed(string? uri, IReadOnlySet<string> allowedOrigins)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);
        return GetOrigin(uri) is { } origin && allowedOrigins.Contains(origin);
    }
}
