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
//   Pos.Gateway backup   [--out <file.db>]          verified copy + <file>.db.json metadata (default: configured dir)
//   Pos.Gateway backup-config --dir <folder or \servershare> [--interval-seconds 3600] [--keep 48]
//   Pos.Gateway restore  --from <backup.db> [--dry-run] [--branch-id <guid>] [--replace-newer]   (gateway stopped)
//   Pos.Gateway reconcile [--dry-run]               compare with / import cloud-confirmed events (gateway stopped)
//   Pos.Gateway recovery-verify [--without-cloud]    end a recovery: shared writes resume
//   Pos.Gateway quarantine [--resolve <id> --note <decision>]
//   Pos.Gateway replace-terminal --terminal <old id> --name <new name>   revoke + one-time code for the replacement
//   Pos.Gateway cloud-relink --url https://<cloud> --code <one-time code> [--pin <sha256>]   new gateway credential
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

InstanceLock? instance = null;
try
{
    // Restore runs before the database is opened (it replaces it) and holds the data-directory lock itself.
    if (command == "restore")
    {
        var plan = GatewayBackup.Restore(dataDir, options.Required("from"),
            new RestoreOptions(options.Has("dry-run"), options.One("branch-id"), options.Has("replace-newer")), loggerFactory);
        var b = plan.Backup;
        Console.WriteLine($"Sauvegarde : succursale {b.BranchName} ({b.BranchId}), créée le {b.CreatedAt} sur {b.SourceMachine}, schéma {b.GatewaySchemaVersion}/{b.PosSchemaVersion}");
        Console.WriteLine($"Contenu : séquence {b.OutboxLastSequence} (acquittée par le cloud jusqu'à {b.SyncAckedSequence}), opération {b.LastOperationPosition}, SHA-256 {b.Sha256} vérifié, intégrité ok");
        Console.WriteLine($"Données actuelles : {plan.CurrentState ?? "aucune"}{(plan.CurrentOutboxSequence is { } cur ? $" (séquence {cur}, {plan.CurrentUnsynced} non synchronisés)" : "")}");
        foreach (var warning in plan.Warnings)
        {
            Console.WriteLine($"Avertissement : {warning}");
        }

        Console.WriteLine(options.Has("dry-run")
            ? "Simulation : aucune modification."
            : "Restauration terminée. Modifications partagées BLOQUÉES jusqu'à « reconcile » (si lié au cloud) puis « recovery-verify ». Anciennes données conservées dans forensic\\.");
        return 0;
    }

    if (File.Exists(Path.Combine(dataDir, GatewayBackup.RestoreMarker)))
    {
        throw new CliException("Une restauration a été interrompue : relancez « Pos.Gateway restore --from <la même sauvegarde> » avant toute autre commande.");
    }

    // One process serving (or reconciling) a database at a time; acquired before the database is opened.
    if (command is "run" or "reconcile")
    {
        instance = InstanceLock.Acquire(dataDir, command);
    }

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
        {
            BackupMetadata metadata;
            if (options.One("out") is { Length: > 0 } outFile)
            {
                metadata = GatewayBackup.Create(store, outFile);
            }
            else
            {
                var config = GatewayBackup.GetConfig(store) ?? throw new CliException("Aucune destination configurée : --out <fichier.db> ou « backup-config ».");
                var run = GatewayBackup.RunConfigured(store, config, log);
                if (run.Status != "ok")
                {
                    Console.Error.WriteLine($"Sauvegarde ÉCHOUÉE : {run.Error}");
                    return 1;
                }

                metadata = GatewayBackup.Validate(run.File!);
            }

            Console.WriteLine($"Sauvegarde vérifiée (intégrité, SHA-256 {metadata.Sha256}), séquence {metadata.OutboxLastSequence}.");
            if (!metadata.Offsite)
            {
                Console.WriteLine("Avertissement : même disque que la passerelle — ce n'est PAS une protection contre une panne du disque ou du PC.");
            }

            return 0;
        }

        case "backup-config":
        {
            var config = GatewayBackup.Configure(store, options.Required("dir"),
                int.Parse(options.One("interval-seconds") ?? "3600", CultureInfo.InvariantCulture), int.Parse(options.One("keep") ?? "48", CultureInfo.InvariantCulture));
            Console.WriteLine($"Sauvegardes : {config.Directory}, toutes les {config.IntervalSeconds} s, {config.Keep} conservées (appliqué par la passerelle en cours d'exécution).");
            if (!GatewayBackup.IsOffsite(dataDir, config.Directory))
            {
                Console.WriteLine("Avertissement : même disque que la passerelle — ce n'est PAS une protection contre une panne. Utilisez un partage réseau (\\\\serveur\\partage) ou un autre support.");
            }

            return 0;
        }

        case "reconcile":
        {
            using var cloud = new CloudClient(store, TimeSpan.FromSeconds(30));
            var printQueue = new PrintQueue(store.Database, loggerFactory.CreateLogger<PrintQueue>());
            var report = await GatewayReconciler.RunAsync(store, printQueue, cloud, options.Has("dry-run"), CancellationToken.None);
            Console.WriteLine($"Local : séquence {report.LocalSequenceBefore} (acquittée {report.AckedBefore}) — cloud : {report.CloudSequence}");
            Console.WriteLine($"Vérifiés (accusés perdus) : {report.VerifiedUnacknowledged} ; importés depuis le cloud : {report.Imported} (dont {report.ImportedSubmissions} envois, {report.UncertainPrintJobs} tickets marqués incertains)");
            Console.WriteLine($"Événements uniquement locaux (à synchroniser) : {report.LocalOnlyEvents}");
            foreach (var q in report.Quarantined)
            {
                Console.WriteLine($"QUARANTAINE (bloquant) : {q}");
            }

            foreach (var n in report.Notes)
            {
                Console.WriteLine($"Note : {n}");
            }

            Console.WriteLine("Les opérations faites sur l'ancienne passerelle après sa dernière synchronisation et absentes de la sauvegarde ne sont connues de personne : elles ne peuvent pas être récupérées.");
            Console.WriteLine(report.DryRun ? "Simulation : aucune modification." : $"État : {report.StateAfter}");
            return report.Quarantined.Count == 0 ? 0 : 3;
        }

        case "recovery-verify":
        {
            var state = store.VerifyRecovery(options.Has("without-cloud"));
            Console.WriteLine($"Récupération vérifiée : état {state.Status}. Les modifications partagées reprennent.");
            return 0;
        }

        case "quarantine":
            if (options.One("resolve") is { Length: > 0 } resolveId)
            {
                var resolved = store.ResolveQuarantine(long.Parse(resolveId, CultureInfo.InvariantCulture), options.Required("note"));
                Console.WriteLine(resolved ? "Décision enregistrée (élément conservé)." : "Élément introuvable ou déjà résolu.");
                return resolved ? 0 : 1;
            }

            foreach (var q in store.ListQuarantine())
            {
                Console.WriteLine($"#{q.Id} {(q.Blocking ? "BLOQUANT" : "info")} {q.Kind} seq={q.Sequence?.ToString(CultureInfo.InvariantCulture) ?? "-"} {q.Detail}" +
                    (q.ResolvedAt is null ? "" : $" — résolu le {q.ResolvedAt} : {q.Resolution}"));
            }

            return 0;

        case "replace-terminal":
        {
            // Lost/broken till: revoke its identity, issue a one-time code for the replacement (new identity, new key).
            var old = options.Required("terminal");
            var known = store.GetTerminal(old) ?? throw new CliException("Terminal introuvable.");
            var wasActive = store.RevokeTerminal(old);
            var (replacementCode, replacementExpires) = store.CreateEnrollmentCode(options.Required("name"), TimeSpan.FromMinutes(15));
            GatewayStore.Audit(dataDir, "terminal_replaced", new { old, oldName = known.Name, wasActive, replacement = options.Required("name") });
            Console.WriteLine($"Terminal {known.Name} ({old}) révoqué{(wasActive ? "" : " (l'était déjà)")} : il ne peut plus se connecter.");
            Console.WriteLine($"Code d'appairage : {replacementCode}");
            Console.WriteLine($"Empreinte        : {GatewayCertificate.FormatFingerprint(Cli.ReadFingerprint(dataDir))}");
            Console.WriteLine($"Expire           : {replacementExpires.ToLocalTime():HH:mm} (usage unique)");
            return 0;
        }

        case "cloud-relink":
            store.RelinkCloud(options.Required("url"), options.Required("code"), options.One("pin"));
            Console.WriteLine("Nouvelle liaison enregistrée (l'ancienne est conservée dans l'audit). Exécutez « reconcile » : il enregistre cette passerelle puis compare avec le cloud.");
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

            var recovery = store.GetRecoveryState();
            Console.WriteLine(recovery.IsNormal
                ? "Récupération : aucune (service normal)."
                : $"RÉCUPÉRATION EN COURS : {recovery.Status} depuis {recovery.Since} — modifications partagées bloquées. {recovery.Reason}");
            var pendingQuarantine = store.ListQuarantine(unresolvedOnly: true);
            if (pendingQuarantine.Count > 0)
            {
                Console.WriteLine($"Quarantaine : {pendingQuarantine.Count} élément(s) non résolu(s), dont {pendingQuarantine.Count(q => q.Blocking)} bloquant(s).");
            }

            var backupConfig = GatewayBackup.GetConfig(store);
            var (lastOk, lastFailed) = GatewayBackup.LastRuns(store);
            Console.WriteLine(backupConfig is null
                ? "Sauvegardes : non configurées (« backup-config --dir <dossier> »)."
                : $"Sauvegardes : {backupConfig.Directory}, toutes les {backupConfig.IntervalSeconds} s, {backupConfig.Keep} conservées" +
                  (GatewayBackup.IsOffsite(dataDir, backupConfig.Directory) ? "." : " — MÊME DISQUE : pas une protection contre une panne."));
            Console.WriteLine($"Dernière sauvegarde réussie : {(lastOk is null ? "aucune" : $"{lastOk.FinishedAt} (séquence {lastOk.Sequence}) {lastOk.File}")}");
            if (lastFailed is not null && string.CompareOrdinal(lastFailed.StartedAt, lastOk?.StartedAt ?? "") > 0)
            {
                Console.WriteLine($"DERNIÈRE SAUVEGARDE EN ÉCHEC : {lastFailed.StartedAt} — {lastFailed.Error}");
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
finally
{
    instance?.Dispose();
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
    builder.Services.AddHostedService(_ => new BackupWorker(store, loggerFactory.CreateLogger<BackupWorker>(), TimeProvider.System));
    var recovery = store.GetRecoveryState();
    if (!recovery.IsNormal)
    {
        log.LogWarning("Gateway starting in recovery state {RecoveryStatus}: shared writes are blocked. {Reason}", recovery.Status, recovery.Reason);
    }

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
