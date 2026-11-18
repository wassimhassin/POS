using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pos.Core;
using Pos.Core.Data;
using Pos.Core.Gateway;

namespace Pos.Gateway;

public sealed record GatewayIdentity(string BranchId, string BranchName, string CreatedAt);

public sealed record TerminalRecord(string Id, string BranchId, string Name, byte[] PublicKey, string Status, string EnrolledAt, string? RevokedAt, string? LastSeenAt);

/// <summary>
/// The gateway's SQLite file. It reuses the POS schema (catalog, print destinations, print queue) so
/// the existing printing subsystem runs unchanged, and adds the gateway tables on top. Single writer:
/// all shared-state mutations go through <see cref="OrderService"/> under one lock.
/// </summary>
public sealed class GatewayStore
{
    public const string DatabaseFileName = "gateway.db";

    // Append-only, tracked in gateway_schema (PRAGMA user_version belongs to the POS schema).
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE gateway_identity (
            id          INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            branch_id   TEXT    NOT NULL,
            branch_name TEXT    NOT NULL,
            created_at  TEXT    NOT NULL
        );

        CREATE TABLE terminals (
            id          TEXT NOT NULL PRIMARY KEY,
            branch_id   TEXT NOT NULL,
            name        TEXT NOT NULL,
            public_key  BLOB NOT NULL,
            status      TEXT NOT NULL CHECK (status IN ('active', 'revoked')),
            enrolled_at TEXT NOT NULL,
            revoked_at  TEXT NULL,
            last_seen_at TEXT NULL
        );

        -- One-time pairing codes; only a SHA-256 hash is stored.
        CREATE TABLE enrollment_codes (
            code_hash     TEXT NOT NULL PRIMARY KEY,
            terminal_name TEXT NOT NULL,
            created_at    TEXT NOT NULL,
            expires_at    TEXT NOT NULL,
            used_at       TEXT NULL,
            used_by       TEXT NULL REFERENCES terminals (id)
        );

        CREATE TABLE shared_orders (
            id           TEXT    NOT NULL PRIMARY KEY,
            branch_id    TEXT    NOT NULL,
            number       INTEGER NOT NULL UNIQUE,
            label        TEXT    NULL,
            status       TEXT    NOT NULL CHECK (status IN ('open', 'submitted')),
            version      INTEGER NOT NULL CHECK (version >= 1),
            currency     TEXT    NOT NULL,
            created_by   TEXT    NOT NULL REFERENCES terminals (id),
            created_at   TEXT    NOT NULL,
            updated_by   TEXT    NOT NULL REFERENCES terminals (id),
            updated_at   TEXT    NOT NULL,
            submitted_by TEXT    NULL REFERENCES terminals (id),
            submitted_at TEXT    NULL
        );

        CREATE TABLE shared_order_lines (
            order_id         TEXT    NOT NULL REFERENCES shared_orders (id),
            product_id       TEXT    NOT NULL REFERENCES products (id),
            line_no          INTEGER NOT NULL,
            product_name     TEXT    NOT NULL,
            unit_price_minor INTEGER NOT NULL CHECK (unit_price_minor >= 0),
            quantity         INTEGER NOT NULL CHECK (quantity BETWEEN 1 AND 999),
            PRIMARY KEY (order_id, product_id)
        );

        -- Ordered operation journal + idempotency store. position is the branch log cursor.
        CREATE TABLE operations (
            position         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            operation_id     TEXT    NOT NULL UNIQUE,
            terminal_id      TEXT    NOT NULL REFERENCES terminals (id),
            type             TEXT    NOT NULL,
            status           TEXT    NOT NULL CHECK (status IN ('applied', 'abandoned')),
            order_id         TEXT    NULL REFERENCES shared_orders (id),
            expected_version INTEGER NULL,
            result_version   INTEGER NULL,
            request_hash     TEXT    NULL,
            response_json    TEXT    NULL,
            at               TEXT    NOT NULL
        );

        CREATE INDEX ix_operations_order ON operations (order_id, position);

        -- At most ONE print intent per (order, station), enforced by the primary key even if the
        -- idempotency layer were bypassed. Each intent is a durable print_jobs row (Phase 1.2 queue).
        CREATE TABLE print_intents (
            order_id     TEXT NOT NULL REFERENCES shared_orders (id),
            station_id   TEXT NOT NULL REFERENCES print_destinations (id),
            print_job_id TEXT NOT NULL UNIQUE REFERENCES print_jobs (id),
            operation_id TEXT NOT NULL REFERENCES operations (operation_id),
            created_at   TEXT NOT NULL,
            PRIMARY KEY (order_id, station_id)
        );
        """,
    ];

    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789"; // no I/L/O/U/0/1 look-alikes
    private const int CodeLength = 20;

    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public GatewayStore(string dataDir, ILoggerFactory loggerFactory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        DataDir = Path.GetFullPath(dataDir);
        _logger = loggerFactory.CreateLogger<GatewayStore>();
        _time = timeProvider ?? TimeProvider.System;
        Database = new PosDatabase(Path.Combine(DataDir, DatabaseFileName), loggerFactory.CreateLogger<PosDatabase>());
    }

    public string DataDir { get; }

    public PosDatabase Database { get; }

    public static int SchemaVersion => Migrations.Length;

    /// <summary>Creates/upgrades both schemas and runs the SQLite integrity check (refuses a corrupt file).</summary>
    public void Initialize()
    {
        Database.Initialize();
        using var connection = Database.OpenConnection();
        Execute(connection, null, "CREATE TABLE IF NOT EXISTS gateway_schema (version INTEGER NOT NULL);");
        var version = Convert.ToInt32(Scalar(connection, null, "SELECT COALESCE(MAX(version), 0) FROM gateway_schema;"), CultureInfo.InvariantCulture);
        if (version > Migrations.Length)
        {
            throw new InvalidOperationException($"Gateway schema version {version} is newer than this gateway supports ({Migrations.Length}).");
        }

        for (var next = version; next < Migrations.Length; next++)
        {
            using var tx = connection.BeginTransaction();
            Execute(connection, tx, Migrations[next]);
            Execute(connection, tx, $"INSERT INTO gateway_schema (version) VALUES ({next + 1});");
            tx.Commit();
            _logger.LogInformation("Applied gateway migration {GatewaySchemaVersion}", next + 1);
        }
    }

    public string Now() => _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    public GatewayIdentity? GetIdentity()
    {
        using var connection = Database.OpenConnection();
        using var query = Command(connection, null, "SELECT branch_id, branch_name, created_at FROM gateway_identity WHERE id = 1;");
        using var reader = query.ExecuteReader();
        return reader.Read() ? new GatewayIdentity(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    /// <summary>Provisions the branch this gateway serves. A gateway serves exactly one branch, forever.</summary>
    public GatewayIdentity InitIdentity(string branchId, string branchName)
    {
        if (!GatewayProtocol.IsId(branchId))
        {
            throw new PosValidationException("invalid_branch", "L'identifiant de succursale doit être un GUID.");
        }

        branchName = branchName.Trim();
        if (branchName.Length is 0 or > 80)
        {
            throw new PosValidationException("invalid_branch", "Nom de succursale invalide (1 à 80 caractères).");
        }

        var existing = GetIdentity();
        if (existing is not null)
        {
            return existing.BranchId == branchId
                ? existing
                : throw new PosValidationException("already_initialized", $"Cette passerelle est déjà rattachée à la succursale {existing.BranchId}.");
        }

        using var connection = Database.OpenConnection();
        using var insert = Command(connection, null, "INSERT INTO gateway_identity (id, branch_id, branch_name, created_at) VALUES (1, $id, $name, $now);");
        insert.Parameters.AddWithValue("$id", branchId);
        insert.Parameters.AddWithValue("$name", branchName);
        insert.Parameters.AddWithValue("$now", Now());
        insert.ExecuteNonQuery();
        _logger.LogInformation("Gateway initialized for branch {BranchId} {BranchName}", branchId, branchName);
        return GetIdentity()!;
    }

    /// <summary>Creates a one-time pairing code (shown once to the administrator; only its hash is stored).</summary>
    public (string Code, DateTimeOffset ExpiresAt) CreateEnrollmentCode(string terminalName, TimeSpan validity)
    {
        terminalName = terminalName.Trim();
        if (terminalName.Length is 0 or > 40)
        {
            throw new PosValidationException("invalid_name", "Nom de terminal invalide (1 à 40 caractères).");
        }

        var chars = new char[CodeLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        var code = new string(chars);
        var expiresAt = _time.GetUtcNow().Add(validity);
        using var connection = Database.OpenConnection();
        using var insert = Command(connection, null, """
            INSERT INTO enrollment_codes (code_hash, terminal_name, created_at, expires_at) VALUES ($hash, $name, $now, $expires);
            """);
        insert.Parameters.AddWithValue("$hash", HashCode(code));
        insert.Parameters.AddWithValue("$name", terminalName);
        insert.Parameters.AddWithValue("$now", Now());
        insert.Parameters.AddWithValue("$expires", expiresAt.ToString("O", CultureInfo.InvariantCulture));
        insert.ExecuteNonQuery();
        _logger.LogInformation("Enrollment code created for terminal {TerminalName}, expires {ExpiresAt}", terminalName, expiresAt);
        return (string.Join('-', code.Chunk(5).Select(c => new string(c))), expiresAt);
    }

    /// <summary>Consumes a pairing code and registers the terminal's public key, atomically.</summary>
    internal TerminalRecord? EnrollTerminal(string code, string terminalId, byte[] publicKey, string branchId)
    {
        using var connection = Database.OpenConnection();
        using var tx = connection.BeginTransaction();
        using var find = Command(connection, tx, """
            SELECT terminal_name FROM enrollment_codes WHERE code_hash = $hash AND used_at IS NULL AND expires_at > $now;
            """);
        find.Parameters.AddWithValue("$hash", HashCode(GatewayProtocol.NormalizeCode(code)));
        find.Parameters.AddWithValue("$now", Now());
        if (find.ExecuteScalar() is not string name)
        {
            return null;
        }

        using var exists = Command(connection, tx, "SELECT COUNT(*) FROM terminals WHERE id = $id;");
        exists.Parameters.AddWithValue("$id", terminalId);
        if (Convert.ToInt32(exists.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
        {
            throw new PosValidationException("terminal_exists", "Ce terminal est déjà enregistré. Révoquez-le puis réinstallez-le avec une nouvelle identité.");
        }

        var now = Now();
        using var insert = Command(connection, tx, """
            INSERT INTO terminals (id, branch_id, name, public_key, status, enrolled_at) VALUES ($id, $branch, $name, $key, 'active', $now);
            UPDATE enrollment_codes SET used_at = $now, used_by = $id WHERE code_hash = $hash;
            """);
        insert.Parameters.AddWithValue("$id", terminalId);
        insert.Parameters.AddWithValue("$branch", branchId);
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$key", publicKey);
        insert.Parameters.AddWithValue("$now", now);
        insert.Parameters.AddWithValue("$hash", HashCode(GatewayProtocol.NormalizeCode(code)));
        insert.ExecuteNonQuery();
        tx.Commit();
        _logger.LogInformation("Terminal enrolled {TerminalId} {TerminalName}", terminalId, name);
        return GetTerminal(terminalId);
    }

    public TerminalRecord? GetTerminal(string terminalId)
    {
        using var connection = Database.OpenConnection();
        using var query = Command(connection, null, $"{TerminalSelect} WHERE id = $id;");
        query.Parameters.AddWithValue("$id", terminalId);
        return ReadTerminals(query).FirstOrDefault();
    }

    public IReadOnlyList<TerminalRecord> ListTerminals()
    {
        using var connection = Database.OpenConnection();
        using var query = Command(connection, null, $"{TerminalSelect} ORDER BY enrolled_at;");
        return ReadTerminals(query);
    }

    public bool RevokeTerminal(string terminalId)
    {
        using var connection = Database.OpenConnection();
        using var update = Command(connection, null, "UPDATE terminals SET status = 'revoked', revoked_at = $now WHERE id = $id AND status = 'active';");
        update.Parameters.AddWithValue("$id", terminalId);
        update.Parameters.AddWithValue("$now", Now());
        var revoked = update.ExecuteNonQuery() == 1;
        if (revoked)
        {
            _logger.LogWarning("Terminal revoked {TerminalId}", terminalId);
        }

        return revoked;
    }

    public void TouchTerminal(string terminalId)
    {
        using var connection = Database.OpenConnection();
        using var update = Command(connection, null, "UPDATE terminals SET last_seen_at = $now WHERE id = $id;");
        update.Parameters.AddWithValue("$id", terminalId);
        update.Parameters.AddWithValue("$now", Now());
        update.ExecuteNonQuery();
    }

    /// <summary>Transactionally consistent online copy (SQLite backup API), verified with integrity_check.</summary>
    public void Backup(string destinationFile)
    {
        var target = Path.GetFullPath(destinationFile);
        if (File.Exists(target))
        {
            throw new PosValidationException("backup_exists", $"Le fichier existe déjà : {target}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var source = Database.OpenConnection())
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
            var check = Convert.ToString(Scalar(destination, null, "PRAGMA integrity_check;"), CultureInfo.InvariantCulture);
            if (check != "ok")
            {
                throw new InvalidOperationException($"Backup integrity check failed: {check}");
            }
        }

        _logger.LogInformation("Backup written {BackupFile}", target);
    }

    internal static string HashCode(string normalizedCode) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode)));

    private const string TerminalSelect = "SELECT id, branch_id, name, public_key, status, enrolled_at, revoked_at, last_seen_at FROM terminals";

    private static List<TerminalRecord> ReadTerminals(SqliteCommand query)
    {
        using var reader = query.ExecuteReader();
        var list = new List<TerminalRecord>();
        while (reader.Read())
        {
            list.Add(new TerminalRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), (byte[])reader.GetValue(3), reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return list;
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command;
    }

    internal static void Execute(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var command = Command(connection, tx, sql);
        command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var command = Command(connection, tx, sql);
        return command.ExecuteScalar();
    }
}
