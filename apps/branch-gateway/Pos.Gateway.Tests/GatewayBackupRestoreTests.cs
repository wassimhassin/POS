using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pos.Core;
using Pos.Core.Gateway;

namespace Pos.Gateway.Tests;

/// <summary>Phase 1.5 without cloud: scheduled backups, backup validation, controlled restore, terminal replacement, single instance.</summary>
public sealed class GatewayBackupRestoreTests : IAsyncLifetime
{
    private readonly GatewayProcess _gateway = new();
    private readonly string _backups = Directory.CreateTempSubdirectory("pos-gw-backups-").FullName;

    public Task InitializeAsync() => _gateway.StartAsync();

    public async Task DisposeAsync()
    {
        await _gateway.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_backups, recursive: true);
    }

    private string Backup(string name)
    {
        var file = Path.Combine(_backups, name);
        Assert.Contains("vérifiée", _gateway.Run("backup", "--out", file), StringComparison.Ordinal);
        return file;
    }

    private static async Task<SharedOrder> PlaceAsync(SimTerminal terminal, bool submit)
    {
        var order = (await terminal.Client.CreateOrderAsync(Wait.NewId(), null)).Order;
        order = (await terminal.Client.AddItemAsync(Wait.NewId(), order.Id, "p-espresso", 1)).Order;
        return submit ? (await terminal.Client.SubmitAsync(Wait.NewId(), order.Id, order.Version)).Order : order;
    }

    [Fact]
    public async Task Scheduled_backups_run_report_their_state_and_keep_only_the_newest()
    {
        await _gateway.KillAsync();
        _gateway.Run("backup-config", "--dir", _backups, "--interval-seconds", "10", "--keep", "2");
        await _gateway.StartAsync();

        await Wait.UntilAsync(() => _gateway.Count("SELECT COUNT(*) FROM backup_runs WHERE status = 'ok'") >= 3, "three scheduled backups", 60);
        await Wait.UntilAsync(() => Directory.GetFiles(_backups, "gateway-*.db").Length == 2, "retention applied");
        var kept = Directory.GetFiles(_backups, "gateway-*.db").Order(StringComparer.Ordinal).ToList();
        foreach (var file in kept)
        {
            using var metadata = JsonDocument.Parse(File.ReadAllText(file + ".json"));
            var root = metadata.RootElement;
            Assert.Equal(_gateway.BranchId, root.GetProperty("branchId").GetString());
            Assert.True(root.GetProperty("gatewaySchemaVersion").GetInt32() >= 3);
            Assert.Equal(64, root.GetProperty("sha256").GetString()!.Length);
            Assert.True(root.TryGetProperty("syncAckedSequence", out _));
            Assert.False(root.GetProperty("offsite").GetBoolean()); // %TEMP% is on the same disk as the gateway data in this test
            Assert.Contains("Simulation", _gateway.Run("restore", "--from", file, "--dry-run"), StringComparison.Ordinal); // checksum + integrity ok
        }

        var status = _gateway.Run("status");
        Assert.Contains("Dernière sauvegarde réussie", status, StringComparison.Ordinal);
        Assert.Contains("MÊME DISQUE", status, StringComparison.Ordinal); // never presented as disaster protection

        // A failing destination is reported (status + exit code), the gateway keeps serving.
        Directory.Delete(_backups, recursive: true);
        File.WriteAllText(_backups, "not a directory");
        try
        {
            var (code, output) = _gateway.RunRaw("backup");
            Assert.Equal(1, code);
            Assert.Contains("ÉCHOUÉE", output, StringComparison.Ordinal);
            Assert.Contains("DERNIÈRE SAUVEGARDE EN ÉCHEC", _gateway.Run("status"), StringComparison.Ordinal);
            Assert.True(_gateway.IsRunning);
        }
        finally
        {
            File.Delete(_backups);
            Directory.CreateDirectory(_backups);
        }
    }

    [Fact]
    public void Corrupted_incomplete_or_tampered_backups_are_rejected()
    {
        var file = Backup("gateway.db");

        var tamperedMetadata = Path.Combine(_backups, "meta.db");
        File.Copy(file, tamperedMetadata);
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file + ".json"))!;
        var tampered = metadata.DeepClone();
        tampered["outboxLastSequence"] = metadata["outboxLastSequence"]!.GetValue<long>() + 5;
        File.WriteAllText(tamperedMetadata + ".json", tampered.ToJsonString());
        AssertRefused(tamperedMetadata, "backup_metadata_mismatch", "ne correspondent pas");

        var corrupted = Path.Combine(_backups, "corrupted.db");
        File.Copy(file, corrupted);
        File.Copy(file + ".json", corrupted + ".json");
        var bytes = File.ReadAllBytes(corrupted);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(corrupted, bytes);
        AssertRefused(corrupted, "backup_checksum", "Somme de contrôle");

        // Checksum recomputed over a broken file: the SQLite verification still refuses it.
        Array.Fill(bytes, (byte)0x42, 0, 100);
        File.WriteAllBytes(corrupted, bytes);
        var rehashed = metadata.DeepClone();
        var original = metadata["sha256"]!.GetValue<string>();
        var hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        rehashed["sha256"] = original.Any(char.IsLower) ? hex.ToLowerInvariant() : hex;
        File.WriteAllText(corrupted + ".json", rehashed.ToJsonString());
        AssertRefused(corrupted, "backup_integrity", "illisible");

        var incomplete = Path.Combine(_backups, "incomplete.db");
        File.Copy(file, incomplete);
        AssertRefused(incomplete, "backup_incomplete", "incomplète");
        Assert.True(_gateway.IsRunning); // validation never touched the running gateway
    }

    private void AssertRefused(string backup, string what, string message)
    {
        var (code, output) = _gateway.RunRaw("restore", "--from", backup, "--dry-run");
        Assert.True(code == 2, $"{what}: exit {code}: {output}");
        Assert.True(output.Contains(message, StringComparison.Ordinal), $"{what}: {output}");
    }

    [Fact]
    public async Task A_backup_of_another_branch_is_rejected()
    {
        await using var other = new GatewayProcess();
        var foreign = Path.Combine(_backups, "other-branch.db");
        other.Run("backup", "--out", foreign);
        await _gateway.KillAsync();

        var (code, output) = _gateway.RunRaw("restore", "--from", foreign);
        Assert.Equal(2, code);
        Assert.Contains("appartient à la succursale", output, StringComparison.Ordinal);

        // New hardware (no database): the operator must state the branch, and it must match.
        var empty = Directory.CreateTempSubdirectory("pos-gw-new-").FullName;
        try
        {
            var mine = Backup("mine.db");
            var cli = new GatewayCli(empty);
            Assert.Contains("--branch-id", cli.RunRaw("restore", "--from", mine).Output, StringComparison.Ordinal);
            Assert.Contains("appartient à la succursale", cli.RunRaw("restore", "--from", mine, "--branch-id", other.BranchId).Output, StringComparison.Ordinal);
            Assert.Equal(0, cli.RunRaw("restore", "--from", mine, "--branch-id", _gateway.BranchId).ExitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public async Task Restoring_an_outdated_backup_is_explicit_keeps_forensics_blocks_writes_and_makes_print_jobs_uncertain()
    {
        await using var a = await SimTerminal.EnrolledAsync(_gateway, "Caisse A");
        var early = await PlaceAsync(a, submit: true); // print jobs created (no printer configured: not printed)
        var backup = Backup("early.db");
        var late = await PlaceAsync(a, submit: false);
        var printJobsBefore = _gateway.Count("SELECT COUNT(*) FROM print_jobs");

        // Never while the gateway serves, and never a second gateway on the same data.
        Assert.Contains("utilise déjà", _gateway.RunRaw("restore", "--from", backup).Output, StringComparison.Ordinal);
        Assert.Contains("utilise déjà", _gateway.RunRaw("run", "--listen", "https://127.0.0.1:1").Output, StringComparison.Ordinal);

        await _gateway.KillAsync();
        var dryRun = _gateway.Run("restore", "--from", backup, "--dry-run");
        Assert.Contains("PLUS RÉCENTES", dryRun, StringComparison.Ordinal);
        var (refused, output) = _gateway.RunRaw("restore", "--from", backup);
        Assert.Equal(2, refused);
        Assert.Contains("plus récente", output, StringComparison.Ordinal); // newer data is never silently overwritten
        Assert.Equal(2, _gateway.Count("SELECT COUNT(*) FROM shared_orders"));

        _gateway.Run("restore", "--from", backup, "--replace-newer");
        var forensic = Directory.GetDirectories(Path.Combine(_gateway.DataDir, "forensic")).Single();
        using (var old = new SqliteConnection($"Data Source={Path.Combine(forensic, "gateway.db")};Mode=ReadOnly;Pooling=False"))
        {
            old.Open();
            using var count = old.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM shared_orders;";
            Assert.Equal(2L, count.ExecuteScalar()); // the replaced data is preserved, not deleted
        }

        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM shared_orders"));
        Assert.Equal(0, _gateway.Count("SELECT COUNT(*) FROM shared_orders WHERE id = $id", ("$id", late.Id)));
        Assert.Equal("restored", Scalar("SELECT status FROM recovery_state"));
        Assert.Equal(0, _gateway.Count("SELECT COUNT(*) FROM print_jobs WHERE status IN ('queued', 'printing', 'failed')"));
        Assert.Equal(_gateway.Count("SELECT COUNT(*) FROM print_jobs WHERE order_id = $id", ("$id", early.Id)),
            _gateway.Count("SELECT COUNT(*) FROM print_jobs WHERE status = 'uncertain'"));
        Assert.True(printJobsBefore >= _gateway.Count("SELECT COUNT(*) FROM print_jobs"));

        // Restored: readable, but shared writes are refused (definitively, nothing applied) until verified.
        await _gateway.StartAsync();
        await a.WaitConnectedAsync();
        Assert.Contains(a.Client.Snapshot().Orders, o => o.Id == early.Id);
        var blocked = await Assert.ThrowsAsync<PosValidationException>(() => a.Client.CreateOrderAsync(Wait.NewId(), null));
        Assert.Equal("recovery_in_progress", blocked.Code);
        Assert.Empty(a.Store.ListPending());
        Assert.Contains("RÉCUPÉRATION EN COURS", _gateway.Run("status"), StringComparison.Ordinal);

        await _gateway.KillAsync();
        Assert.Contains("--without-cloud", _gateway.RunRaw("recovery-verify").Output, StringComparison.Ordinal);
        _gateway.Run("recovery-verify", "--without-cloud");
        await _gateway.StartAsync();
        await a.WaitConnectedAsync();
        var after = await PlaceAsync(a, submit: false);
        Assert.Equal(2, after.Number); // numbering continues from the restored state

        // Nothing reprinted automatically: uncertain jobs stay uncertain.
        await Task.Delay(2000);
        Assert.Equal(0, _gateway.Count("SELECT COUNT(*) FROM print_jobs WHERE order_id = $id AND status <> 'uncertain'", ("$id", early.Id)));

        var audit = File.ReadAllLines(Path.Combine(_gateway.DataDir, "audit", "recovery.jsonl"));
        foreach (var action in new[] { "restore_started", "restore_completed", "recovery_verified" })
        {
            Assert.Contains(audit, l => l.Contains($"\"action\":\"{action}\"", StringComparison.Ordinal));
        }
    }

#if DEBUG
    [Theory]
    [InlineData("after-forensic")]
    [InlineData("after-copy")]
    public async Task A_crash_during_restore_blocks_the_gateway_until_the_same_restore_completes(string crashPoint)
    {
        await using var a = await SimTerminal.EnrolledAsync(_gateway, "Caisse A");
        var kept = await PlaceAsync(a, submit: false);
        var backup = Backup("before.db");
        var other = Backup("other.db");
        await PlaceAsync(a, submit: false);
        await _gateway.KillAsync();

        _gateway.WithEnvironment("POS_GATEWAY_FAULT_RESTORE", crashPoint);
        var (crashed, _) = _gateway.RunRaw("restore", "--from", backup, "--replace-newer");
        _gateway.WithoutEnvironment("POS_GATEWAY_FAULT_RESTORE");
        Assert.NotEqual(0, crashed);
        Assert.True(File.Exists(Path.Combine(_gateway.DataDir, "restore-in-progress.json")));

        Assert.Contains("interrompue", _gateway.RunRaw("status").Output, StringComparison.Ordinal);
        Assert.Contains("interrompue", _gateway.RunRaw("run").Output, StringComparison.Ordinal); // cannot serve a half-restored state
        Assert.Contains("interrompue", _gateway.RunRaw("restore", "--from", other).Output, StringComparison.Ordinal);

        _gateway.Run("restore", "--from", backup, "--replace-newer");
        Assert.False(File.Exists(Path.Combine(_gateway.DataDir, "restore-in-progress.json")));
        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM shared_orders WHERE id = $id", ("$id", kept.Id)));
        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM shared_orders"));
        Assert.Equal("ok", Scalar("PRAGMA integrity_check"));
        var forensic = Directory.GetDirectories(Path.Combine(_gateway.DataDir, "forensic")).Single();
        Assert.True(File.Exists(Path.Combine(forensic, "gateway.db"))); // the original newer data survived the crash
    }
#endif

    [Fact]
    public async Task A_lost_till_is_revoked_and_replaced_by_a_new_identity_that_keeps_the_branch_orders()
    {
        await using var old = await SimTerminal.EnrolledAsync(_gateway, "Caisse A");
        var open = await PlaceAsync(old, submit: false);
        var submitted = await PlaceAsync(old, submit: true);
        var oldId = old.TerminalId;

        var output = _gateway.Run("replace-terminal", "--terminal", oldId, "--name", "Caisse A (remplacement)");
        Assert.Contains("révoqué", output, StringComparison.Ordinal);
        var code = System.Text.RegularExpressions.Regex.Match(output, "Code d'appairage : (\\S+)").Groups[1].Value;

        // The old till (stolen, or found again later) is refused: live session closed, no reconnection, no write.
        await old.WaitUntilAsync(() => old.Client.Status.State == GatewayState.Offline, "old till disconnected");
        await old.StopAsync();
        old.Start();
        await Task.Delay(3000);
        Assert.NotEqual(GatewayState.Connected, old.Client.Status.State);
        await Assert.ThrowsAsync<PosValidationException>(() => old.Client.AddItemAsync(Wait.NewId(), open.Id, "p-pizza", 1));

        // Replacement: a new identity and key, paired with the one-time code; it continues the branch's orders.
        await using var replacement = SimTerminal.Create();
        await replacement.Client.EnrollAsync(_gateway.Url, _gateway.Fingerprint, code);
        replacement.Start();
        await replacement.WaitConnectedAsync();
        Assert.NotEqual(oldId, replacement.TerminalId);
        Assert.Contains(replacement.Client.Snapshot().Orders, o => o.Id == submitted.Id && o.Status == SharedOrderStatus.Submitted);
        var continued = (await replacement.Client.AddItemAsync(Wait.NewId(), open.Id, "p-pizza", 1)).Order;
        await replacement.Client.SubmitAsync(Wait.NewId(), open.Id, continued.Version);
        Assert.Equal(1, _gateway.Count("SELECT COUNT(*) FROM terminals WHERE id = $id AND status = 'revoked'", ("$id", oldId)));
        Assert.Contains("terminal_replaced", File.ReadAllText(Path.Combine(_gateway.DataDir, "audit", "recovery.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unpairing_a_till_forgets_its_identity_but_never_silently_drops_pending_operations()
    {
        await using var till = await SimTerminal.EnrolledAsync(_gateway, "Caisse A");
        var order = await PlaceAsync(till, submit: false);
        var oldId = till.TerminalId;

        till.Store.AddPending(Wait.NewId(), "add_item", "Ajout en attente");
        var refused = Assert.Throws<PosValidationException>(() => till.Client.Unpair(discardPending: false));
        Assert.Equal("pending_operations", refused.Code);
        Assert.NotNull(till.Store.GetLink());

        till.Client.Unpair(discardPending: true); // explicit operator decision (logged)
        Assert.Null(till.Store.GetLink());
        Assert.Empty(till.Store.ListPending());
        Assert.Equal(GatewayState.NotConfigured, till.Client.Status.State);
        Assert.NotEqual(oldId, till.TerminalId); // a new identity at the next pairing

        _gateway.Run("revoke", "--terminal", oldId);
        var (code, fingerprint) = _gateway.Enroll("Caisse A (réappairée)");
        await till.Client.EnrollAsync(_gateway.Url, fingerprint, code);
        await till.WaitConnectedAsync();
        Assert.Contains(till.Client.Snapshot().Orders, o => o.Id == order.Id); // branch orders preserved
    }

    private string? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_gateway.DataDir, "gateway.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    /// <summary>The gateway CLI on another data directory (e.g. replacement hardware with no database yet).</summary>
    private sealed class GatewayCli(string dataDir)
    {
        public (int ExitCode, string Output) RunRaw(params string[] args)
        {
            var info = new System.Diagnostics.ProcessStartInfo(GatewayProcess.Executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args.Append("--data-dir").Append(dataDir))
            {
                info.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit(30_000);
            return (process.ExitCode, stdout.Result + stderr.Result);
        }
    }
}
