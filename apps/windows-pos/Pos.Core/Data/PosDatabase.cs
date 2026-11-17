using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Pos.Core.Data;

/// <summary>
/// Owns the local SQLite file: connection settings, schema migrations, integrity check and seeding.
/// </summary>
public sealed class PosDatabase
{
    // Append-only. Each entry upgrades the schema by one version (tracked in PRAGMA user_version).
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE products (
            id          TEXT    NOT NULL PRIMARY KEY,
            sku         TEXT    NOT NULL UNIQUE,
            name        TEXT    NOT NULL,
            category    TEXT    NOT NULL,
            price_minor INTEGER NOT NULL CHECK (price_minor >= 0),
            currency    TEXT    NOT NULL,
            sort_order  INTEGER NOT NULL DEFAULT 0,
            is_active   INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1))
        );

        CREATE TABLE draft_orders (
            id         TEXT NOT NULL PRIMARY KEY,
            currency   TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE draft_order_lines (
            order_id         TEXT    NOT NULL REFERENCES draft_orders (id) ON DELETE CASCADE,
            line_no          INTEGER NOT NULL,
            product_id       TEXT    NOT NULL REFERENCES products (id),
            product_name     TEXT    NOT NULL,
            unit_price_minor INTEGER NOT NULL CHECK (unit_price_minor >= 0),
            quantity         INTEGER NOT NULL CHECK (quantity BETWEEN 1 AND 999),
            PRIMARY KEY (order_id, line_no)
        );

        CREATE INDEX ix_draft_orders_updated_at ON draft_orders (updated_at);
        """,
    ];

    private readonly string _connectionString;
    private readonly ILogger _logger;

    public PosDatabase(string filePath, ILogger<PosDatabase> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);
        FilePath = Path.GetFullPath(filePath);
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public string FilePath { get; }

    public static int SchemaVersion => Migrations.Length;

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // FULL: a commit acknowledged to the UI survives power loss, not just process crash.
        Execute(connection, null, "PRAGMA synchronous = FULL;");
        return connection;
    }

    /// <summary>Creates/upgrades the schema, verifies integrity and seeds sample data. Idempotent.</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var connection = OpenConnection();

        Execute(connection, null, "PRAGMA journal_mode = WAL;");

        var integrity = Convert.ToString(Scalar(connection, null, "PRAGMA quick_check;"), CultureInfo.InvariantCulture);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"SQLite integrity check failed for '{FilePath}': {integrity}");
        }

        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > Migrations.Length)
        {
            throw new InvalidOperationException(
                $"Database schema version {version} is newer than this application supports ({Migrations.Length}).");
        }

        for (var next = version; next < Migrations.Length; next++)
        {
            using var tx = connection.BeginTransaction();
            Execute(connection, tx, Migrations[next]);
            Execute(connection, tx, $"PRAGMA user_version = {next + 1};");
            tx.Commit();
            _logger.LogInformation("Applied database migration {SchemaVersion}", next + 1);
        }

        var seeded = SampleData.SeedIfEmpty(connection);
        if (seeded > 0)
        {
            _logger.LogInformation("Seeded {ProductCount} sample products", seeded);
        }

        _logger.LogInformation("Database ready {DatabasePath} {SchemaVersion}", FilePath, Migrations.Length);
    }

    internal static void Execute(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
