using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pos.Core.Logging;

namespace Pos.Core.Tests;

public sealed class JsonFileLoggerProviderTests
{
    [Fact]
    public void Writes_one_structured_json_object_per_line()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pos-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = new JsonFileLoggerProvider(dir))
            {
                var logger = provider.CreateLogger("Test.Category");
                logger.LogInformation("Saved draft {OrderId} with {LineCount} lines", "o-1", 3);
                logger.LogDebug("Below minimum level, not written");
                logger.LogError(new InvalidOperationException("kaput"), "Failure for {OrderId}", "o-2");
            }

            var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(dir, "pos-*.jsonl")));
            Assert.Equal(2, lines.Length);

            using var info = JsonDocument.Parse(lines[0]);
            Assert.Equal("Information", info.RootElement.GetProperty("level").GetString());
            Assert.Equal("Test.Category", info.RootElement.GetProperty("category").GetString());
            Assert.Equal("Saved draft o-1 with 3 lines", info.RootElement.GetProperty("message").GetString());
            Assert.Equal("o-1", info.RootElement.GetProperty("props").GetProperty("OrderId").GetString());
            Assert.Equal(3, info.RootElement.GetProperty("props").GetProperty("LineCount").GetInt32());

            using var error = JsonDocument.Parse(lines[1]);
            Assert.Equal("System.InvalidOperationException", error.RootElement.GetProperty("exception").GetProperty("type").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
