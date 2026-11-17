using System.Text;

namespace Pos.Core.Tests;

/// <summary>
/// Guards against UTF-8 text being re-saved through a legacy code page (e.g. Windows PowerShell 5.1),
/// which silently turns "é" into "Ã©" in user-facing French strings.
/// </summary>
public sealed class SourceEncodingTests
{
    private static readonly string[] Extensions = [".cs", ".xaml", ".ts", ".tsx", ".css", ".html", ".mjs", ".md", ".ps1"];
    private static readonly string[] Excluded = ["node_modules", "bin", "obj", "dist", ".git"];

    // Lead bytes of 2-byte UTF-8 sequences decoded as Windows-1252, followed by a continuation char.
    private static readonly string[] MojibakeMarkers = ["Ã©", "Ã¨", "Ã ", "Ãª", "Ã®", "Ã§", "Ã‰", "â€", "Â "];

    [Fact]
    public void Source_files_are_valid_utf8_without_mojibake()
    {
        var root = FindRepoRoot();
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var offenders = new List<string>();

        foreach (var file in EnumerateSources(root))
        {
            string text;
            try
            {
                text = strict.GetString(File.ReadAllBytes(file));
            }
            catch (DecoderFallbackException)
            {
                offenders.Add($"{Path.GetRelativePath(root, file)}: invalid UTF-8");
                continue;
            }

            var marker = MojibakeMarkers.FirstOrDefault(m => text.Contains(m, StringComparison.Ordinal));
            if (marker is not null)
            {
                offenders.Add($"{Path.GetRelativePath(root, file)}: contains '{marker}'");
            }
        }

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> EnumerateSources(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                && !file.EndsWith(nameof(SourceEncodingTests) + ".cs", StringComparison.Ordinal))
            {
                yield return file;
            }
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (!Excluded.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
            {
                foreach (var file in EnumerateSources(sub))
                {
                    yield return file;
                }
            }
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PosSaaS.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (PosSaaS.slnx) not found.");
    }
}
