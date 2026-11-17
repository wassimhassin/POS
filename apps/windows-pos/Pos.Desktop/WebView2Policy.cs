using Microsoft.Win32;

namespace Pos.Desktop;

/// <summary>
/// Reads the WebView2 "AdditionalBrowserArguments" group policy (HKLM/HKCU\Software\Policies\Microsoft\Edge\WebView2).
/// Policies are admin-controlled and cannot be overridden by the app, so Release builds refuse to
/// start when a policy forces remote debugging for this executable.
/// </summary>
internal static class WebView2Policy
{
    private const string KeyPath = @"Software\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments";

    public static IEnumerable<string> ReadAdditionalBrowserArguments(string exeName)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(KeyPath);
            if (key is null)
            {
                continue;
            }

            foreach (var valueName in new[] { exeName, "*" })
            {
                if (key.GetValue(valueName) is string arguments)
                {
                    yield return arguments;
                }
            }
        }
    }
}
