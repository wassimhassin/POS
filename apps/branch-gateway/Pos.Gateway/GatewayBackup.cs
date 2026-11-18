using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pos.Core;
using Pos.Core.Data;
using Pos.Core.Gateway;

namespace Pos.Gateway;

/// <summary>Sidecar <c>&lt;backup&gt;.db.json</c>, written last: a backup without it is incomplete and never restored.</summary>
public sealed record BackupMetadata(
    int FormatVersion,
    string BranchId,
    string BranchName,
    string? CloudGatewayId,
    string CreatedAt,
    int GatewaySchemaVersion,
    int PosSchemaVersion,
    long OutboxLastSequence,
    long SyncAckedSequence,
    long LastOperationPosition,
    string Sha256,
    long SizeBytes,
    string SourceMachine,
    bool Offsite);

public sealed record BackupConfig(string Directory, int IntervalSeconds, int Keep);

public sealed record BackupRun(string StartedAt, string? FinishedAt, string Status, string? File, long? Sequence, bool? Offsite, string? Error);

/// <summary>Restore options. Every override is explicit and audited.</summary>
public sealed record RestoreOptions(bool DryRun, string? ExpectedBranchId, bool ReplaceNewer);

public sealed record RestorePlan(BackupMetadata Backup, string? CurrentState, long? CurrentOutboxSequence, long? CurrentUnsynced, bool BackupIsOlder, IReadOnlyList<string> Warnings);

/// <summary>
/// Backups (SQLite online backup API: transactionally consistent while the gateway serves), verification, retention,
/// and the controlled restore. Only one process may own a data directory at a time (<see cref="InstanceLock"/>).
/// </summary>
public static class GatewayBackup
{
    public const int FormatVersion = 1;
    public const string MetadataSuffix = ".json";
    public const string RestoreMarker = "restore-in-progress.json";

    // ------------------------------------------------------------------ backup

    public static BackupMetadata Create(GatewayStore store, string destinationFile)
    {
        ArgumentNullException.ThrowIfNull(store);
        var target = Path.GetFullPath(destinationFile);
        if (File.Exists(target) || File.Exists(target + MetadataSuffix))
        {
            throw new PosValidationException("backup_exists", $"Le fichier existe déjà : {target}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".partial";
        File.Delete(partial);
        BackupMetadata metadata;
        try
        {
            using (var source = store.Database.OpenConnection())
            using (var copy = Open(partial, readOnly: false))
            {
                source.BackupDatabase(copy);
                metadata = Describe(copy, "", 0, IsOffsite(store.DataDir, target));
            }

            SqliteConnection.ClearAllPools();
            var (sha, size) = Checksum(partial);
            metadata = metadata with { Sha256 = sha, SizeBytes = size };
            File.Move(partial, target);
            File.WriteAllText(target + MetadataSuffix + ".partial", JsonSerializer.Serialize(metadata, GatewayProtocol.Json));
            File.Move(target + MetadataSuffix + ".partial", target + MetadataSuffix);
        }
        finally
        {
            File.Delete(partial);
        }

        return metadata;
    }

    /// <summary>Runs one configured backup: records the outcome, applies retention. Never throws (a failure is reported).</summary>
    public static BackupRun RunConfigured(GatewayStore store, BackupConfig config, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);
        var started = store.Now();
        try
        {
            var identity = store.GetIdentity() ?? throw new InvalidOperationException("Passerelle non initialisée.");
            var name = $"gateway-{identity.BranchId}-{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmssfff'Z'}.db";
            var metadata = Create(store, Path.Combine(config.Directory, name));
            var deleted = ApplyRetention(config.Directory, identity.BranchId, config.Keep);
            var run = new BackupRun(started, store.Now(), "ok", Path.Combine(config.Directory, name), metadata.OutboxLastSequence, metadata.Offsite, null);
            Record(store, run, metadata.Sha256);
            BackupLog.Written(logger, run.File!, metadata.OutboxLastSequence, metadata.Offsite, deleted);
            if (!metadata.Offsite)
            {
                BackupLog.SameDisk(logger, config.Directory);
            }

            return run;
        }
#pragma warning disable CA1031 // a failed backup must be reported, never stop the gateway
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var run = new BackupRun(started, store.Now(), "failed", null, null, null, $"{ex.GetType().Name}: {ex.Message}");
            try
            {
                Record(store, run, null);
            }
#pragma warning disable CA1031
            catch (Exception recordError)
#pragma warning restore CA1031
            {
                BackupLog.RecordFailed(logger, recordError);
            }

            BackupLog.Failed(logger, ex);
            return run;
        }
    }

    /// <summary>Keeps the newest <paramref name="keep"/> complete backups of this branch in the directory; deletes older ones.</summary>
    public static int ApplyRetention(string directory, string branchId, int keep)
    {
        var backups = Directory.GetFiles(directory, $"gateway-{branchId}-*.db")
            .Where(f => File.Exists(f + MetadataSuffix))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
        var deleted = 0;
        foreach (var old in backups.Skip(keep))
        {
            File.Delete(old + MetadataSuffix);
            File.Delete(old);
            deleted++;
        }

        return deleted;
    }

    /// <summary>
    /// Off-site = another volume than the gateway's data (UNC share, other drive). A copy on the same disk does not
    /// protect against a disk or PC failure and is reported as such. (A different drive letter may still be the same
    /// physical disk; only the administrator can confirm true off-site storage.)
    /// </summary>
    public static bool IsOffsite(string dataDir, string backupPath)
    {
        var data = Path.GetPathRoot(Path.GetFullPath(dataDir));
        var target = Path.GetPathRoot(Path.GetFullPath(backupPath));
        return !string.Equals(data, target, StringComparison.OrdinalIgnoreCase);
    }

    public static BackupConfig? GetConfig(GatewayStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        using var connection = store.Database.OpenConnection();
        using var query = GatewayStore.Command(connection, null, "SELECT directory, interval_seconds, keep FROM backup_config WHERE id = 1;");
        using var reader = query.ExecuteReader();
        return reader.Read() ? new BackupConfig(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)) : null;
    }

    public static BackupConfig Configure(GatewayStore store, string directory, int intervalSeconds, int keep)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (intervalSeconds is < 10 or > 604800 || keep is < 1 or > 1000)
        {
            throw new PosValidationException("invalid_backup_config", "Intervalle 10 s à 7 jours, conservation 1 à 1000 sauvegardes.");
        }

        var full = Path.GetFullPath(directory);
        if (full.StartsWith(Path.GetFullPath(store.DataDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new PosValidationException("backup_inside_data", "Le dossier de sauvegarde ne peut pas être dans le dossier de données de la passerelle.");
        }

        Directory.CreateDirectory(full);
        using var connection = store.Database.OpenConnection();
        using var upsert = GatewayStore.Command(connection, null, """
            INSERT INTO backup_config (id, directory, interval_seconds, keep) VALUES (1, $d, $i, $k)
            ON CONFLICT (id) DO UPDATE SET directory = $d, interval_seconds = $i, keep = $k;
            """);
        upsert.Parameters.AddWithValue("$d", full);
        upsert.Parameters.AddWithValue("$i", intervalSeconds);
        upsert.Parameters.AddWithValue("$k", keep);
        upsert.ExecuteNonQuery();
        return new BackupConfig(full, intervalSeconds, keep);
    }

    public static (BackupRun? LastSuccess, BackupRun? LastFailure) LastRuns(GatewayStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        using var connection = store.Database.OpenConnection();
        BackupRun? Read(string status)
        {
            using var query = GatewayStore.Command(connection, null,
                "SELECT started_at, finished_at, status, file, sequence, offsite, error FROM backup_runs WHERE status = $s ORDER BY id DESC LIMIT 1;");
            query.Parameters.AddWithValue("$s", status);
            using var reader = query.ExecuteReader();
            return reader.Read()
                ? new BackupRun(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt64(5) == 1, reader.IsDBNull(6) ? null : reader.GetString(6))
                : null;
        }

        return (Read("ok"), Read("failed"));
    }

    private static void Record(GatewayStore store, BackupRun run, string? sha256)
    {
        using var connection = store.Database.OpenConnection();
        using var insert = GatewayStore.Command(connection, null, """
            INSERT INTO backup_runs (started_at, finished_at, status, file, sha256, sequence, offsite, error) VALUES ($s, $f, $st, $file, $sha, $seq, $off, $err);
            """);
        insert.Parameters.AddWithValue("$s", run.StartedAt);
        insert.Parameters.AddWithValue("$f", (object?)run.FinishedAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("$st", run.Status);
        insert.Parameters.AddWithValue("$file", (object?)run.File ?? DBNull.Value);
        insert.Parameters.AddWithValue("$sha", (object?)sha256 ?? DBNull.Value);
        insert.Parameters.AddWithValue("$seq", (object?)run.Sequence ?? DBNull.Value);
        insert.Parameters.AddWithValue("$off", run.Offsite is { } o ? (o ? 1 : 0) : DBNull.Value);
        insert.Parameters.AddWithValue("$err", (object?)run.Error ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ validation

    /// <summary>Checks a backup: metadata present, checksum, SQLite integrity, identity and schema inside the file match the metadata.</summary>
    public static BackupMetadata Validate(string backupFile)
    {
        var file = Path.GetFullPath(backupFile);
        if (!File.Exists(file))
        {
            throw new PosValidationException("backup_missing", $"Sauvegarde introuvable : {file}");
        }

        if (!File.Exists(file + MetadataSuffix))
        {
            throw new PosValidationException("backup_incomplete", "Métadonnées absentes : sauvegarde incomplète ou non produite par la passerelle, refusée.");
        }

        BackupMetadata metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<BackupMetadata>(File.ReadAllText(file + MetadataSuffix), GatewayProtocol.Json)
                ?? throw new PosValidationException("backup_metadata", "Métadonnées vides.");
        }
        catch (JsonException ex)
        {
            throw new PosValidationException("backup_metadata", $"Métadonnées illisibles : {ex.Message}");
        }

        if (metadata.FormatVersion != FormatVersion)
        {
            throw new PosValidationException("backup_format", $"Format de sauvegarde {metadata.FormatVersion} non pris en charge.");
        }

        var (sha, size) = Checksum(file);
        if (!string.Equals(sha, metadata.Sha256, StringComparison.OrdinalIgnoreCase) || size != metadata.SizeBytes)
        {
            throw new PosValidationException("backup_checksum", "Somme de contrôle SHA-256 différente : sauvegarde altérée ou corrompue, refusée.");
        }

        if (metadata.GatewaySchemaVersion > GatewayStore.SchemaVersion || metadata.PosSchemaVersion > PosDatabase.SchemaVersion)
        {
            throw new PosValidationException("backup_schema", $"Sauvegarde créée par une version plus récente (schéma {metadata.GatewaySchemaVersion}/{metadata.PosSchemaVersion}).");
        }

        try
        {
            using var connection = Open(file, readOnly: true);
            var check = Convert.ToString(GatewayStore.Scalar(connection, null, "PRAGMA integrity_check;"), CultureInfo.InvariantCulture);
            if (check != "ok")
            {
                throw new PosValidationException("backup_integrity", $"Contrôle d'intégrité SQLite en échec : {check}");
            }

            var inside = Describe(connection, metadata.Sha256, metadata.SizeBytes, metadata.Offsite);
            if (inside with { CreatedAt = metadata.CreatedAt, SourceMachine = metadata.SourceMachine, CloudGatewayId = metadata.CloudGatewayId } != metadata)
            {
                throw new PosValidationException("backup_metadata_mismatch", "Les métadonnées ne correspondent pas au contenu de la sauvegarde, refusée.");
            }
        }
        catch (SqliteException ex)
        {
            throw new PosValidationException("backup_integrity", $"Fichier SQLite illisible : {ex.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        return metadata;
    }

    // ------------------------------------------------------------------ restore

    /// <summary>
    /// Validates, compares with the current data, and (unless dry-run) replaces the database: current files are MOVED to
    /// forensic/&lt;stamp&gt;/ (never deleted), the backup is copied and re-verified, then the gateway is left in
    /// 'restored' state (shared writes blocked) with every unfinished print job marked uncertain. Resumable: a
    /// crash leaves a marker that blocks the gateway until the restore is run again.
    /// </summary>
    public static RestorePlan Restore(string dataDir, string backupFile, RestoreOptions options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        // A dry run only reads (validation + comparison): allowed while the gateway serves. A real restore is not.
        using var instance = options.DryRun ? null : InstanceLock.Acquire(dataDir, "restore");
        var logger = loggerFactory.CreateLogger("Pos.Gateway.Restore");
        var dbPath = Path.Combine(dataDir, GatewayStore.DatabaseFileName);
        var markerPath = Path.Combine(dataDir, RestoreMarker);
        var resuming = File.Exists(markerPath);
        if (resuming)
        {
            var previous = JsonSerializer.Deserialize<RestoreMarkerFile>(File.ReadAllText(markerPath), GatewayProtocol.Json)!;
            if (!string.Equals(Path.GetFullPath(previous.Backup), Path.GetFullPath(backupFile), StringComparison.OrdinalIgnoreCase))
            {
                throw new PosValidationException("restore_interrupted",
                    $"Une restauration de {previous.Backup} a été interrompue : relancez-la avec la même sauvegarde (copie forensique : {previous.Forensic}).");
            }
        }

        var metadata = Validate(backupFile);
        var warnings = new List<string>();
        if (!metadata.Offsite)
        {
            warnings.Add("La sauvegarde a été écrite sur le même disque que la passerelle : elle ne protégeait pas contre une panne du disque ou du PC.");
        }

        // Current data: identity, how far it got, and whether anything unsynchronized would be replaced.
        string? currentBranch = null;
        long? currentSequence = null;
        long? currentUnsynced = null;
        string? currentState = resuming ? "restauration interrompue" : null;
        var previousRun = resuming ? JsonSerializer.Deserialize<RestoreMarkerFile>(File.ReadAllText(markerPath), GatewayProtocol.Json)! : null;
        if (!resuming && File.Exists(dbPath))
        {
            try
            {
                using var connection = Open(dbPath, readOnly: true);
                if (Convert.ToString(GatewayStore.Scalar(connection, null, "PRAGMA integrity_check;"), CultureInfo.InvariantCulture) != "ok")
                {
                    currentState = "endommagée";
                }
                else
                {
                    currentBranch = GatewayStore.Scalar(connection, null, "SELECT branch_id FROM gateway_identity WHERE id = 1;") as string;
                    if (HasTable(connection, "sync_outbox"))
                    {
                        currentSequence = Convert.ToInt64(GatewayStore.Scalar(connection, null, "SELECT COALESCE(MAX(sequence), 0) FROM sync_outbox;"), CultureInfo.InvariantCulture);
                        currentUnsynced = Convert.ToInt64(GatewayStore.Scalar(connection, null,
                            "SELECT COUNT(*) FROM sync_outbox WHERE sequence > (SELECT acked_sequence FROM sync_state WHERE id = 1);"), CultureInfo.InvariantCulture);
                    }

                    currentState = "lisible";
                }
            }
            catch (SqliteException ex)
            {
                currentState = $"illisible ({ex.SqliteErrorCode})";
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        var expected = currentBranch ?? previousRun?.BranchId ?? options.ExpectedBranchId;
        if (expected is null)
        {
            throw new PosValidationException("branch_unknown",
                "Identité de la succursale inconnue (aucune base lisible ici) : précisez --branch-id <guid> pour confirmer la succursale à restaurer.");
        }

        if (!string.Equals(expected, metadata.BranchId, StringComparison.OrdinalIgnoreCase)
            || (options.ExpectedBranchId is not null && !string.Equals(options.ExpectedBranchId, metadata.BranchId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PosValidationException("wrong_branch", $"Cette sauvegarde appartient à la succursale {metadata.BranchId}, pas à {expected}. Refusée.");
        }

        var older = currentSequence is { } current && current > metadata.OutboxLastSequence;
        if (older)
        {
            warnings.Add($"Les données actuelles sont PLUS RÉCENTES ({currentSequence} événements, dont {currentUnsynced} non synchronisés) que la sauvegarde ({metadata.OutboxLastSequence}).");
        }

        var plan = new RestorePlan(metadata, currentState, currentSequence, currentUnsynced, older, warnings);
        if (options.DryRun)
        {
            return plan;
        }

        if (older && !options.ReplaceNewer)
        {
            throw new PosValidationException("current_is_newer",
                "Refusé : la base actuelle est plus récente que la sauvegarde. Si elle est réellement inutilisable, relancez avec --replace-newer " +
                "(elle sera conservée en copie forensique et ses événements non synchronisés resteront consultables, pas importés).");
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var forensic = previousRun?.Forensic ?? Path.Combine(dataDir, "forensic", stamp);
        if (!resuming)
        {
            File.WriteAllText(markerPath, JsonSerializer.Serialize(new RestoreMarkerFile(Path.GetFullPath(backupFile), forensic, stamp, metadata.BranchId), GatewayProtocol.Json));
            GatewayStore.Audit(dataDir, "restore_started", new { backup = Path.GetFullPath(backupFile), metadata.BranchId, metadata.CreatedAt, metadata.OutboxLastSequence, currentState, currentSequence, currentUnsynced, forensic, options.ReplaceNewer });
        }

        // 1. Move (never delete) the current files into the forensic folder.
        Directory.CreateDirectory(forensic);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(dbPath + suffix))
            {
                // On a resumed restore the original is already in the forensic folder: what is here now is a partial
                // restore attempt. It is kept too, under another name (nothing is ever overwritten or deleted).
                var target = Path.Combine(forensic, GatewayStore.DatabaseFileName + suffix);
                if (File.Exists(target))
                {
                    target = Path.Combine(forensic, $"{GatewayStore.DatabaseFileName}.attempt-{DateTime.UtcNow.Ticks}{suffix}");
                }

                File.Move(dbPath + suffix, target, overwrite: false);
            }
        }

        Fault("after-forensic");

        // 2. Copy, re-verify the copy, then rename into place (atomic on the same volume).
        var staging = dbPath + ".restore";
        File.Copy(backupFile, staging, overwrite: true);
        if (Checksum(staging).Sha256 != metadata.Sha256)
        {
            throw new IOException("La copie de la sauvegarde ne correspond pas à sa somme de contrôle.");
        }

        Fault("after-copy");
        File.Move(staging, dbPath, overwrite: false);

        // 3. Upgrade if needed, then block shared writes and make every unfinished print job uncertain.
        var store = new GatewayStore(dataDir, loggerFactory);
        store.Initialize();
        int uncertain;
        using (var connection = store.Database.OpenConnection())
        using (var tx = connection.BeginTransaction())
        {
            uncertain = GatewayStore.MarkPrintJobsUncertain(connection, tx, store.Now(),
                "Restauration : ce travail a pu être imprimé par la passerelle remplacée après la sauvegarde. Vérifiez avant toute réimpression.");
            store.SetRecoveryState(connection, tx, RecoveryStatus.Restored,
                $"Restaurée depuis la sauvegarde du {metadata.CreatedAt} (séquence {metadata.OutboxLastSequence}). " +
                (store.GetCloudLink() is { IsLinked: true } ? "Exécutez « reconcile » puis « recovery-verify »." : "Exécutez « recovery-verify --without-cloud »."),
                Path.GetFileName(backupFile));
            tx.Commit();
        }

        SqliteConnection.ClearAllPools();
        File.Delete(markerPath);
        GatewayStore.Audit(dataDir, "restore_completed", new { backup = Path.GetFullPath(backupFile), forensic, uncertainPrintJobs = uncertain });
        BackupLog.Restored(logger, backupFile, metadata.OutboxLastSequence, forensic, uncertain);
        return plan;
    }

    private static void Fault(string point)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("POS_GATEWAY_FAULT_RESTORE") == point)
        {
            Console.Error.WriteLine($"Fault injection: crash during restore ({point})");
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
#else
        _ = point;
#endif
    }

    private sealed record RestoreMarkerFile(string Backup, string Forensic, string Stamp, string BranchId);

    // ------------------------------------------------------------------ helpers

    private static BackupMetadata Describe(SqliteConnection connection, string sha, long size, bool offsite)
    {
        using var identity = GatewayStore.Command(connection, null, "SELECT branch_id, branch_name FROM gateway_identity WHERE id = 1;");
        using var reader = identity.ExecuteReader();
        if (!reader.Read())
        {
            throw new PosValidationException("backup_identity", "La base ne contient pas d'identité de succursale.");
        }

        var (branchId, branchName) = (reader.GetString(0), reader.GetString(1));
        reader.Close();
        long Long(string sql) => Convert.ToInt64(GatewayStore.Scalar(connection, null, sql) ?? 0L, CultureInfo.InvariantCulture);
        var hasSync = HasTable(connection, "sync_outbox");
        return new BackupMetadata(
            FormatVersion, branchId, branchName,
            hasSync ? GatewayStore.Scalar(connection, null, "SELECT gateway_id FROM cloud_link WHERE id = 1;") as string : null,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            (int)Long("SELECT COALESCE(MAX(version), 0) FROM gateway_schema;"),
            (int)Long("PRAGMA user_version;"),
            hasSync ? Long("SELECT COALESCE(MAX(sequence), 0) FROM sync_outbox;") : 0,
            hasSync ? Long("SELECT acked_sequence FROM sync_state WHERE id = 1;") : 0,
            Long("SELECT COALESCE(MAX(position), 0) FROM operations;"),
            sha, size, Environment.MachineName, offsite);
    }

    private static bool HasTable(SqliteConnection connection, string table)
    {
        using var query = GatewayStore.Command(connection, null, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $t;");
        query.Parameters.AddWithValue("$t", table);
        return Convert.ToInt64(query.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static SqliteConnection Open(string file, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static (string Sha256, long Size) Checksum(string file)
    {
        using var stream = File.OpenRead(file);
        return (Convert.ToHexString(SHA256.HashData(stream)), stream.Length);
    }
}

/// <summary>
/// Exclusive lock on the data directory: one running gateway (or restore/reconcile) per database. A second
/// <c>run</c> on the same data refuses to start; a restore refuses while the gateway serves.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    public static InstanceLock Acquire(string dataDir, string purpose)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "gateway.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write($"{purpose} pid={Environment.ProcessId} since={DateTimeOffset.UtcNow:O}");
            }

            stream.Flush(true);
            return new InstanceLock(stream);
        }
        catch (IOException)
        {
            throw new PosValidationException("gateway_busy",
                "Une passerelle (ou une restauration/réconciliation) utilise déjà ces données. Arrêtez-la d'abord : une seule instance par succursale.");
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Scheduled backups for the running gateway (configuration read at each cycle: « backup-config »).</summary>
internal sealed class BackupWorker(GatewayStore store, ILogger<BackupWorker> logger, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = GatewayBackup.GetConfig(store);
            var delay = TimeSpan.FromSeconds(30);
            if (config is not null)
            {
                var (last, _) = GatewayBackup.LastRuns(store);
                var due = last?.FinishedAt is { } at
                    ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture).AddSeconds(config.IntervalSeconds)
                    : DateTimeOffset.MinValue;
                if (time.GetUtcNow() >= due)
                {
                    var run = await Task.Run(() => GatewayBackup.RunConfigured(store, config, logger), stoppingToken);
                    // After a failure: retry sooner (at most 5 min), the failure stays visible in « status ».
                    delay = TimeSpan.FromSeconds(run.Status == "ok" ? config.IntervalSeconds : Math.Min(300, config.IntervalSeconds));
                }
                else
                {
                    delay = due - time.GetUtcNow();
                }
            }

            if (delay > TimeSpan.FromSeconds(30))
            {
                delay = TimeSpan.FromSeconds(30); // re-read the configuration regularly
            }

            try
            {
                await Task.Delay(delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

internal static partial class BackupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Backup written {BackupFile} (sequence {Sequence}, offsite {Offsite}); {Deleted} old backups removed by retention")]
    public static partial void Written(ILogger logger, string backupFile, long sequence, bool offsite, int deleted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup directory {Directory} is on the same disk as the gateway data: NOT disaster protection")]
    public static partial void SameDisk(ILogger logger, string directory);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backup FAILED")]
    public static partial void Failed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record the backup failure")]
    public static partial void RecordFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway database restored from {BackupFile} (sequence {Sequence}); previous files kept in {Forensic}; {Uncertain} print jobs marked uncertain")]
    public static partial void Restored(ILogger logger, string backupFile, long sequence, string forensic, int uncertain);
}
