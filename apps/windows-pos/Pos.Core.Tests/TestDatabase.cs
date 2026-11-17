using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Core.Data;

namespace Pos.Core.Tests;

/// <summary>Creates an isolated on-disk SQLite database in a temp folder.</summary>
internal sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        Directory = Path.Combine(Path.GetTempPath(), "pos-tests", Guid.NewGuid().ToString("N"));
        FilePath = Path.Combine(Directory, "pos.db");
        Database = Open();
    }

    public string Directory { get; }

    public string FilePath { get; }

    public PosDatabase Database { get; private set; }

    /// <summary>Simulates an application restart: drops pooled connections and reopens the file.</summary>
    public PosDatabase Restart()
    {
        SqliteConnection.ClearAllPools();
        Database = Open();
        return Database;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of temp files.
        }
    }

    private PosDatabase Open()
    {
        var db = new PosDatabase(FilePath, NullLogger<PosDatabase>.Instance);
        db.Initialize();
        return db;
    }
}
