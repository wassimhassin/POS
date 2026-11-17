using Pos.Core.Hosting;

namespace Pos.Core.Tests;

public sealed class WebView2EnvironmentGuardTests
{
    [Fact]
    public void Removes_all_webview2_overrides_and_keeps_other_variables()
    {
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--remote-debugging-port=9222");
        Environment.SetEnvironmentVariable("webview2_browser_executable_folder", @"C:\evil");
        Environment.SetEnvironmentVariable("POS_GUARD_TEST_KEEP", "1");
        try
        {
            var removed = WebView2EnvironmentGuard.RemoveProcessOverrides();

            Assert.Contains("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", removed, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", removed, StringComparer.OrdinalIgnoreCase);
            Assert.Null(Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"));
            Assert.Null(Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER"));
            Assert.Equal("1", Environment.GetEnvironmentVariable("POS_GUARD_TEST_KEEP"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("POS_GUARD_TEST_KEEP", null);
        }
    }

    [Theory]
    [InlineData("--remote-debugging-port=9222", true)]
    [InlineData("-remote-debugging-port=0", true)]
    [InlineData("--REMOTE-DEBUGGING-PIPE", true)]
    [InlineData("--disable-gpu --remote-allow-origins=*", true)]
    [InlineData("--auto-open-devtools-for-tabs", true)]
    [InlineData("--disable-gpu --lang=fr", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Detects_debugging_switches(string? arguments, bool expected) =>
        Assert.Equal(expected, WebView2EnvironmentGuard.ContainsDebuggingSwitch(arguments));
}
