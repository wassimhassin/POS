using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using Pos.Core;
using Pos.Core.Gateway;
using Pos.Gateway.Tests;

namespace Pos.Cloud.Tests;

/// <summary>
/// Phase 1.6A reliability validation: controlled load and fault scenarios against the REAL gateway process (SQLite),
/// the REAL cloud process and a real PostgreSQL cluster (private to the tests). Opt-in (long, memory-hungry):
///   $env:POS_RELIABILITY = "1"; dotnet test apps/cloud-api/Pos.Cloud.Tests --filter "FullyQualifiedName~ReliabilityTests"
/// Each scenario asserts correctness (no loss, no duplicate, stores identical) and appends its measurements as one
/// JSON line to $env:POS_RELIABILITY_OUT (default: reliability-results.jsonl next to the test binaries).
/// Single PC, loopback network: these are software-path measurements, not hardware or LAN measurements.
/// </summary>
public sealed class ReliabilityTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();
    private Guid _organization;
    private Guid _business;

    public async Task InitializeAsync()
    {
        await _cloud.StartAsync();
        _organization = _cloud.CreateOrganization("Groupe Charge");
        _business = _cloud.CreateBusiness(_organization, "Café Charge");
    }

    public Task DisposeAsync() => _cloud.DisposeAsync().AsTask();

    // ------------------------------------------------------------------ L1: concurrent terminals

    [ReliabilityFact]
    public async Task L1_concurrent_terminals_orders_submissions_and_contention()
    {
        const int Terminals = 8;
        const int OrdersPerTerminal = 25;
        const int ContendedAddsPerTerminal = 10;
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Charge");
        var terminals = new List<SimTerminal>();
        for (var i = 0; i < Terminals; i++)
        {
            terminals.Add(await branch.TerminalAsync($"Caisse {i + 1}"));
        }

        await branch.WaitSyncedAsync(_cloud);
        await using var gatewayMemory = new MemorySampler(() => branch.Gateway.CurrentProcess);
        await using var cloudMemory = new MemorySampler(() => _cloud.CurrentProcess);
        var latency = new Latencies();

        // Independent orders: every terminal creates, fills and submits its own orders as fast as it can.
        var run = Stopwatch.StartNew();
        await Task.WhenAll(terminals.Select(t => Task.Run(async () =>
        {
            for (var n = 0; n < OrdersPerTerminal; n++)
            {
                var order = (await latency.Time("create", () => t.Client.CreateOrderAsync(Wait.NewId(), null))).Order;
                order = (await latency.Time("add_item", () => t.Client.AddItemAsync(Wait.NewId(), order.Id, "p-espresso", 1))).Order;
                order = (await latency.Time("add_item", () => t.Client.AddItemAsync(Wait.NewId(), order.Id, "p-pizza", 2))).Order;
                await latency.Time("submit", () => t.Client.SubmitAsync(Wait.NewId(), order.Id, order.Version));
            }
        })));
        run.Stop();
        var operations = Terminals * OrdersPerTerminal * 4;

        // Contention: every terminal adds to the SAME order at the same time.
        var table = (await terminals[0].Client.CreateOrderAsync(Wait.NewId(), "Grande table")).Order;
        var contention = Stopwatch.StartNew();
        await Task.WhenAll(terminals.Select(t => Task.Run(async () =>
        {
            for (var i = 0; i < ContendedAddsPerTerminal; i++)
            {
                await latency.Time("add_item_contended", () => t.Client.AddItemAsync(Wait.NewId(), table.Id, "p-crepe", 1));
            }
        })));
        contention.Stop();

        // Submit race on the same version: exactly one wins, the others get a definitive conflict.
        var version = branch.Gateway.Count("SELECT version FROM shared_orders WHERE id = $id", ("$id", table.Id));
        var outcomes = await Task.WhenAll(terminals.Select(t => Task.Run(async () =>
        {
            try
            {
                await t.Client.SubmitAsync(Wait.NewId(), table.Id, version);
                return "applied";
            }
            catch (PosValidationException ex)
            {
                return ex.Code;
            }
        })));
        Assert.Single(outcomes, o => o == "applied");
        Assert.All(terminals, t => Assert.Empty(t.Store.ListPending()));

        var sync = Stopwatch.StartNew();
        await branch.WaitSyncedAsync(_cloud, 180);
        sync.Stop();

        // Correctness: counts, unique contiguous numbers, the contended order, then gateway == cloud row by row.
        var orders = Terminals * OrdersPerTerminal + 1;
        Assert.Equal(orders, branch.Gateway.Count("SELECT COUNT(*) FROM shared_orders"));
        Assert.Equal(orders, branch.Gateway.Count("SELECT COUNT(DISTINCT number) FROM shared_orders"));
        Assert.Equal(orders, branch.Gateway.Count("SELECT MAX(number) FROM shared_orders"));
        Assert.Equal(orders, branch.Gateway.Count("SELECT COUNT(*) FROM shared_orders WHERE status = 'submitted'"));
        Assert.Equal(Terminals * ContendedAddsPerTerminal, branch.Gateway.Count("SELECT quantity FROM shared_order_lines WHERE order_id = $id", ("$id", table.Id)));
        Assert.Equal(1 + (Terminals * ContendedAddsPerTerminal) + 1, branch.Gateway.Count("SELECT version FROM shared_orders WHERE id = $id", ("$id", table.Id)));
        Assert.Equal(operations + 1 + (Terminals * ContendedAddsPerTerminal) + 1, branch.Gateway.Count("SELECT COUNT(*) FROM operations WHERE status = 'applied'"));
        await Stores.AssertIdenticalAsync(branch, _cloud);

        Results.Record("L1_concurrent_terminals", new
        {
            terminals = Terminals,
            operations,
            seconds = Math.Round(run.Elapsed.TotalSeconds, 2),
            operationsPerSecond = Math.Round(operations / run.Elapsed.TotalSeconds, 1),
            contendedAdds = Terminals * ContendedAddsPerTerminal,
            contendedSeconds = Math.Round(contention.Elapsed.TotalSeconds, 2),
            submitRace = outcomes.GroupBy(o => o).ToDictionary(g => g.Key, g => g.Count()),
            latencyMs = latency.Summary(),
            syncLagAfterLastOperationSeconds = Math.Round(sync.Elapsed.TotalSeconds, 2),
            outboxEvents = branch.OutboxCount,
            gatewayMemoryMb = gatewayMemory.Summary(),
            cloudMemoryMb = cloudMemory.Summary(),
        });
    }

    // ------------------------------------------------------------------ L2: large backlog, clean drain

    [ReliabilityFact]
    public async Task L2_large_backlog_built_offline_drains_exactly_once()
    {
        const int Terminals = 4;
        const int OrdersPerTerminal = 167; // 3 events each (create, add, submit): 2004 events
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Arriéré");
        var terminals = new List<SimTerminal>();
        for (var i = 0; i < Terminals; i++)
        {
            terminals.Add(await branch.TerminalAsync($"Caisse {i + 1}"));
        }

        await branch.WaitSyncedAsync(_cloud);
        var ackedBefore = branch.AckedSequence;
        await _cloud.KillAsync(); // internet down for the whole service

        var produce = Stopwatch.StartNew();
        await Task.WhenAll(terminals.Select(t => Task.Run(async () =>
        {
            for (var n = 0; n < OrdersPerTerminal; n++)
            {
                await BranchUnderTest.PlaceAsync(t);
            }
        })));
        produce.Stop();
        var backlog = branch.OutboxCount - ackedBefore;
        Assert.Equal(Terminals * OrdersPerTerminal * 3, backlog);
        var databaseBytes = new FileInfo(Path.Combine(branch.Gateway.DataDir, "gateway.db")).Length + WalBytes(branch);

        await using var gatewayMemory = new MemorySampler(() => branch.Gateway.CurrentProcess);
        await using var cloudMemory = new MemorySampler(() => _cloud.CurrentProcess);
        var drain = Stopwatch.StartNew();
        await _cloud.StartAsync();
        var firstAck = await Measure.UntilAsync(() => branch.AckedSequence > ackedBefore, "first batch acknowledged", 60);
        await branch.WaitSyncedAsync(_cloud, 300);
        drain.Stop();
        await Stores.AssertIdenticalAsync(branch, _cloud);

        Results.Record("L2_backlog_drain", new
        {
            backlogEvents = backlog,
            offlineProductionSeconds = Math.Round(produce.Elapsed.TotalSeconds, 2),
            offlineOperationsPerSecond = Math.Round(backlog / produce.Elapsed.TotalSeconds, 1),
            gatewayDatabaseMb = Math.Round(databaseBytes / 1048576.0, 2),
            secondsToFirstAckAfterCloudStart = Math.Round(firstAck.TotalSeconds, 2),
            drainSeconds = Math.Round(drain.Elapsed.TotalSeconds, 2),
            eventsPerSecond = Math.Round(backlog / drain.Elapsed.TotalSeconds, 1),
            batchSize = 100,
            gatewayMemoryMb = gatewayMemory.Summary(),
            cloudMemoryMb = cloudMemory.Summary(),
        });
    }

    // ------------------------------------------------------------------ L3: faults in the middle of a drain

    [ReliabilityFact]
    public async Task L3_gateway_restart_cloud_crash_and_postgres_restart_during_a_drain()
    {
        const int Terminals = 2;
        const int OrdersPerTerminal = 167; // 1002 events, drained in batches of 10 so faults land mid-drain
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Perturbations", batchSize: 10);
        var terminals = new List<SimTerminal>();
        for (var i = 0; i < Terminals; i++)
        {
            terminals.Add(await branch.TerminalAsync($"Caisse {i + 1}"));
        }

        await branch.WaitSyncedAsync(_cloud);
        var start = branch.AckedSequence;
        await _cloud.KillAsync();
        await Task.WhenAll(terminals.Select(t => Task.Run(async () =>
        {
            for (var n = 0; n < OrdersPerTerminal; n++)
            {
                await BranchUnderTest.PlaceAsync(t);
            }
        })));
        var total = branch.OutboxCount;
        var faults = new List<object>();
        var drain = Stopwatch.StartNew();
        await _cloud.StartAsync();

        // 1. Gateway hard-killed mid-drain, restarted.
        await Wait.UntilAsync(() => branch.AckedSequence >= start + 200, "20% drained", 120);
        var at = branch.AckedSequence;
        await branch.Gateway.KillAsync();
        var restart = Stopwatch.StartNew();
        await branch.Gateway.StartAsync();
        var listening = restart.Elapsed;
        var resumed = await Measure.UntilAsync(() => branch.AckedSequence > at, "sync resumed after gateway restart", 60);
        faults.Add(new { fault = "gateway_kill", ackedAtFault = at - start, midDrain = at < total, gatewayListeningSeconds = Math.Round(listening.TotalSeconds, 2), secondsToNextAck = Math.Round((listening + resumed).TotalSeconds, 2) });

        // 2. Cloud hard-killed mid-drain, restarted (the gateway is in exponential backoff meanwhile).
        await Wait.UntilAsync(() => branch.AckedSequence >= start + 500, "50% drained", 120);
        at = branch.AckedSequence;
        await _cloud.KillAsync();
        await Task.Delay(2000);
        restart.Restart();
        await _cloud.StartAsync();
        var cloudListening = restart.Elapsed;
        resumed = await Measure.UntilAsync(() => branch.AckedSequence > at, "sync resumed after cloud restart", 90);
        faults.Add(new { fault = "cloud_kill_2s", ackedAtFault = at - start, midDrain = at < total, cloudListeningSeconds = Math.Round(cloudListening.TotalSeconds, 2), secondsToNextAck = Math.Round((cloudListening + resumed).TotalSeconds, 2) });

        // 3. PostgreSQL crash-stopped (immediate) and restarted under the running cloud process.
        await Wait.UntilAsync(() => branch.AckedSequence >= start + 750, "75% drained", 120);
        at = branch.AckedSequence;
        restart.Restart();
        PostgresCluster.Instance.Restart("immediate");
        var pgRestart = restart.Elapsed;
        resumed = await Measure.UntilAsync(() => branch.AckedSequence > at, "sync resumed after PostgreSQL restart", 90);
        var cloudSurvived = !_cloud.HasExited;
        faults.Add(new { fault = "postgres_immediate_restart", ackedAtFault = at - start, midDrain = at < total, postgresRestartSeconds = Math.Round(pgRestart.TotalSeconds, 2), secondsToNextAckAfterPostgresUp = Math.Round(resumed.TotalSeconds, 2), cloudProcessSurvived = cloudSurvived });

        await branch.WaitSyncedAsync(_cloud, 300);
        drain.Stop();
        Assert.True(cloudSurvived, "the cloud process must survive a database restart");
        await Stores.AssertIdenticalAsync(branch, _cloud);
        var duplicateBatches = _cloud.Log.Count(l => l.Contains("duplicates (already applied", StringComparison.Ordinal) && !l.Contains(" 0 duplicates", StringComparison.Ordinal));
        var serverErrors = _cloud.Log.Count(l => l.Contains("fail:", StringComparison.Ordinal));

        Results.Record("L3_faults_during_drain", new
        {
            backlogEvents = total - start,
            batchSize = 10,
            totalDrainSeconds = Math.Round(drain.Elapsed.TotalSeconds, 2),
            faults,
            cloudBatchesWithDuplicates = duplicateBatches,
            cloudErrorLogLines = serverErrors,
        });
    }

    // ------------------------------------------------------------------ L4: duplicates and interrupted requests

    [ReliabilityFact]
    public async Task L4_duplicate_requests_and_interrupted_connections_apply_exactly_once()
    {
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Doublons", link: false);
        var terminal = await branch.TerminalAsync("Caisse A");
        using var http = RawGateway.Http(branch.Gateway);
        var token = await RawGateway.LoginAsync(http, terminal, branch.Gateway.BranchId);
        var orders = $"v1/branches/{branch.Gateway.BranchId}/orders";

        // 1. The same create sent 20 times concurrently (double taps, client retries): one order, same answer.
        var createId = Wait.NewId();
        var body = JsonSerializer.Serialize(new CreateOrderRequest(createId, "Doublon"), GatewayProtocol.Json);
        var concurrent = Stopwatch.StartNew();
        var answers = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => RawGateway.SendAsync(http, HttpMethod.Post, orders, token, body)));
        concurrent.Stop();
        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.Status));
        var ids = answers.Select(a => JsonDocument.Parse(a.Body).RootElement.GetProperty("order").GetProperty("id").GetString()).Distinct().ToList();
        var orderId = Assert.Single(ids)!;
        Assert.Equal(1, branch.Gateway.Count("SELECT COUNT(*) FROM shared_orders"));
        Assert.Equal(1, branch.Gateway.Count("SELECT COUNT(*) FROM operations WHERE operation_id = $id", ("$id", createId)));

        // 2. The same id with a different body is refused (never silently re-applied).
        var conflicting = await RawGateway.SendAsync(http, HttpMethod.Post, orders, token, JsonSerializer.Serialize(new CreateOrderRequest(createId, "Autre"), GatewayProtocol.Json));
        Assert.Equal(HttpStatusCode.Conflict, conflicting.Status);

        // 3. 200 add-item requests, each cut at a random point (0-25 ms): outcome unknown to the client.
        const int Interrupted = 200;
        var known = new ConcurrentBag<string>();
        var unknown = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(Enumerable.Range(0, Interrupted), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
        {
            var operationId = Wait.NewId();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Random.Shared.Next(0, 26)));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{orders}/{orderId}/items")
            {
                Content = JsonContent.Create(new AddItemRequest(operationId, "p-espresso", 1), options: GatewayProtocol.Json),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            try
            {
                using var response = await http.SendAsync(request, cts.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                known.Add(operationId);
            }
            catch (OperationCanceledException)
            {
                unknown.Add(operationId);
            }
        });

        // Resolution (what a terminal does at reconnection): each unknown operation is applied once or burned.
        var resolveLatency = new Latencies();
        var applied = known.Count;
        var abandoned = new List<string>();
        foreach (var operationId in unknown)
        {
            var sw = Stopwatch.StartNew();
            var (status, json) = await RawGateway.SendAsync(http, HttpMethod.Post, $"v1/branches/{branch.Gateway.BranchId}/operations/{operationId}/resolve", token);
            resolveLatency.Add("resolve", sw.Elapsed);
            Assert.Equal(HttpStatusCode.OK, status);
            if (JsonDocument.Parse(json).RootElement.GetProperty("status").GetString() == "applied")
            {
                applied++;
            }
            else
            {
                abandoned.Add(operationId);
            }
        }

        Assert.Equal(applied, branch.Gateway.Count("SELECT quantity FROM shared_order_lines WHERE order_id = $id", ("$id", orderId)));
        Assert.Equal(1 + applied, branch.Gateway.Count("SELECT version FROM shared_orders WHERE id = $id", ("$id", orderId)));

        // Late copies: an abandoned id stays refused, an applied id is a replay (quantity unchanged).
        foreach (var operationId in abandoned.Take(20))
        {
            var late = await RawGateway.SendAsync(http, HttpMethod.Post, $"{orders}/{orderId}/items", token, JsonSerializer.Serialize(new AddItemRequest(operationId, "p-espresso", 1), GatewayProtocol.Json));
            Assert.NotEqual(HttpStatusCode.OK, late.Status);
        }

        foreach (var operationId in known.Take(20))
        {
            var replay = await RawGateway.SendAsync(http, HttpMethod.Post, $"{orders}/{orderId}/items", token, JsonSerializer.Serialize(new AddItemRequest(operationId, "p-espresso", 1), GatewayProtocol.Json));
            Assert.Equal(HttpStatusCode.OK, replay.Status);
        }

        Assert.Equal(applied, branch.Gateway.Count("SELECT quantity FROM shared_order_lines WHERE order_id = $id", ("$id", orderId)));

        // The cloud receives each applied operation exactly once.
        await branch.LinkAsync(_cloud);
        await branch.WaitSyncedAsync(_cloud, 120);
        await Stores.AssertIdenticalAsync(branch, _cloud);

        Results.Record("L4_duplicates_and_interruptions", new
        {
            concurrentIdenticalRequests = 20,
            concurrentIdenticalSeconds = Math.Round(concurrent.Elapsed.TotalSeconds, 3),
            ordersCreated = 1,
            interruptedRequests = Interrupted,
            completedBeforeCut = known.Count,
            cutWithUnknownOutcome = unknown.Count,
            resolvedApplied = applied - known.Count,
            resolvedAbandoned = abandoned.Count,
            resolveLatencyMs = resolveLatency.Summary(),
        });
    }

    // ------------------------------------------------------------------ L5: online backups under write load

    [ReliabilityFact]
    public async Task L5_online_backups_taken_under_write_load_are_consistent_and_restorable()
    {
        const int Terminals = 4;
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Sauvegarde", link: false);
        var terminals = new List<SimTerminal>();
        for (var i = 0; i < Terminals; i++)
        {
            terminals.Add(await branch.TerminalAsync($"Caisse {i + 1}"));
        }

        // Grow the database first so that a backup takes measurable time.
        await Task.WhenAll(terminals.Select(t => Task.Run(async () =>
        {
            for (var n = 0; n < 150; n++)
            {
                await BranchUnderTest.PlaceAsync(t);
            }
        })));

        var backups = Directory.CreateTempSubdirectory("pos-rel-backups-").FullName;
        try
        {
            async Task<Latencies> LoadAsync(TimeSpan duration)
            {
                var latency = new Latencies();
                var until = DateTime.UtcNow + duration;
                await Task.WhenAll(terminals.Select(t => Task.Run(async () =>
                {
                    while (DateTime.UtcNow < until)
                    {
                        var order = (await latency.Time("create", () => t.Client.CreateOrderAsync(Wait.NewId(), null))).Order;
                        await latency.Time("add_item", () => t.Client.AddItemAsync(Wait.NewId(), order.Id, "p-espresso", 1));
                    }
                })));
                return latency;
            }

            var baseline = await LoadAsync(TimeSpan.FromSeconds(10));

            // Same load while backups run back to back (each one: online copy + integrity check + SHA-256).
            var backupTimes = new List<double>();
            var files = new List<string>();
            var stopBackups = new CancellationTokenSource();
            var backupLoop = Task.Run(async () =>
            {
                for (var i = 0; !stopBackups.IsCancellationRequested; i++)
                {
                    var file = Path.Combine(backups, string.Create(CultureInfo.InvariantCulture, $"load-{i}.db"));
                    var sw = Stopwatch.StartNew();
                    branch.Gateway.Run("backup", "--out", file);
                    backupTimes.Add(sw.Elapsed.TotalSeconds);
                    files.Add(file);
                    await Task.Delay(200);
                }
            });
            var underBackup = await LoadAsync(TimeSpan.FromSeconds(10));
            await stopBackups.CancelAsync();
            await backupLoop;

            // Production path: scheduled backups inside the running gateway (interval 10 s) while the same load runs.
            var scheduledDir = Path.Combine(backups, "scheduled");
            branch.Gateway.Run("backup-config", "--dir", scheduledDir, "--interval-seconds", "10", "--keep", "100");
            var underScheduled = await LoadAsync(TimeSpan.FromSeconds(45));
            var scheduled = Directory.GetFiles(scheduledDir, "gateway-*.db");
            Assert.True(scheduled.Length >= 2, $"scheduled backups taken: {scheduled.Length}");
            files.AddRange(scheduled);
            var runSeconds = new List<double>();
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(branch.Gateway.DataDir, "gateway.db")};Mode=ReadOnly;Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT started_at, finished_at, status FROM backup_runs;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    Assert.Equal("ok", reader.GetString(2));
                    runSeconds.Add((DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture) - DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture)).TotalSeconds);
                }
            }

            // Every backup taken under load validates (checksum, integrity, metadata == content) for this branch.
            foreach (var file in files)
            {
                Assert.Contains("Simulation", branch.Gateway.Run("restore", "--from", file, "--dry-run"), StringComparison.Ordinal);
            }

            // And the last one restores into a working gateway on another data directory.
            await using var restored = new GatewayProcess();
            foreach (var old in Directory.GetFiles(restored.DataDir, "gateway.db*"))
            {
                File.Delete(old);
            }

            restored.Run("restore", "--from", files[^1], "--branch-id", branch.Gateway.BranchId);
            restored.Run("recovery-verify", "--without-cloud");
            var metadata = JsonDocument.Parse(File.ReadAllText(files[^1] + ".json")).RootElement;
            Assert.Equal(metadata.GetProperty("outboxLastSequence").GetInt64(), restored.Count("SELECT MAX(sequence) FROM sync_outbox"));
            Assert.True(restored.Count("SELECT COUNT(*) FROM shared_orders") > 0);

            Results.Record("L5_backup_under_load", new
            {
                terminals = Terminals,
                databaseMb = Math.Round(new FileInfo(Path.Combine(branch.Gateway.DataDir, "gateway.db")).Length / 1048576.0, 2),
                backupsTaken = files.Count,
                backupSeconds = new { min = Math.Round(backupTimes.Min(), 2), max = Math.Round(backupTimes.Max(), 2), note = "includes CLI process start" },
                allBackupsValid = true,
                latencyMsWithoutBackup = baseline.Summary(),
                latencyMsDuringBackToBackCliBackups = underBackup.Summary(),
                scheduledBackupsTaken = scheduled.Length,
                scheduledBackupSeconds = new { min = Math.Round(runSeconds.Min(), 2), max = Math.Round(runSeconds.Max(), 2), note = "in-process: copy + integrity_check + SHA-256 + retention" },
                latencyMsWithScheduledBackupsEvery10s = underScheduled.Summary(),
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(backups, recursive: true);
        }
    }

    // ------------------------------------------------------------------ R: recovery times

    [ReliabilityFact]
    public async Task R1_gateway_crash_recovery_time_with_connected_terminals()
    {
        const int Terminals = 6; // per-IP auth rate limit (60/min): all simulated terminals share 127.0.0.1
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Redémarrage");
        var terminals = new List<SimTerminal>();
        for (var i = 0; i < Terminals; i++)
        {
            terminals.Add(await branch.TerminalAsync($"Caisse {i + 1}"));
        }

        var order = await BranchUnderTest.PlaceAsync(terminals[0], submit: false);
        var rounds = new List<object>();
        for (var round = 0; round < 2; round++)
        {
            await branch.Gateway.KillAsync();
            var detect = await Measure.UntilAsync(() => terminals.All(t => t.Client.Status.State != GatewayState.Connected), "all terminals detect the outage", 30);
            var sw = Stopwatch.StartNew();
            await branch.Gateway.StartAsync();
            var listening = sw.Elapsed;
            await Wait.UntilAsync(() => terminals.All(t => t.Client.Status.State == GatewayState.Connected), "all terminals reconnected", 60);
            var reconnected = sw.Elapsed;
            var write = Stopwatch.StartNew();
            await terminals[round + 1].Client.AddItemAsync(Wait.NewId(), order.Id, "p-pizza", 1);
            write.Stop();
            rounds.Add(new
            {
                secondsToDetectOutage = Math.Round(detect.TotalSeconds, 2),
                secondsProcessStartToListening = Math.Round(listening.TotalSeconds, 2),
                secondsProcessStartToAllTerminalsConnected = Math.Round(reconnected.TotalSeconds, 2),
                firstWriteMs = Math.Round(write.Elapsed.TotalMilliseconds, 1),
            });
        }

        Assert.Equal(4, branch.Gateway.Count("SELECT COUNT(*) FROM operations WHERE order_id = $id", ("$id", order.Id))); // create + add + one add per round, each once
        await branch.WaitSyncedAsync(_cloud, 60);
        await Stores.AssertIdenticalAsync(branch, _cloud);
        Results.Record("R1_gateway_crash_recovery", new { terminals = Terminals, rounds });
    }

    [ReliabilityFact]
    public async Task R2_terminal_network_cut_and_silent_outage_apply_once()
    {
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Réseau", link: false);
        await using var proxy = new TcpProxy(branch.Gateway.Port);
        await using var terminal = SimTerminal.Create();
        var (code, fingerprint) = branch.Gateway.Enroll("Caisse Wi-Fi");
        await terminal.Client.EnrollAsync(proxy.Url, fingerprint, code); // the till reaches the gateway through the "network"
        terminal.Start();
        await terminal.WaitConnectedAsync();
        var order = (await terminal.Client.CreateOrderAsync(Wait.NewId(), null)).Order;

        // a) Connection reset (cable pulled / switch reboot): detection and reconnection.
        proxy.Cut(silent: false);
        var detectReset = await Measure.UntilAsync(() => terminal.Client.Status.State != GatewayState.Connected, "reset detected", 30);
        await Task.Delay(3000);
        var blocked = await Assert.ThrowsAsync<PosValidationException>(() => terminal.Client.AddItemAsync(Wait.NewId(), order.Id, "p-espresso", 1));
        proxy.Restore();
        var reconnectReset = await Measure.UntilAsync(() => terminal.Client.Status.State == GatewayState.Connected, "reconnected after reset", 60);
        await terminal.WaitUntilAsync(() => terminal.Store.ListPending().Count == 0, "pending resolved", 30);

        // b) Silent outage (Wi-Fi drop: packets lost, no reset) while an operation is in flight.
        var before = branch.Gateway.Count("SELECT COALESCE(SUM(quantity), 0) FROM shared_order_lines WHERE order_id = $id", ("$id", order.Id));
        proxy.Cut(silent: true);
        var inFlightId = Wait.NewId();
        var inFlight = Stopwatch.StartNew();
        var outcome = "applied";
        var restoreTask = Task.Delay(8000).ContinueWith(_ => proxy.Restore(), TaskScheduler.Default);
        try
        {
            await terminal.Client.AddItemAsync(inFlightId, order.Id, "p-espresso", 1);
        }
        catch (PosValidationException ex)
        {
            outcome = ex.Code;
        }

        inFlight.Stop();
        await restoreTask;
        var reconnectSilent = await Measure.UntilAsync(() => terminal.Client.Status.State == GatewayState.Connected, "reconnected after silent outage", 90);
        await terminal.WaitUntilAsync(() => terminal.Store.ListPending().Count == 0, "pending resolved", 60);
        var after = branch.Gateway.Count("SELECT COALESCE(SUM(quantity), 0) FROM shared_order_lines WHERE order_id = $id", ("$id", order.Id));
        Assert.Equal(before + branch.Gateway.Count("SELECT COUNT(*) FROM operations WHERE operation_id = $id AND status = 'applied'", ("$id", inFlightId)), after); // at most once

        Results.Record("R2_terminal_network", new
        {
            resetSecondsToDetect = Math.Round(detectReset.TotalSeconds, 2),
            resetWriteWhileDown = blocked.Code,
            resetSecondsToReconnectAfterRestore = Math.Round(reconnectReset.TotalSeconds, 2),
            silentOutageSeconds = 8,
            silentInFlightOutcome = outcome,
            silentInFlightClientSeconds = Math.Round(inFlight.Elapsed.TotalSeconds, 2),
            silentInFlightFinallyApplied = branch.Gateway.Count("SELECT COUNT(*) FROM operations WHERE operation_id = $id AND status = 'applied'", ("$id", inFlightId)) == 1,
            silentSecondsToReconnectAfterRestore = Math.Round(reconnectSilent.TotalSeconds, 2),
        });
    }

    [ReliabilityFact]
    public async Task R3_cloud_link_cut_and_delayed_acknowledgments_with_production_sync_settings()
    {
        // Production defaults (no test overrides): batch 100, timeout 30 s, max backoff 60 s, idle poll 5 s.
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Liaison", link: false);
        await branch.Gateway.KillAsync();
        branch.Gateway.RunArguments.Clear();
        await branch.Gateway.StartAsync();
        await using var proxy = new TcpProxy(_cloud.Port);
        branch.Gateway.Run("cloud-link", "--url", proxy.Url, "--code", _cloud.GatewayCode(branch.BranchId), "--pin", _cloud.Fingerprint);
        var a = await branch.TerminalAsync("Caisse A");
        await branch.WaitSyncedAsync(_cloud, 60);

        var idle = Stopwatch.StartNew();
        await BranchUnderTest.PlaceAsync(a);
        await branch.WaitSyncedAsync(_cloud, 60);
        var steadyLag = idle.Elapsed;

        // a) Internet link down (connections refused) for 45 s: backoff grows; time to resync once it is back.
        proxy.Cut(silent: false);
        for (var i = 0; i < 5; i++)
        {
            await BranchUnderTest.PlaceAsync(a);
        }

        await Task.Delay(45_000);
        proxy.Restore();
        var resyncAfterRefused = await Measure.UntilAsync(() => SyncedNow(branch), "resynced after link restored", 120);

        // b) Silent link loss (packets dropped) for 20 s, shorter than the 30 s request timeout.
        proxy.Cut(silent: true);
        for (var i = 0; i < 5; i++)
        {
            await BranchUnderTest.PlaceAsync(a);
        }

        await Task.Delay(20_000);
        proxy.Restore();
        var resyncAfterSilent = await Measure.UntilAsync(() => SyncedNow(branch), "resynced after silent loss", 120);

        // c) Acknowledgments delayed 3 s on every response (slow satellite / congested uplink).
        proxy.ResponseDelay = TimeSpan.FromSeconds(3);
        var slow = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            await BranchUnderTest.PlaceAsync(a);
        }

        await branch.WaitSyncedAsync(_cloud, 120);
        slow.Stop();
        proxy.ResponseDelay = TimeSpan.Zero;
        await Stores.AssertIdenticalAsync(branch, _cloud);

        Results.Record("R3_cloud_link_production_settings", new
        {
            steadyStateSyncLagSeconds = Math.Round(steadyLag.TotalSeconds, 2),
            refusedOutageSeconds = 45,
            secondsToResyncAfterRefusedOutage = Math.Round(resyncAfterRefused.TotalSeconds, 2),
            silentOutageSeconds = 20,
            secondsToResyncAfterSilentOutage = Math.Round(resyncAfterSilent.TotalSeconds, 2),
            delayedAckSeconds = 3,
            secondsToSync15EventsWithDelayedAcks = Math.Round(slow.Elapsed.TotalSeconds, 2),
        });
    }

    [ReliabilityFact]
    public async Task R4_acknowledgments_slower_than_the_timeout_are_replayed_without_duplicates()
    {
        // Test sync settings (timeout 5 s): every response is delayed 7 s, the gateway gives up and resends.
        await using var branch = await BranchUnderTest.CreateAsync(_cloud, _organization, _business, "Accusés", link: false);
        await using var proxy = new TcpProxy(_cloud.Port);
        branch.Gateway.Run("cloud-link", "--url", proxy.Url, "--code", _cloud.GatewayCode(branch.BranchId), "--pin", _cloud.Fingerprint);
        var a = await branch.TerminalAsync("Caisse A");
        await branch.WaitSyncedAsync(_cloud, 60);

        proxy.ResponseDelay = TimeSpan.FromSeconds(7);
        for (var i = 0; i < 5; i++)
        {
            await BranchUnderTest.PlaceAsync(a);
        }

        await Wait.UntilAsync(() => branch.Gateway.Count("SELECT COALESCE(MAX(attempts), 0) FROM sync_outbox") >= 2, "at least one timed-out resend", 60);
        var cloudEventsWhileDelayed = _cloud.Count("SELECT COUNT(*) FROM sync_events WHERE branch_id = @p0", branch.BranchId);
        var recovery = Stopwatch.StartNew();
        proxy.ResponseDelay = TimeSpan.Zero;
        await branch.WaitSyncedAsync(_cloud, 120);
        recovery.Stop();
        await Stores.AssertIdenticalAsync(branch, _cloud);

        Results.Record("R4_ack_slower_than_timeout", new
        {
            responseDelaySeconds = 7,
            gatewayTimeoutSeconds = 5,
            maxAttemptsPerEvent = branch.Gateway.Count("SELECT MAX(attempts) FROM sync_outbox"),
            cloudEventsCommittedWhileAcksLate = cloudEventsWhileDelayed,
            secondsToSyncAfterDelayRemoved = Math.Round(recovery.Elapsed.TotalSeconds, 2),
            cloudBatchesWithDuplicates = _cloud.Log.Count(l => l.Contains("duplicates (already applied", StringComparison.Ordinal) && !l.Contains(" 0 duplicates", StringComparison.Ordinal)),
        });
    }

    private bool SyncedNow(BranchUnderTest branch)
    {
        var outbox = branch.OutboxCount;
        return branch.AckedSequence == outbox && _cloud.Count("SELECT last_sequence FROM branch_sync_states WHERE branch_id = @p0", branch.BranchId) == outbox;
    }

    private static long WalBytes(BranchUnderTest branch)
    {
        var wal = Path.Combine(branch.Gateway.DataDir, "gateway.db-wal");
        return File.Exists(wal) ? new FileInfo(wal).Length : 0;
    }
}

/// <summary>Runs only with POS_RELIABILITY=1 (long and memory-hungry: run alone, sequentially).</summary>
public sealed class ReliabilityFactAttribute : FactAttribute
{
    public ReliabilityFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("POS_RELIABILITY") != "1")
        {
            Skip = "Reliability scenario: set POS_RELIABILITY=1 and run alone (see docs/09-RELIABILITY-VALIDATION.md).";
        }
    }
}

internal static class Results
{
    private static readonly Lock s_gate = new();

    public static void Record(string scenario, object metrics)
    {
        var path = Environment.GetEnvironmentVariable("POS_RELIABILITY_OUT") is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "reliability-results.jsonl");
        var line = JsonSerializer.Serialize(new { scenario, at = DateTimeOffset.UtcNow, machine = new { Environment.ProcessorCount, os = Environment.OSVersion.VersionString }, metrics });
        lock (s_gate)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }

        Console.WriteLine(line);
    }
}

internal static class Measure
{
    public static async Task<TimeSpan> UntilAsync(Func<bool> condition, string what, int timeoutSeconds)
    {
        var sw = Stopwatch.StartNew();
        await Wait.UntilAsync(condition, what, timeoutSeconds);
        return sw.Elapsed;
    }
}

/// <summary>Per-operation latency distribution (milliseconds).</summary>
internal sealed class Latencies
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<double>> _samples = new(StringComparer.Ordinal);

    public async Task<T> Time<T>(string name, Func<Task<T>> action)
    {
        var sw = Stopwatch.StartNew();
        var result = await action();
        Add(name, sw.Elapsed);
        return result;
    }

    public void Add(string name, TimeSpan elapsed) => _samples.GetOrAdd(name, _ => []).Add(elapsed.TotalMilliseconds);

    public Dictionary<string, object> Summary() => _samples.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k =>
    {
        var sorted = k.Value.Order().ToArray();
        double P(double q) => Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)], 1);
        return (object)new { count = sorted.Length, p50 = P(0.50), p95 = P(0.95), p99 = P(0.99), max = Math.Round(sorted[^1], 1) };
    }, StringComparer.Ordinal);
}

/// <summary>Samples a process's working set every 200 ms (follows restarts through the accessor).</summary>
internal sealed class MemorySampler : IAsyncDisposable
{
    private readonly Func<Process?> _target;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _peak;
    private long _last;
    private long _private;
    private long _osPeak;

    public MemorySampler(Func<Process?> target)
    {
        _target = target;
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                Sample();
                try
                {
                    await Task.Delay(200, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });
    }

    private void Sample()
    {
        try
        {
            if (_target() is { HasExited: false } p)
            {
                p.Refresh();
                _last = p.WorkingSet64;
                _private = p.PrivateMemorySize64;
                _peak = Math.Max(_peak, _last);
                _osPeak = Math.Max(_osPeak, p.PeakWorkingSet64);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    public object Summary()
    {
        Sample();
        return new { workingSetPeak = Mb(_peak), workingSetLast = Mb(_last), privateBytesLast = Mb(_private), osPeakWorkingSet = Mb(_osPeak) };
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}

/// <summary>Ground truth comparison: every order in the gateway's SQLite equals the cloud's PostgreSQL row, events once.</summary>
internal static class Stores
{
    public static async Task AssertIdenticalAsync(BranchUnderTest branch, CloudProcess cloud)
    {
        var gateway = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(branch.Gateway.DataDir, "gateway.db")};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT o.id, o.number, o.status, o.version,
                       COALESCE((SELECT SUM(quantity) FROM shared_order_lines l WHERE l.order_id = o.id), 0),
                       COALESCE((SELECT SUM(quantity * unit_price_minor) FROM shared_order_lines l WHERE l.order_id = o.id), 0)
                FROM shared_orders o;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                gateway[reader.GetString(0)] = string.Create(CultureInfo.InvariantCulture, $"{reader.GetInt64(1)}:{reader.GetString(2)}:{reader.GetInt64(3)}:{reader.GetInt64(4)}:{reader.GetInt64(5)}");
            }
        }

        var inCloud = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using (var connection = new NpgsqlConnection(PostgresCluster.Instance.AdminConnection(cloud.Database)))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT o.id::text, o.number, o.status, o.version,
                       COALESCE((SELECT SUM(quantity) FROM order_lines l WHERE l.branch_id = o.branch_id AND l.order_id = o.id), 0),
                       o.total_minor
                FROM orders o WHERE o.branch_id = @b;
                """, connection);
            command.Parameters.AddWithValue("b", branch.BranchId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                inCloud[reader.GetString(0)] = string.Create(CultureInfo.InvariantCulture, $"{reader.GetInt32(1)}:{reader.GetString(2)}:{reader.GetInt64(3)}:{Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture)}:{reader.GetInt64(5)}");
            }
        }

        Assert.Equal(gateway.Count, inCloud.Count);
        foreach (var (id, state) in gateway)
        {
            Assert.True(inCloud.TryGetValue(id, out var cloudState), $"order {id} missing in the cloud");
            Assert.Equal(state, cloudState);
        }

        var outbox = branch.OutboxCount;
        Assert.Equal(outbox, cloud.Count("SELECT COUNT(*) FROM sync_events WHERE branch_id = @p0", branch.BranchId));
        Assert.Equal(outbox, cloud.Count("SELECT COUNT(DISTINCT event_id) FROM sync_events WHERE branch_id = @p0", branch.BranchId));
        Assert.Equal(outbox, cloud.Count("SELECT MAX(sequence) FROM sync_events WHERE branch_id = @p0", branch.BranchId));
        Assert.Equal(outbox, branch.Gateway.Count("SELECT MAX(sequence) FROM sync_outbox"));
    }
}
