using System.IO;

namespace Pos.Desktop;

/// <summary>
/// File locations. Data defaults to %LOCALAPPDATA%\PosSaaS and can be redirected with the
/// POS_DATA_DIR environment variable (used by the end-to-end restart test).
/// </summary>
internal sealed record AppPaths(string DataDir, string DatabaseFile, string LogsDir, string WebViewDataDir, string WebRoot)
{
    public const string DataDirVariable = "POS_DATA_DIR";

    public static AppPaths Resolve()
    {
        var dataDir = Environment.GetEnvironmentVariable(DataDirVariable);
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PosSaaS");
        }

        dataDir = Path.GetFullPath(dataDir);
        return new AppPaths(
            dataDir,
            Path.Combine(dataDir, "pos.db"),
            Path.Combine(dataDir, "logs"),
            Path.Combine(dataDir, "WebView2"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot"));
    }
}
