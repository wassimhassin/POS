using System.Collections;

namespace Pos.Core.Hosting;

/// <summary>
/// WebView2 honours WEBVIEW2_* environment variables (e.g. WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
/// can add --remote-debugging-port, WEBVIEW2_BROWSER_EXECUTABLE_FOLDER can swap the browser).
/// Anyone able to set an environment variable for the POS user could therefore expose the UI and
/// the native bridge over the DevTools protocol. Release builds remove these variables from the
/// process before the WebView2 environment is created; child browser processes inherit the
/// cleaned environment.
/// </summary>
public static class WebView2EnvironmentGuard
{
    public const string VariablePrefix = "WEBVIEW2_";

    // Substrings so that "-remote-debugging-port", "--remote-debugging-port=0" and
    // "--remote-debugging-pipe" are all caught.
    private static readonly string[] DebuggingSwitchFragments =
    [
        "remote-debugging",
        "remote-allow-origins",
        "auto-open-devtools",
    ];

    /// <summary>Removes every WEBVIEW2_* variable from the current process. Returns removed names.</summary>
    public static IReadOnlyList<string> RemoveProcessOverrides()
    {
        var removed = new List<string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && name.StartsWith(VariablePrefix, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(name, null);
                removed.Add(name);
            }
        }

        removed.Sort(StringComparer.OrdinalIgnoreCase);
        return removed;
    }

    /// <summary>True when browser arguments would enable DevTools / remote debugging access.</summary>
    public static bool ContainsDebuggingSwitch(string? browserArguments) =>
        !string.IsNullOrEmpty(browserArguments)
        && DebuggingSwitchFragments.Any(f => browserArguments.Contains(f, StringComparison.OrdinalIgnoreCase));
}
