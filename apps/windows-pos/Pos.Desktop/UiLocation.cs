namespace Pos.Desktop;

/// <summary>Where the React UI is loaded from.</summary>
internal static class UiLocation
{
    /// <summary>Virtual host mapped to the packaged wwwroot folder (.example is reserved, never resolves on the network).</summary>
    public const string VirtualHost = "pos-app.example";

    public static readonly Uri PackagedUri = new($"https://{VirtualHost}/index.html");

    /// <summary>
    /// Release builds always load the packaged UI. Debug builds accept --ui-url=http://localhost:5173
    /// to use the Vite dev server (loopback only).
    /// </summary>
    public static Uri Resolve(IEnumerable<string> args)
    {
#if DEBUG
        const string prefix = "--ui-url=";
        var arg = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
        if (arg is not null
            && Uri.TryCreate(arg[prefix.Length..], UriKind.Absolute, out var devUri)
            && devUri.IsLoopback
            && (devUri.Scheme == Uri.UriSchemeHttp || devUri.Scheme == Uri.UriSchemeHttps))
        {
            return devUri;
        }
#else
        _ = args;
#endif
        return PackagedUri;
    }

    public static bool IsPackaged(Uri uri) => string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase);
}
