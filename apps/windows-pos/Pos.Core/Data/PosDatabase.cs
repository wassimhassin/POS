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

        // v2 — Phase 1.2 printing: destinations (receipt + preparation stations), product routing,
        // durable print-job queue with an append-only event trail.
        """
        CREATE TABLE print_destinations (
            id             TEXT    NOT NULL PRIMARY KEY,
            name           TEXT    NOT NULL,
            kind           TEXT    NOT NULL CHECK (kind IN ('receipt', 'station')),
            printer_name   TEXT    NULL,
            paper_width_mm INTEGER NOT NULL DEFAULT 80 CHECK (paper_width_mm IN (58, 80)),
            sort_order     INTEGER NOT NULL DEFAULT 0
        );

        INSERT INTO print_destinations (id, name, kind, paper_width_mm, sort_order) VALUES
            ('receipt', 'Ticket client', 'receipt', 80, 0),
            ('kitchen', 'Cuisine',       'station', 80, 1),
            ('bar',     'Bar',           'station', 58, 2),
            ('counter', 'Comptoir',      'station', 58, 3);

        ALTER TABLE products ADD COLUMN station_id TEXT NULL REFERENCES print_destinations (id);

        UPDATE products SET station_id = CASE category
            WHEN 'Boissons chaudes'  THEN 'bar'
            WHEN 'Boissons fraîches' THEN 'bar'
            WHEN 'Viennoiseries'     THEN 'counter'
            ELSE 'kitchen'
        END;

        CREATE TABLE print_jobs (
            id              TEXT    NOT NULL PRIMARY KEY,
            kind            TEXT    NOT NULL CHECK (kind IN ('receipt', 'station')),
            destination_id  TEXT    NOT NULL REFERENCES print_destinations (id),
            order_id        TEXT    NOT NULL,
            order_number    TEXT    NOT NULL,
            source_json     TEXT    NOT NULL,
            paper_width_mm  INTEGER NOT NULL CHECK (paper_width_mm IN (58, 80)),
            copy_number     INTEGER NOT NULL DEFAULT 0 CHECK (copy_number >= 0),
            reprint_of      TEXT    NULL REFERENCES print_jobs (id),
            status          TEXT    NOT NULL CHECK (status IN ('queued', 'printing', 'sent', 'failed', 'uncertain')),
            attempts        INTEGER NOT NULL DEFAULT 0,
            auto_attempts   INTEGER NOT NULL DEFAULT 0,
            next_attempt_at TEXT    NULL,
            last_error      TEXT    NULL,
            last_printer    TEXT    NULL,
            spooler_job_id  INTEGER NULL,
            created_at      TEXT    NOT NULL,
            updated_at      TEXT    NOT NULL,
            sent_at         TEXT    NULL
        );

        CREATE INDEX ix_print_jobs_status ON print_jobs (status, next_attempt_at);
        CREATE INDEX ix_print_jobs_order ON print_jobs (order_id, kind, destination_id);

        CREATE TABLE print_job_events (
            id     INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            job_id TEXT    NOT NULL REFERENCES print_jobs (id),
            at     TEXT    NOT NULL,
            event  TEXT    NOT NULL,
            detail TEXT    NULL
        );

        CREATE INDEX ix_print_job_events_job ON print_job_events (job_id, id);
        """,
    ];

    /// <summary>For migration tests: the SQL of each schema version.</summary>
    internal static IReadOnlyList<string> MigrationScripts => Migrations;

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
