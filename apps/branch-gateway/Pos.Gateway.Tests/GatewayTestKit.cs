using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Core.Data;
using Pos.Core.Gateway;

namespace Pos.Gateway.Tests;

/// <summary>A real Pos.Gateway.exe process on 127.0.0.1 with its own data directory.</summary>
public sealed partial class GatewayProcess : IAsyncDisposable
{
    public const string BranchName = "Succursale de test";

    private Process? _process;
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public GatewayProcess()
    {
        DataDir = Directory.CreateTempSubdirectory("pos-gw-test-").FullName;
        Port = FreePort();
        BranchId = Guid.NewGuid().ToString("D");
        Run("init", "--branch-id", BranchId, "--branch-name", BranchName);
    }

    public string DataDir { get; }

    public int Port { get; }

    public string BranchId { get; }

    public string Url => $"https://127.0.0.1:{Port}/";

    public string Fingerprint => File.ReadAllText(Path.Combine(DataDir, "gateway-tls.sha256")).Trim();

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>The running gateway process (memory sampling in the reliability tests).</summary>
    public Process? CurrentProcess => _process;

    /// <summary>Extra arguments for « run » (e.g. sync tuning in cloud tests).</summary>
    public List<string> RunArguments { get; } = [];

    /// <summary>Recent output lines of the running gateway (bounded), for failure diagnostics.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Log { get; } = new();

    private void Record(string? line)
    {
        if (line is null)
        {
            return;
        }

        Log.Enqueue(line);
        while (Log.Count > 400 && Log.TryDequeue(out _))
        {
        }
    }

    public static string Executable
    {
        get
        {
            // apps/<area>/<TestProject>/bin/<Configuration>/net10.0-windows → same configuration of the gateway.
            var testBin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = testBin.Parent!.Name;
            var apps = testBin.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
            var exe = Path.Combine(apps, "branch-gateway", "Pos.Gateway", "bin", configuration, "net10.0-windows", "Pos.Gateway.exe");
            return File.Exists(exe) ? exe : throw new FileNotFoundException("Build Pos.Gateway first.", exe);
        }
    }

    public GatewayProcess WithEnvironment(string name, string value)
    {
        _environment[name] = value;
        return this;
    }

    public GatewayProcess WithoutEnvironment(string name)
    {
        _environment.Remove(name);
        return this;
    }

    public async Task StartAsync()
    {
        var info = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "run", "--data-dir", DataDir, "--listen", $"https://127.0.0.1:{Port}" }.Concat(RunArguments))
        {
            info.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in _environment)
        {
            info.Environment[name] = value;
        }

        _process = Process.Start(info)!;
        TestProcesses.Track(_process);
        _process.OutputDataReceived += (_, e) => Record(e.Data);
        _process.ErrorDataReceived += (_, e) => Record(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"Gateway exited during startup with code {_process.ExitCode}.");
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

        throw new TimeoutException("Gateway did not start.");
    }

    /// <summary>Hard kill (power cut / crash): no graceful shutdown.</summary>
    public async Task KillAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
    }

    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await _process!.WaitForExitAsync(cts.Token);
        return _process.ExitCode;
    }

    public (string Code, string Fingerprint) Enroll(string terminalName)
    {
        var output = Run("enroll", "--name", terminalName);
        var code = CodeLine().Match(output).Groups[1].Value;
        return (code, Fingerprint);
    }

    public string Run(params string[] args)
    {
        var (exitCode, output) = RunRaw(args);
        return exitCode == 0 ? output : throw new InvalidOperationException($"Pos.Gateway {args[0]} failed ({exitCode}): {output}");
    }

    public (int ExitCode, string Output) RunRaw(params string[] args)
    {
        var info = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args.Append("--data-dir").Append(DataDir))
        {
            info.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in _environment)
        {
            info.Environment[name] = value;
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(30_000);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>Direct read access to the gateway's SQLite file: ground truth for assertions.</summary>
    public long Count(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDir, "gateway.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        await KillAsync();
        _process?.Dispose();
        SqliteConnection.ClearAllPools();
        for (var i = 0; i < 10; i++)
        {
            try
            {
                Directory.Delete(DataDir, recursive: true);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [GeneratedRegex(@"Code d'appairage : (\S+)")]
    private static partial Regex CodeLine();
}

/// <summary>A simulated POS terminal: its own POS SQLite file + the production <see cref="GatewayClient"/>.</summary>
public sealed class SimTerminal : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    private SimTerminal(string dataDir)
    {
        DataDir = dataDir;
        Database = new PosDatabase(Path.Combine(dataDir, "pos.db"), NullLogger<PosDatabase>.Instance);
        Database.Initialize();
        Store = new TerminalStore(Database);
        Client = new GatewayClient(Store, NullLogger<GatewayClient>.Instance);
    }

    public string DataDir { get; }

    public PosDatabase Database { get; }

    public TerminalStore Store { get; }

    public GatewayClient Client { get; }

    public string TerminalId => Store.TerminalId;

    public static SimTerminal Create() => new(Directory.CreateTempSubdirectory("pos-term-test-").FullName);

    /// <summary>Same POS data directory, new process-equivalent (terminal restart).</summary>
    public static SimTerminal Reopen(string dataDir) => new(dataDir);

    public static async Task<SimTerminal> EnrolledAsync(GatewayProcess gateway, string name)
    {
        var terminal = Create();
        var (code, fingerprint) = gateway.Enroll(name);
        await terminal.Client.EnrollAsync(gateway.Url, fingerprint, code);
        terminal.Start();
        await terminal.WaitConnectedAsync();
        return terminal;
    }

    public void Start() => _loop = Task.Run(() => Client.RunAsync(_stop.Token));

    public Task WaitConnectedAsync() => WaitUntilAsync(() => Client.Status.State == GatewayState.Connected, "connected");

    public Task WaitUntilAsync(Func<bool> condition, string what, int timeoutSeconds = 20) =>
        Wait.UntilAsync(condition, $"{Client.Status.TerminalName ?? "terminal"}: {what} (state {Client.Status.State}, {Client.Status.LastError})", timeoutSeconds);

    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Client.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            await StopAsync();
        }

        _stop.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Every server process a test starts. Killed when the test host exits, so a fixture whose InitializeAsync threw (xUnit
/// then skips DisposeAsync) cannot leave a cloud or gateway running.
/// </summary>
public static class TestProcesses
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> Running = new();

    static TestProcesses() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        foreach (var process in Running.Values)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    };

    public static void Track(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        Running[process.Id] = process;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Running.TryRemove(process.Id, out _);
    }
}

public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}");
            }

            await Task.Delay(50);
        }
    }

    public static string NewId() => Guid.NewGuid().ToString("D");

    /// <summary>Value comparison of orders (records holding lists compare lists by reference).</summary>
    public static string Json(SharedOrder? order) => System.Text.Json.JsonSerializer.Serialize(order, GatewayProtocol.Json);
}

/// <summary>Raw HTTP access to the gateway (pinned TLS) for protocol-level negative tests.</summary>
public static class RawGateway
{
    public static HttpClient Http(GatewayProcess gateway) => new(new SocketsHttpHandler
    {
        SslOptions = { RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData())) == gateway.Fingerprint },
    })
    {
        BaseAddress = new Uri(gateway.Url),
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Performs the challenge/response login with the terminal's own key and returns a bearer token.</summary>
    public static async Task<string> LoginAsync(HttpClient http, SimTerminal terminal, string branchId)
    {
        var challenge = await (await http.PostAsJsonAsync("v1/auth/challenge", new ChallengeRequest(terminal.TerminalId))).Content.ReadFromJsonAsync<ChallengeResponse>();
        var signature = terminal.Store.Sign(GatewayProtocol.AuthPayload(branchId, terminal.TerminalId, challenge!.Nonce, terminal.Store.GetLink()!.CertificateSha256));
        using var response = await http.PostAsJsonAsync("v1/auth/token", new TokenRequest(terminal.TerminalId, challenge.Nonce, Convert.ToBase64String(signature)));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!.Token;
    }

    public static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient http, HttpMethod method, string path, string? token, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
