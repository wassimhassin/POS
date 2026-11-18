using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Pos.Core;
using Pos.Core.Gateway;
using Pos.Core.Logging;
using Pos.Core.Printing;
using Pos.Gateway;

// Pos.Gateway — branch LAN gateway (technical prototype).
//
//   Pos.Gateway init     --branch-id <guid> --branch-name <name>
//   Pos.Gateway run      [--listen https://0.0.0.0:7443] [--host <dns-or-ip>]...      (default command)
//   Pos.Gateway enroll   --name <terminal name> [--minutes 15]
//   Pos.Gateway terminals
//   Pos.Gateway revoke   --terminal <terminal id>
//   Pos.Gateway printer  --destination <kitchen|bar|counter|receipt> (--printer <name> | --none) [--width 58|80]
//   Pos.Gateway backup   --out <file.db>
//   Pos.Gateway status
//   Pos.Gateway cloud-link --url https://<cloud> --code <one-time code> [--pin <cloud certificate SHA-256>]
//
//   run also accepts: --sync-batch <1..100> --sync-max-backoff <seconds> --sync-timeout <seconds>
//
// Every command accepts --data-dir <dir> (default: POS_GATEWAY_DATA_DIR, else %LOCALAPPDATA%\PosSaaS\Gateway).

var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "run";
var options = Cli.Parse(args.SkipWhile(a => a == command).ToArray());
var dataDir = Path.GetFullPath(options.One("data-dir")
    ?? Environment.GetEnvironmentVariable("POS_GATEWAY_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PosSaaS", "Gateway"));
Directory.CreateDirectory(dataDir);

using var loggerFactory = LoggerFactory.Create(b => b
    .SetMinimumLevel(LogLevel.Information)
    .AddFilter("Microsoft", LogLevel.Warning)
    .AddProvider(new JsonFileLoggerProvider(Path.Combine(dataDir, "logs")))
    .AddSimpleConsole(o => o.SingleLine = true));
var log = loggerFactory.CreateLogger("Pos.Gateway");

try
{
    var store = new GatewayStore(dataDir, loggerFactory);
    store.Initialize(); // migrations + SQLite integrity check: a corrupt file stops the gateway here

    switch (command)
    {
        case "init":
            var identity = store.InitIdentity(options.Required("branch-id"), options.Required("branch-name"));
            Console.WriteLine($"Succursale : {identity.BranchName} ({identity.BranchId})");
            return 0;

        case "enroll":
            var fingerprint = Cli.ReadFingerprint(dataDir);
            var minutes = int.Parse(options.One("minutes") ?? "15", CultureInfo.InvariantCulture);
            var (code, expiresAt) = store.CreateEnrollmentCode(options.Required("name"), TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 60)));
            Console.WriteLine($"Code d'appairage : {code}");
            Console.WriteLine($"Empreinte        : {GatewayCertificate.FormatFingerprint(fingerprint)}");
            Console.WriteLine($"Expire           : {expiresAt.ToLocalTime():HH:mm} (usage unique)");
            return 0;

        case "terminals":
            foreach (var t in store.ListTerminals())
            {
                Console.WriteLine($"{t.Id}  {t.Status,-8} {t.Name,-20} enrôlé {t.EnrolledAt}  vu {t.LastSeenAt ?? "-"}");
            }

            return 0;

        case "revoke":
            var revoked = store.RevokeTerminal(options.Required("terminal"));
            Console.WriteLine(revoked ? "Terminal révoqué." : "Terminal introuvable ou déjà révoqué.");
            return revoked ? 0 : 1;

        case "printer":
            var printer = options.Has("none") ? null : options.Required("printer");
            var destination = new PrintSettingsRepository(store.Database).UpdateDestination(
                options.Required("destination"), printer, int.Parse(options.One("width") ?? "80", CultureInfo.InvariantCulture));
            Console.WriteLine($"{destination.Name} → {destination.PrinterName ?? "(aucune)"} {destination.PaperWidthMm} mm");
            return 0;

        case "backup":
            store.Backup(options.Required("out"));
            Console.WriteLine("Sauvegarde vérifiée.");
            return 0;

        case "status":
            var id = store.GetIdentity();
            Console.WriteLine(id is null ? "Non initialisée." : $"Succursale {id.BranchName} ({id.BranchId}), schéma {GatewayStore.SchemaVersion}");
            Console.WriteLine($"Données : {dataDir}");
            var link = store.GetCloudLink();
            Console.WriteLine(link switch
            {
                null => "Cloud : non lié (les événements s'accumulent dans la file locale).",
                { IsLinked: false } => $"Cloud : liaison en attente vers {link.CloudUrl} (code à usage unique fourni le {link.RequestedAt}).",
                _ => $"Cloud : lié à {link.CloudUrl} — passerelle {link.GatewayId}, organisation {link.OrganizationName} ({link.OrganizationId}), entreprise {link.BusinessName} ({link.BusinessId})",
            });
            var sync = store.GetSyncStatus();
            Console.WriteLine($"Synchronisation : {sync.AckedSequence}/{sync.LastSequence} acquittés, {sync.Pending} en attente, dernier succès {sync.LastSuccessAt ?? "-"}");
            if (sync.LastError is not null)
            {
                Console.WriteLine($"Erreur ({sync.ConsecutiveFailures} échecs consécutifs{(sync.BlockedSequence is { } b ? $", bloquée à la séquence {b}" : "")}) : {sync.LastError}");
            }

            return 0;

        case "cloud-link":
            store.RequestCloudLink(options.Required("url"), options.Required("code"), options.One("pin"));
            Console.WriteLine("Liaison enregistrée : la passerelle en cours d'exécution s'enregistre auprès du cloud (voir « status »).");
            return 0;

        case "run":
            return await RunAsync(store, options, dataDir, loggerFactory, log);

        default:
            Console.Error.WriteLine($"Commande inconnue : {command}");
            return 2;
    }
}
catch (Exception ex) when (ex is PosValidationException or CliException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (Exception ex)
{
    log.LogCritical(ex, "Gateway {Command} failed", command);
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<int> RunAsync(GatewayStore store, Cli options, string dataDir, ILoggerFactory loggerFactory, ILogger log)
{
    var identity = store.GetIdentity()
        ?? throw new CliException("Passerelle non initialisée : exécutez d'abord « Pos.Gateway init --branch-id <guid> --branch-name <nom> ».");
    var listen = Cli.ParseListen(options.One("listen") ?? "https://0.0.0.0:7443");
    using var certificate = GatewayCertificate.LoadOrCreate(dataDir, options.All("host"), log);
    File.WriteAllText(Path.Combine(dataDir, Cli.FingerprintFile), certificate.Fingerprint);

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
    // No configuration from environment variables or files: nothing can add a plain-HTTP listener.
    builder.Configuration.Sources.Clear();
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton(loggerFactory);
    builder.Host.UseWindowsService(o => o.ServiceName = "PosSaaSGateway");

    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        k.Limits.MaxRequestBodySize = GatewayProtocol.MaxBodyBytes;
        k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
        k.Listen(listen, o =>
        {
            o.Protocols = HttpProtocols.Http1AndHttp2;
            o.UseHttps(certificate.Certificate);
        });
    });

    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        var strict = GatewayProtocol.Json;
        o.SerializerOptions.UnmappedMemberHandling = strict.UnmappedMemberHandling;
        o.SerializerOptions.RespectNullableAnnotations = strict.RespectNullableAnnotations;
        o.SerializerOptions.RespectRequiredConstructorParameters = strict.RespectRequiredConstructorParameters;
        o.SerializerOptions.NumberHandling = strict.NumberHandling;
        o.SerializerOptions.MaxDepth = strict.MaxDepth;
    });
    builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(store);
    builder.Services.AddSingleton(certificate);
    builder.Services.AddSingleton<TerminalAuth>();
    builder.Services.AddSingleton<ConnectedTerminals>();
    builder.Services.AddSingleton<Broadcaster>();
    builder.Services.AddHostedService<HubSessionMonitor>();

    // Phase 1.2 printing subsystem, unchanged: the gateway's print_jobs are fed by submitted orders.
    var printQueue = new PrintQueue(store.Database, loggerFactory.CreateLogger<PrintQueue>());
    var interrupted = printQueue.RecoverInterrupted();
    if (interrupted > 0)
    {
        log.LogWarning("{JobCount} print jobs were interrupted by the previous shutdown and need operator review", interrupted);
    }

    var printWorker = new PrintWorker(printQueue, new PrintSettingsRepository(store.Database), new WindowsSpoolerPrinter(), loggerFactory.CreateLogger<PrintWorker>());
    // Phase 1.4: cloud synchronization from the durable outbox (outbound HTTPS only; no listener added).
    var syncOptions = SyncOptions.Default with
    {
        BatchSize = Math.Clamp(int.Parse(options.One("sync-batch") ?? "100", CultureInfo.InvariantCulture), 1, Pos.Sync.SyncProtocol.MaxBatchEvents),
        MaxBackoff = TimeSpan.FromSeconds(Math.Clamp(double.Parse(options.One("sync-max-backoff") ?? "60", CultureInfo.InvariantCulture), 1, 3600)),
        RequestTimeout = TimeSpan.FromSeconds(Math.Clamp(double.Parse(options.One("sync-timeout") ?? "30", CultureInfo.InvariantCulture), 1, 300)),
    };
    var syncWorker = new CloudSyncWorker(store, syncOptions, loggerFactory.CreateLogger<CloudSyncWorker>());
    builder.Services.AddHostedService(_ => syncWorker);

    var orders = new OrderService(store, printQueue, loggerFactory.CreateLogger<OrderService>())
    {
        PrintJobsCreated = printWorker.Signal,
        OutboxAppended = syncWorker.Signal,
    };
#if DEBUG
    orders.CrashAfterCommitOf = Environment.GetEnvironmentVariable("POS_GATEWAY_FAULT_CRASH_AFTER_COMMIT");
    if (orders.CrashAfterCommitOf is not null)
    {
        log.LogWarning("Fault injection armed: crash after committing {OperationType}", orders.CrashAfterCommitOf);
    }
#endif
    builder.Services.AddSingleton(orders);
    builder.Services.AddHostedService(_ => new PrintWorkerService(printWorker));

    builder.Services.AddAuthentication(TerminalTokenHandler.SchemeName)
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TerminalTokenHandler>(TerminalTokenHandler.SchemeName, null);
    // Deny by default: an endpoint without an explicit AllowAnonymous requires an authenticated terminal.
    builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    builder.Services.AddSignalR(o =>
    {
        o.MaximumReceiveMessageSize = 4 * 1024;
        o.EnableDetailedErrors = false;
    });
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = 429;
        o.AddPolicy(GatewayApi.AnonymousRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
        o.AddPolicy(GatewayApi.EnrollRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    });

    var app = builder.Build();
    app.Use(async (http, next) =>
    {
        if (!GatewayApi.IsLocalNetwork(http.Connection.RemoteIpAddress))
        {
            log.LogWarning("Refused connection from non-local address {RemoteAddress}", http.Connection.RemoteIpAddress);
            http.Abort();
            return;
        }

        await next();
    });
    app.Use(GatewayApi.HandleErrors);
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
    GatewayApi.Map(app);

    app.Lifetime.ApplicationStarted.Register(() => log.LogInformation(
        "Gateway listening on https://{ListenEndpoint} for branch {BranchName} ({BranchId}); certificate SHA-256 {Fingerprint}",
        listen, identity.BranchName, identity.BranchId, certificate.Fingerprint));
    await app.RunAsync();
    log.LogInformation("Gateway stopped");
    return 0;
}

/// <summary>Runs the Phase 1.2 print worker for the gateway's lifetime.</summary>
internal sealed class PrintWorkerService(PrintWorker worker) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => worker.RunAsync(stoppingToken);

    public override void Dispose()
    {
        base.Dispose();
        worker.Dispose();
    }
}

internal sealed class CliException(string message) : Exception(message)
{
    public CliException() : this("Argument invalide.") { }

    public CliException(string message, Exception innerException) : this(message) => _ = innerException;
}

internal sealed class Cli
{
    public const string FingerprintFile = "gateway-tls.sha256";
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public static Cli Parse(string[] args)
    {
        var cli = new Cli();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliException($"Argument inattendu : {args[i]}");
            }

            var name = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
            if (!cli._values.TryGetValue(name, out var list))
            {
                cli._values[name] = list = [];
            }

            list.Add(value);
        }

        return cli;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? One(string name) => _values.TryGetValue(name, out var v) ? v[^1] : null;

    public IReadOnlyList<string> All(string name) => _values.TryGetValue(name, out var v) ? v : [];

    public string Required(string name) =>
        One(name) is { Length: > 0 } value ? value : throw new CliException($"Option requise : --{name}");

    public static IPEndPoint ParseListen(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/")
        {
            throw new CliException("--listen doit être une adresse https://<ip>:<port> (HTTP non chiffré interdit).");
        }

        var host = uri.Host.Trim('[', ']');
        var ip = host == "localhost" ? IPAddress.Loopback : IPAddress.TryParse(host, out var parsed) ? parsed
            : throw new CliException("--listen doit utiliser une adresse IP (ex. https://0.0.0.0:7443).");
        return new IPEndPoint(ip, uri.Port);
    }

    public static string ReadFingerprint(string dataDir)
    {
        var path = Path.Combine(dataDir, FingerprintFile);
        return File.Exists(path)
            ? File.ReadAllText(path).Trim()
            : throw new CliException("Démarrez la passerelle une première fois (« Pos.Gateway run ») pour générer son certificat.");
    }
}
