using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;
using Pos.Core.Gateway;
using Pos.Gateway.Tests;
using Pos.Sync;

namespace Pos.Cloud.Tests;

/// <summary>
/// A throwaway PostgreSQL cluster (initdb + pg_ctl from the local PostgreSQL installation; no admin rights,
/// the installed service is not touched). SCRAM password authentication, 127.0.0.1 only. One cluster per test
/// run, one database per test. The cloud runs as <c>pos_app</c>, a role with data privileges only (no DDL);
/// migrations run as the owner.
/// </summary>
public sealed class PostgresCluster
{
    private static readonly Lazy<PostgresCluster> s_instance = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly string _bin;
    private readonly string _root;

    private PostgresCluster(string bin, string root, int port, string password, string appPassword)
    {
        _bin = bin;
        _root = root;
        Port = port;
        Password = password;
        AppPassword = appPassword;
    }

    public static PostgresCluster Instance => s_instance.Value;

    public int Port { get; }

    public string Password { get; }

    public string AppPassword { get; }

    public static string FindBin()
    {
        var configured = Environment.GetEnvironmentVariable("POS_PG_BIN");
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        var bin = Directory.Exists(root)
            ? Directory.GetDirectories(root).Select(d => Path.Combine(d, "bin")).Where(d => File.Exists(Path.Combine(d, "pg_ctl.exe")))
                .OrderByDescending(d => int.TryParse(new DirectoryInfo(d).Parent!.Name, out var v) ? v : 0).FirstOrDefault()
            : null;
        return bin ?? throw new InvalidOperationException("PostgreSQL introuvable : installez PostgreSQL ou définissez POS_PG_BIN (dossier contenant initdb.exe et pg_ctl.exe).");
    }

    private static PostgresCluster Start()
    {
        var bin = FindBin();
        var root = Directory.CreateTempSubdirectory("pos-pg-test-").FullName;
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var passwordFile = Path.Combine(root, "pw.txt");
        File.WriteAllText(passwordFile, password);
        var data = Path.Combine(root, "data");
        Tool(bin, "initdb.exe", "-D", data, "-U", "postgres", "-A", "scram-sha-256", $"--pwfile={passwordFile}", "-E", "UTF8", "--locale=C");
        File.Delete(passwordFile);

        var port = FreePort();
        Tool(bin, "pg_ctl.exe", "-D", data, "-o", $"-p {port} -c listen_addresses=127.0.0.1 -c max_connections=300", "-l", Path.Combine(root, "postgres.log"), "-w", "start");
        var cluster = new PostgresCluster(bin, root, port, password, appPassword);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cluster.Stop();
        cluster.Execute("postgres", $"CREATE ROLE pos_app LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE;");
        return cluster;
    }

    public string AdminConnection(string database) =>
        $"Host=127.0.0.1;Port={Port};Username=postgres;Password={Password};Database={database};Pooling=false;Timeout=15";

    public string AppConnection(string database) =>
        $"Host=127.0.0.1;Port={Port};Username=pos_app;Password={AppPassword};Database={database};Timeout=15";

    public string CreateDatabase()
    {
        var name = $"pos_{Guid.NewGuid():N}";
        Execute("postgres", $"CREATE DATABASE {name};");
        Execute(name, "REVOKE ALL ON SCHEMA public FROM PUBLIC;");
        return name;
    }

    /// <summary>After migrations: the application role may read and write data, nothing else.</summary>
    public void GrantApp(string database) => Execute(database, $"""
        GRANT CONNECT ON DATABASE {database} TO pos_app;
        GRANT USAGE ON SCHEMA public TO pos_app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO pos_app;
        REVOKE INSERT, UPDATE, DELETE ON "__EFMigrationsHistory" FROM pos_app;
        """);

    public void Execute(string database, string sql)
    {
        using var connection = new NpgsqlConnection(AdminConnection(database));
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private void Stop()
    {
        try
        {
            Tool(_bin, "pg_ctl.exe", "-D", Path.Combine(_root, "data"), "-m", "immediate", "-w", "stop");
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
        }
    }

    private static void Tool(string bin, string exe, params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(bin, exe)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{exe} failed: {stdout.Result}{stderr.Result}");
        }
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>A real Pos.Cloud.exe process with its own PostgreSQL database and development certificate.</summary>
public sealed partial class CloudProcess : IAsyncDisposable
{
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);
    private readonly string _dir;
    private readonly string _certificatePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private Process? _process;

    public CloudProcess()
    {
        Database = PostgresCluster.Instance.CreateDatabase();
        _dir = Directory.CreateTempSubdirectory("pos-cloud-test-").FullName;
        Port = PostgresCluster.FreePort();
        Admin("migrate");
        PostgresCluster.Instance.GrantApp(Database);
        Fingerprint = Value(Admin("dev-cert", "--out", CertificatePath), "sha256");
    }

    public string Database { get; }

    public int Port { get; }

    public string Url => $"https://127.0.0.1:{Port}/";

    public string Fingerprint { get; }

    public string CertificatePath => Path.Combine(_dir, "cloud-dev.pfx");

    /// <summary>Everything the cloud process printed (structured log lines).</summary>
    public ConcurrentQueue<string> Log { get; } = new();

    public static string Executable
    {
        get
        {
            var testBin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = testBin.Parent!.Name;
            var area = testBin.Parent!.Parent!.Parent!.Parent!.FullName; // apps/cloud-api
            var exe = Path.Combine(area, "Pos.Cloud", "bin", configuration, "net10.0", "Pos.Cloud.exe");
            return File.Exists(exe) ? exe : throw new FileNotFoundException("Build Pos.Cloud first.", exe);
        }
    }

    public CloudProcess WithEnvironment(string name, string value)
    {
        _environment[name] = value;
        return this;
    }

    public async Task StartAsync()
    {
        var info = Info("run", "--listen", $"https://127.0.0.1:{Port}", "--certificate", CertificatePath);
        foreach (var (name, value) in _environment)
        {
            info.Environment[name] = value;
        }

        _process = Process.Start(info)!;
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Enqueue(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log.Enqueue(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"Cloud exited during startup with code {_process.ExitCode}: {string.Join('\n', Log)}");
            }

            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, Port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException("Cloud did not start.");
    }

    /// <summary>Hard kill: no graceful shutdown (a crash, or the internet link going down from the gateway's view).</summary>
    public async Task KillAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
    }

    public bool HasExited => _process is { HasExited: true };

    public string Admin(params string[] args)
    {
        var (exitCode, output) = AdminRaw(args);
        return exitCode == 0 ? output : throw new InvalidOperationException($"Pos.Cloud {args[0]} failed ({exitCode}): {output}");
    }

    public (int ExitCode, string Output) AdminRaw(params string[] args)
    {
        using var process = Process.Start(Info(args))!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(60_000);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    public Guid CreateOrganization(string name) => Guid.Parse(Value(Admin("org-create", "--name", name), "organization_id"));

    public Guid CreateBusiness(Guid organizationId, string name) =>
        Guid.Parse(Value(Admin("business-create", "--org", organizationId.ToString("D"), "--name", name), "business_id"));

    public Guid CreateBranch(Guid businessId, string name, string? id = null) =>
        Guid.Parse(Value(Admin([.. new[] { "branch-create", "--business", businessId.ToString("D"), "--name", name }, .. id is null ? Array.Empty<string>() : ["--id", id]]), "branch_id"));

    public string GatewayCode(Guid branchId) => Value(Admin("gateway-code", "--branch", branchId.ToString("D")), "code");

    public string ReportKey(Guid organizationId, Guid? businessId = null, Guid? branchId = null)
    {
        var args = new List<string> { "report-key", "--org", organizationId.ToString("D"), "--name", "test" };
        if (businessId is { } b)
        {
            args.AddRange(["--business", b.ToString("D")]);
        }

        if (branchId is { } br)
        {
            args.AddRange(["--branch", br.ToString("D")]);
        }

        return Value(Admin([.. args]), "report_key");
    }

    /// <summary>Ground truth, straight from PostgreSQL (as the owner).</summary>
    public long Count(string sql, params object[] parameters)
    {
        using var connection = new NpgsqlConnection(PostgresCluster.Instance.AdminConnection(Database));
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i]);
        }

        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    public HttpClient Http(string? reportKey = null)
    {
        var pin = Convert.FromHexString(Fingerprint);
        var http = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && SHA256.HashData(cert.GetRawCertData()).AsSpan().SequenceEqual(pin) },
        })
        {
            BaseAddress = new Uri(Url),
            Timeout = TimeSpan.FromSeconds(15),
        };
        if (reportKey is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reportKey);
        }

        return http;
    }

    public async Task<List<ReportOrder>> OrdersAsync(string reportKey, Guid branchId)
    {
        using var http = Http(reportKey);
        return (await http.GetFromJsonAsync<List<ReportOrder>>($"v1/reports/branches/{branchId:D}/orders", SyncProtocol.Json))!;
    }

    public async ValueTask DisposeAsync()
    {
        await KillAsync();
        _process?.Dispose();
        try
        {
            NpgsqlConnection.ClearAllPools();
            PostgresCluster.Instance.Execute("postgres", $"DROP DATABASE IF EXISTS {Database} WITH (FORCE);");
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or NpgsqlException)
        {
        }
    }

    public static string Value(string output, string key)
    {
        var match = Regex.Match(output, $"^{Regex.Escape(key)}=(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : throw new InvalidOperationException($"'{key}' not found in: {output}");
    }

    private ProcessStartInfo Info(params string[] args)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["POS_CLOUD_DB"] = PostgresCluster.Instance.AppConnection(Database);
        info.Environment["POS_CLOUD_MIGRATION_DB"] = PostgresCluster.Instance.AdminConnection(Database);
        info.Environment["POS_CLOUD_CERT_PASSWORD"] = _certificatePassword;
        return info;
    }
}

/// <summary>One branch: cloud tenant rows + a real gateway process (with its own branch id) linked to the cloud.</summary>
public sealed class BranchUnderTest : IAsyncDisposable
{
    private BranchUnderTest(GatewayProcess gateway, Guid organizationId, Guid businessId, Guid branchId)
    {
        Gateway = gateway;
        OrganizationId = organizationId;
        BusinessId = businessId;
        BranchId = branchId;
    }

    public GatewayProcess Gateway { get; }

    public Guid OrganizationId { get; }

    public Guid BusinessId { get; }

    public Guid BranchId { get; }

    public List<SimTerminal> Terminals { get; } = [];

    /// <summary>
    /// The gateway is initialized first (existing local identity), then the cloud branch is created with THAT id,
    /// then the gateway is linked with a one-time code: the existing identity is kept, not replaced.
    /// </summary>
    public static async Task<BranchUnderTest> CreateAsync(CloudProcess cloud, Guid organizationId, Guid businessId, string name, bool link = true, int batchSize = 100)
    {
        var gateway = new GatewayProcess();
        gateway.RunArguments.AddRange(["--sync-max-backoff", "1", "--sync-timeout", "5", "--sync-batch", batchSize.ToString(CultureInfo.InvariantCulture)]);
        var branchId = cloud.CreateBranch(businessId, name, gateway.BranchId);
        await gateway.StartAsync();
        var branch = new BranchUnderTest(gateway, organizationId, businessId, branchId);
        if (link)
        {
            await branch.LinkAsync(cloud);
        }

        return branch;
    }

    public async Task LinkAsync(CloudProcess cloud)
    {
        Gateway.Run("cloud-link", "--url", cloud.Url, "--code", cloud.GatewayCode(BranchId), "--pin", cloud.Fingerprint);
        await Wait.UntilAsync(() => Gateway.Count("SELECT COUNT(*) FROM cloud_link WHERE gateway_id IS NOT NULL") == 1, "gateway linked to the cloud", 30);
    }

    public async Task<SimTerminal> TerminalAsync(string name)
    {
        var terminal = await SimTerminal.EnrolledAsync(Gateway, name);
        Terminals.Add(terminal);
        return terminal;
    }

    /// <summary>Places an order (create, add 2 items, optionally submit) and returns its final state as the gateway committed it.</summary>
    public static async Task<SharedOrder> PlaceAsync(SimTerminal terminal, bool submit = true, string productId = "p-espresso")
    {
        var order = (await terminal.Client.CreateOrderAsync(Wait.NewId(), null)).Order;
        order = (await terminal.Client.AddItemAsync(Wait.NewId(), order.Id, productId, 2)).Order;
        return submit ? (await terminal.Client.SubmitAsync(Wait.NewId(), order.Id, order.Version)).Order : order;
    }

    /// <summary>Writes to the gateway's SQLite file (fault/upgrade simulation by the test).</summary>
    public void ExecuteGatewaySql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Gateway.DataDir, "gateway.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Every order of the branch in the cloud equals the gateway's committed state, and each event is stored once.</summary>
    public async Task AssertCloudMatchesAsync(CloudProcess cloud, IReadOnlyCollection<SharedOrder> expected)
    {
        var key = cloud.ReportKey(OrganizationId, BusinessId, BranchId);
        var orders = await cloud.OrdersAsync(key, BranchId);
        Assert.Equal(expected.Count, orders.Count);
        foreach (var order in expected)
        {
            var inCloud = Assert.Single(orders, o => o.Order.Id == order.Id);
            Assert.Equal(Compare.Normalize(Compare.FromGateway(order)), Compare.Normalize(inCloud.Order));
        }

        Assert.Equal(OutboxCount, cloud.Count("SELECT COUNT(*) FROM sync_events WHERE branch_id = @p0", BranchId));
        Assert.Equal(OutboxCount, cloud.Count("SELECT COUNT(DISTINCT event_id) FROM sync_events WHERE branch_id = @p0", BranchId));
        Assert.Equal(OutboxCount, cloud.Count("SELECT MAX(sequence) FROM sync_events WHERE branch_id = @p0", BranchId));
        Assert.Equal(OutboxCount, Gateway.Count("SELECT MAX(sequence) FROM sync_outbox")); // contiguous, no gap
    }

    public long OutboxCount => Gateway.Count("SELECT COUNT(*) FROM sync_outbox");

    public long AckedSequence => Gateway.Count("SELECT acked_sequence FROM sync_state");

    public string? LastSyncError
    {
        get
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Gateway.DataDir, "gateway.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT last_error FROM sync_state";
            return command.ExecuteScalar() as string;
        }
    }

    /// <summary>Waits until every outbox event is acknowledged by the cloud and the cloud checkpoint matches.</summary>
    public Task WaitSyncedAsync(CloudProcess cloud, int timeoutSeconds = 45) => Wait.UntilAsync(() =>
    {
        var outbox = OutboxCount;
        return outbox > 0 && AckedSequence == outbox
            && cloud.Count("SELECT last_sequence FROM branch_sync_states WHERE branch_id = @p0", BranchId) == outbox;
    }, $"branch {BranchId} fully synchronized (outbox {OutboxCount}, acked {AckedSequence}, error {LastSyncError})", timeoutSeconds);

    public async ValueTask DisposeAsync()
    {
        foreach (var terminal in Terminals)
        {
            await terminal.DisposeAsync();
        }

        await Gateway.DisposeAsync();
    }
}

/// <summary>A gateway credential held by the test itself, to speak the sync protocol directly (raw, signed requests).</summary>
public sealed class RawGatewayClient : IDisposable
{
    private readonly CloudProcess _cloud;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private RawGatewayClient(CloudProcess cloud) => _cloud = cloud;

    public string GatewayId { get; private set; } = "";

    public RegisterGatewayResponse Registration { get; private set; } = null!;

    public ECDsa Key => _key;

    /// <summary>A key pair with no credential yet (to exercise registration).</summary>
    public static RawGatewayClient Unregistered(CloudProcess cloud) => new(cloud);

    public static async Task<RawGatewayClient> RegisterAsync(CloudProcess cloud, Guid branchId)
    {
        var client = new RawGatewayClient(cloud);
        using var response = await client.RegisterRawAsync(cloud.GatewayCode(branchId), branchId.ToString("D"));
        response.EnsureSuccessStatusCode();
        client.Registration = (await response.Content.ReadFromJsonAsync<RegisterGatewayResponse>(SyncProtocol.Json))!;
        client.GatewayId = client.Registration.GatewayId;
        return client;
    }

    public async Task<HttpResponseMessage> RegisterRawAsync(string code, string branchId, ECDsa? signer = null)
    {
        var publicKey = _key.ExportSubjectPublicKeyInfo();
        var signature = (signer ?? _key).SignData(SyncProtocol.RegisterPayload(code, branchId, publicKey), HashAlgorithmName.SHA256);
        using var http = _cloud.Http();
        return await http.PostAsJsonAsync("v1/gateways/register",
            new RegisterGatewayRequest(code, branchId, Convert.ToBase64String(publicKey), Convert.ToBase64String(signature)), SyncProtocol.Json);
    }

    public Task<(HttpStatusCode Status, string Body)> SendAsync(params SyncEvent[] events) =>
        SendRawAsync(JsonSerializer.Serialize(new SyncBatch(events), SyncProtocol.Json));

    public async Task<SyncBatchResult> SendOkAsync(params SyncEvent[] events)
    {
        var (status, body) = await SendAsync(events);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}");
        return JsonSerializer.Deserialize<SyncBatchResult>(body, SyncProtocol.Json)!;
    }

    /// <summary>Signed request with optional tampering: another key, a shifted clock, a body different from the signed one.</summary>
    public async Task<(HttpStatusCode Status, string Body)> SendRawAsync(string json, ECDsa? signer = null, TimeSpan? clockSkew = null, string? signedBody = null, string? gatewayId = null)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var timestamp = DateTimeOffset.UtcNow.Add(clockSkew ?? TimeSpan.Zero).ToUnixTimeSeconds();
        var id = gatewayId ?? GatewayId;
        var signature = (signer ?? _key).SignData(SyncProtocol.RequestPayload(id, "POST", "/v1/sync/events", timestamp,
            SyncProtocol.Sha256Hex(signedBody is null ? body : Encoding.UTF8.GetBytes(signedBody))), HashAlgorithmName.SHA256);
        using var http = _cloud.Http();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/sync/events") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue(SyncProtocol.GatewayScheme, SyncProtocol.AuthorizationParameter(id, timestamp, signature));
        request.Headers.ExpectContinue = body.Length > 64 * 1024; // let the server refuse an oversized body before it is uploaded
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Valid synthetic events for protocol-level tests.</summary>
public static class TestEvents
{
    public static SyncEvent Order(long sequence, int number, long version = 1, string? orderId = null, string? eventId = null, int quantity = 1, string status = "open")
    {
        var now = "2026-11-18T10:00:00.0000000+00:00";
        var lines = quantity == 0 ? [] : new List<SyncOrderLine> { new("p-espresso", "Café express", 2500, quantity, 2500L * quantity, "bar") };
        var order = new SyncOrder(orderId ?? Guid.NewGuid().ToString("D"), number, null, status, version, "TND", lines,
            lines.Sum(l => l.Quantity), lines.Sum(l => l.LineTotalMinor), "Caisse T", "Caisse T", status == "submitted" ? "Caisse T" : null,
            now, now, status == "submitted" ? now : null);
        return new SyncEvent(sequence, eventId ?? Guid.NewGuid().ToString("D"), status == "submitted" ? SyncEventTypes.Submit : SyncEventTypes.CreateOrder,
            SyncProtocol.SchemaVersion, now, null, order, null, null);
    }
}

public static class Compare
{
    /// <summary>
    /// The gateway's order as the cloud should hold it. Timestamps are compared at PostgreSQL precision
    /// (microseconds); everything else must be identical.
    /// </summary>
    public static string Normalize(SyncOrder o) => JsonSerializer.Serialize(o with
    {
        CreatedAt = Micro(o.CreatedAt)!,
        UpdatedAt = Micro(o.UpdatedAt)!,
        SubmittedAt = Micro(o.SubmittedAt),
    }, SyncProtocol.Json);

    public static SyncOrder FromGateway(SharedOrder o) => new(
        o.Id, o.Number, o.Label, o.Status, o.Version, o.Currency,
        [.. o.Lines.Select(l => new SyncOrderLine(l.ProductId, l.Name, l.UnitPriceMinor, l.Quantity, l.LineTotalMinor, l.StationId))],
        o.ItemCount, o.TotalMinor, o.CreatedByName, o.UpdatedByName, o.SubmittedByName, o.CreatedAt, o.UpdatedAt, o.SubmittedAt);

    private static string? Micro(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var t = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
        return new DateTimeOffset(t.Ticks - (t.Ticks % 10), TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
    }
}
