using Microsoft.Data.Sqlite;
using Pos.Core;
using Pos.Core.Gateway;
using Pos.Gateway.Tests;

namespace Pos.Cloud.Tests;

/// <summary>
/// Phase 1.5 with a real cloud: a gateway restored from an outdated backup is behind the cloud, reconciles only
/// cloud-confirmed events (original ids, no duplicates, print jobs uncertain), lost acknowledgments, conflicting
/// history quarantined, replacement hardware with exactly one active gateway per branch.
/// </summary>
public sealed class CloudRecoveryTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();
    private readonly string _backups = Directory.CreateTempSubdirectory("pos-cloud-backups-").FullName;
    private Guid _organization;
    private Guid _business;

    public async Task InitializeAsync()
    {
        await _cloud.StartAsync();
        _organization = _cloud.CreateOrganization("Groupe Reprise");
        _business = _cloud.CreateBusiness(_organization, "Café Reprise");
    }

    public async Task DisposeAsync()
    {
        await _cloud.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_backups, recursive: true);
    }

    private string Backup(BranchUnderTest branch, string name)
    {
        var file = Path.Combine(_backups, name);
        branch.Gateway.Run("backup", "--out", file);
        return file;
    }

    private static string? Scalar(BranchUnderTest branch, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(branch.Gateway.DataDir, "gateway.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    [Fact]
    public async Task A_gateway_restored_behind_the_cloud_imports_only_confirmed_operations_once_with_original_ids()
    {
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Centre");
        var a = await branch.TerminalAsync("Caisse A");
        var first = await BranchUnderTest.PlaceAsync(a);
        await branch.WaitSyncedAsync(_cloud);
        var backup = Backup(branch, "behind.db");

        // After the backup: confirmed by the cloud, then the gateway PC dies.
        var second = await BranchUnderTest.PlaceAsync(a);                 // submitted: print intents
        var createId = Wait.NewId();
        var third = (await a.Client.CreateOrderAsync(createId, "Table 9")).Order; // open
        await branch.WaitSyncedAsync(_cloud);
        var cloudEvents = branch.OutboxCount;
        await branch.Gateway.KillAsync();

        branch.Gateway.Run("restore", "--from", backup, "--replace-newer");
        Assert.Equal(0, branch.Gateway.Count("SELECT COUNT(*) FROM shared_orders WHERE id IN ($a, $b)", ("$a", second.Id), ("$b", third.Id)));

        // Restored but not reconciled: the gateway serves read-only and does not push its (older) history.
        await branch.Gateway.StartAsync();
        await a.WaitConnectedAsync();
        var blocked = await Assert.ThrowsAsync<PosValidationException>(() => a.Client.CreateOrderAsync(Wait.NewId(), null));
        Assert.Equal("recovery_in_progress", blocked.Code);
        await branch.Gateway.KillAsync();
        Assert.Contains("reconcile", branch.Gateway.RunRaw("recovery-verify").Output, StringComparison.Ordinal); // linked: reconcile first

        var dryRun = branch.Gateway.Run("reconcile", "--dry-run");
        Assert.Contains("Simulation", dryRun, StringComparison.Ordinal);
        Assert.Equal(0, branch.Gateway.Count("SELECT COUNT(*) FROM shared_orders WHERE id = $id", ("$id", second.Id)));

        var report = branch.Gateway.Run("reconcile");
        Assert.Contains("ne peuvent pas être récupérées", report, StringComparison.Ordinal); // honest about what nobody holds
        Assert.Equal("reconciled", Scalar(branch, "SELECT status FROM recovery_state"));
        Assert.Equal(cloudEvents, branch.OutboxCount);
        Assert.Equal(cloudEvents, branch.AckedSequence);

        // Original ids and state, exactly once.
        Assert.Equal(3, branch.Gateway.Count("SELECT COUNT(*) FROM shared_orders"));
        Assert.Equal(second.Version, branch.Gateway.Count("SELECT version FROM shared_orders WHERE id = $id AND status = 'submitted'", ("$id", second.Id)));
        Assert.Equal(1, branch.Gateway.Count("SELECT COUNT(*) FROM shared_orders WHERE id = $id AND status = 'open'", ("$id", third.Id)));
        Assert.Equal(1, branch.Gateway.Count("SELECT COUNT(*) FROM operations WHERE operation_id = $id", ("$id", createId)));

        // Printing after recovery: the tickets of the imported submission are uncertain, never queued for a reprint.
        var printJobs = branch.Gateway.Count("SELECT COUNT(*) FROM print_jobs WHERE order_id = $id", ("$id", second.Id));
        Assert.True(printJobs > 0);
        Assert.Equal(printJobs, branch.Gateway.Count("SELECT COUNT(*) FROM print_jobs WHERE order_id = $id AND status = 'uncertain'", ("$id", second.Id)));
        Assert.Equal(printJobs, branch.Gateway.Count("SELECT COUNT(*) FROM print_intents WHERE order_id = $id", ("$id", second.Id)));

        // Running reconcile again changes nothing (idempotent).
        branch.Gateway.Run("reconcile");
        Assert.Equal(3, branch.Gateway.Count("SELECT COUNT(*) FROM shared_orders"));
        Assert.Equal(cloudEvents, branch.OutboxCount);

        branch.Gateway.Run("recovery-verify");
        await branch.Gateway.StartAsync();
        await a.WaitConnectedAsync();

        // The till retries the creation it had made before the failure: a replay, not a second order.
        var replayed = (await a.Client.CreateOrderAsync(createId, "Table 9")).Order;
        Assert.Equal(third.Id, replayed.Id);
        Assert.Equal(3, branch.Gateway.Count("SELECT COUNT(*) FROM shared_orders"));

        var fourth = await BranchUnderTest.PlaceAsync(a);
        Assert.Equal(4, fourth.Number); // numbering continues after the imported orders
        await branch.WaitSyncedAsync(_cloud);
        var final = a.Client.Snapshot().Orders.Where(o => o.Id == first.Id || o.Id == second.Id || o.Id == third.Id || o.Id == fourth.Id).ToList();
        await branch.AssertCloudMatchesAsync(_cloud, final); // each event once in the cloud, contiguous
        Assert.Equal(printJobs, branch.Gateway.Count("SELECT COUNT(*) FROM print_jobs WHERE order_id = $id AND status = 'uncertain'", ("$id", second.Id)));
    }

    [Fact]
    public async Task A_backup_whose_acknowledgment_was_lost_is_verified_against_the_cloud_and_not_sent_twice()
    {
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Nord");
        var a = await branch.TerminalAsync("Caisse A");
        await branch.WaitSyncedAsync(_cloud);
        await _cloud.KillAsync();                  // cloud unreachable: the orders below stay unacknowledged
        var orders = new List<SharedOrder> { await BranchUnderTest.PlaceAsync(a), await BranchUnderTest.PlaceAsync(a, submit: false) };
        var ackedInBackup = branch.AckedSequence;
        var backup = Backup(branch, "unacked.db");
        await _cloud.StartAsync();
        await branch.WaitSyncedAsync(_cloud);      // the cloud now holds everything; the backup does not know it
        var events = branch.OutboxCount;
        Assert.True(events > ackedInBackup);
        await branch.Gateway.KillAsync();

        branch.Gateway.Run("restore", "--from", backup);
        Assert.Equal(ackedInBackup, branch.AckedSequence);
        var report = branch.Gateway.Run("reconcile");
        Assert.Contains($"accusés perdus) : {events - ackedInBackup}", report, StringComparison.Ordinal);
        Assert.Contains("importés depuis le cloud : 0", report, StringComparison.Ordinal);
        Assert.Equal(events, branch.AckedSequence);
        var attemptsAfterRestore = branch.Gateway.Count("SELECT COALESCE(SUM(attempts), 0) FROM sync_outbox WHERE sequence > $from AND sequence <= $to", ("$from", ackedInBackup), ("$to", events));

        branch.Gateway.Run("recovery-verify");
        await branch.Gateway.StartAsync();
        await a.WaitConnectedAsync();
        orders.Add(await BranchUnderTest.PlaceAsync(a));
        await branch.WaitSyncedAsync(_cloud);
        await branch.AssertCloudMatchesAsync(_cloud, orders);
        Assert.Equal(attemptsAfterRestore, branch.Gateway.Count("SELECT COALESCE(SUM(attempts), 0) FROM sync_outbox WHERE sequence > $from AND sequence <= $to", ("$from", ackedInBackup), ("$to", events))); // verified events are never re-sent by the restored gateway
    }

    [Fact]
    public async Task A_local_history_that_contradicts_the_cloud_is_quarantined_and_blocks_recovery()
    {
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Sud");
        var a = await branch.TerminalAsync("Caisse A");
        await branch.WaitSyncedAsync(_cloud);
        await _cloud.KillAsync();
        await BranchUnderTest.PlaceAsync(a);
        var ackedInBackup = branch.AckedSequence;
        var backup = Backup(branch, "diverged.db");
        await _cloud.StartAsync();
        await branch.WaitSyncedAsync(_cloud);
        await branch.Gateway.KillAsync();

        branch.Gateway.Run("restore", "--from", backup);
        // Simulates a backup holding a different history at the same sequence (e.g. a database from another life of the branch).
        branch.ExecuteGatewaySql("UPDATE sync_outbox SET payload = replace(payload, '\"quantity\":2', '\"quantity\":3') WHERE sequence = (SELECT MAX(sequence) FROM sync_outbox WHERE payload LIKE '%\"quantity\":2%');");

        var (code, output) = branch.Gateway.RunRaw("reconcile");
        Assert.Equal(3, code);
        Assert.Contains("QUARANTAINE", output, StringComparison.Ordinal);
        Assert.Equal("cloud_ahead", Scalar(branch, "SELECT status FROM recovery_state"));
        Assert.Equal(1, branch.Gateway.Count("SELECT COUNT(*) FROM recovery_quarantine WHERE kind = 'sequence_conflict' AND blocking = 1 AND resolved_at IS NULL"));
        Assert.Equal(ackedInBackup, branch.AckedSequence); // nothing acknowledged on the basis of a contradicting history

        Assert.NotEqual(0, branch.Gateway.RunRaw("recovery-verify").ExitCode);
        Assert.NotEqual(0, branch.Gateway.RunRaw("recovery-verify", "--without-cloud").ExitCode);
        Assert.Contains("BLOQUANT", branch.Gateway.Run("quarantine"), StringComparison.Ordinal);
        await branch.Gateway.StartAsync();
        await a.WaitConnectedAsync();
        var blocked = await Assert.ThrowsAsync<PosValidationException>(() => a.Client.CreateOrderAsync(Wait.NewId(), null));
        Assert.Equal("recovery_in_progress", blocked.Code);
    }

    [Fact]
    public async Task Replacement_hardware_needs_an_explicit_replacement_code_and_fences_the_old_gateway()
    {
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Gare");
        var a = await branch.TerminalAsync("Caisse A");
        var order = await BranchUnderTest.PlaceAsync(a);
        await branch.WaitSyncedAsync(_cloud);
        var backup = Backup(branch, "old-pc.db");

        // New PC: empty data directory (its own TLS certificate), restore with the branch id stated by the operator.
        await using var replacement = new GatewayProcess();
        foreach (var file in Directory.GetFiles(replacement.DataDir, "gateway.db*"))
        {
            File.Delete(file);
        }

        replacement.Run("restore", "--from", backup, "--branch-id", branch.BranchId.ToString("D"));

        // A normal code is refused while the old gateway is still active: never two gateways for one branch.
        replacement.Run("cloud-relink", "--url", _cloud.Url, "--code", _cloud.GatewayCode(branch.BranchId), "--pin", _cloud.Fingerprint);
        var (refused, output) = replacement.RunRaw("reconcile");
        Assert.NotEqual(0, refused);
        Assert.Contains("gateway_already_active", output, StringComparison.Ordinal);
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM gateway_credentials WHERE branch_id = @p0 AND revoked_at IS NULL", branch.BranchId));

        // Explicit replacement: the old credential is revoked in the same step.
        replacement.Run("cloud-relink", "--url", _cloud.Url, "--code", _cloud.GatewayCode(branch.BranchId, replace: true), "--pin", _cloud.Fingerprint);
        Assert.Contains("Vérifiés", replacement.Run("reconcile"), StringComparison.Ordinal);
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM gateway_credentials WHERE branch_id = @p0 AND revoked_at IS NULL", branch.BranchId));
        Assert.Contains(_cloud.Log, l => l.Contains("replaced", StringComparison.OrdinalIgnoreCase));

        // The old gateway (still running, e.g. repaired and switched on again) is refused by the cloud and fences
        // itself: it stops accepting shared writes instead of diverging from the replacement.
        await BranchUnderTest.PlaceAsync(a, submit: false);
        await Wait.UntilAsync(() => Scalar(branch, "SELECT status FROM recovery_state") == "fenced", "old gateway fenced", 45);
        var fenced = await Assert.ThrowsAsync<PosValidationException>(() => a.Client.CreateOrderAsync(Wait.NewId(), null));
        Assert.Equal("recovery_in_progress", fenced.Code);

        replacement.Run("recovery-verify");
        Assert.Equal(1, replacement.Count("SELECT COUNT(*) FROM shared_orders WHERE id = $id", ("$id", order.Id)));
        Assert.Equal("normal", ScalarIn(replacement.DataDir, "SELECT status FROM recovery_state"));
        Assert.Contains("cloud_relink", File.ReadAllText(Path.Combine(replacement.DataDir, "audit", "recovery.jsonl")), StringComparison.Ordinal);
    }

    private static string? ScalarIn(string dataDir, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(dataDir, "gateway.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }
}
