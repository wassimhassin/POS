using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Pos.Cloud;
using Pos.Sync;

// Pos.Cloud — multi-tenant cloud API (technical prototype).
//
//   Pos.Cloud migrate                                                   (POS_CLOUD_MIGRATION_DB: schema owner)
//   Pos.Cloud org-create      --name <name> [--id <guid>]
//   Pos.Cloud business-create --org <guid> --name <name> [--id <guid>]
//   Pos.Cloud branch-create   --business <guid> --name <name> [--id <existing gateway branch id>]
//   Pos.Cloud gateway-code    --branch <guid> [--minutes 15] [--replace] one-time gateway provisioning code; --replace
//                                                                       revokes the branch's active gateway on use
//   Pos.Cloud gateways        [--branch <guid>]
//   Pos.Cloud gateway-revoke  --gateway <guid>
//   Pos.Cloud owner-invite    --org <guid> --email <address>     emails a one-time owner invitation (72 h; MFA required to accept)
//   Pos.Cloud data-key-new                                             prints a new data-protection key entry (POS_CLOUD_DATA_KEYS)
//   Pos.Cloud mfa-rekey                                                re-encrypts MFA secrets with the active data key (legacy + old keys)
//   Pos.Cloud cleanup                                                  deletes ended sessions (30 d), expired tokens (7 d), abandoned MFA setups
//   Pos.Cloud audit-prune     [--older-than-days 400]                  owner role (POS_CLOUD_MIGRATION_DB); minimum 365 days
//   Pos.Cloud report-key      --org <guid> [--business <guid> [--branch <guid>]] --name <name>
//   Pos.Cloud dev-cert        --out <file.pfx> [--host <name-or-ip>]...  (POS_CLOUD_CERT_PASSWORD)
//   Pos.Cloud run             --listen https://<ip>:<port> --certificate <file.pfx> [--openapi]  (--openapi serves /openapi/v1.json)
//   Pos.Cloud check-config    --listen https://<ip>:<port> --certificate <file.pfx>   validates settings + database, no listener
//   Pos.Cloud openapi         --out <file.json>                                       writes the OpenAPI document (no database)
//
// Runtime and admin commands use POS_CLOUD_DB (a role with data privileges only, no DDL).
// No secret is hard-coded: connection strings and the certificate password come from the environment.

var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "run";
var options = Cli.Parse([.. args.Skip(args.Length > 0 && args[0] == command ? 1 : 0)]);

using var loggerFactory = LoggerFactory.Create(b => b
    .SetMinimumLevel(LogLevel.Information)
    .AddFilter("Microsoft", LogLevel.Warning)
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss.fff ";
    }));
var log = loggerFactory.CreateLogger("Pos.Cloud");

try
{
    switch (command)
    {
        case "migrate":
        {
            await using var db = Cli.Db("POS_CLOUD_MIGRATION_DB");
            await db.Database.MigrateAsync();
            Console.WriteLine($"schema={string.Join(',', await db.Database.GetAppliedMigrationsAsync())}");
            return 0;
        }

        case "org-create":
        {
            await using var db = Cli.Db();
            var organization = new Organization { Id = options.Id("id") ?? Guid.NewGuid(), Name = options.Name(), CreatedAt = DateTimeOffset.UtcNow };
            db.Organizations.Add(organization);
            await db.SaveChangesAsync();
            Console.WriteLine($"organization_id={organization.Id:D}");
            return 0;
        }

        case "business-create":
        {
            await using var db = Cli.Db();
            var organizationId = options.Id("org") ?? throw new CliException("Option requise : --org");
            if (!await db.Organizations.AnyAsync(o => o.Id == organizationId))
            {
                throw new CliException("Organisation introuvable.");
            }

            var business = new Business { OrganizationId = organizationId, Id = options.Id("id") ?? Guid.NewGuid(), Name = options.Name(), CreatedAt = DateTimeOffset.UtcNow };
            db.Businesses.Add(business);
            await db.SaveChangesAsync();
            Console.WriteLine($"business_id={business.Id:D}");
            return 0;
        }

        case "branch-create":
        {
            await using var db = Cli.Db();
            var businessId = options.Id("business") ?? throw new CliException("Option requise : --business");
            var business = await db.Businesses.SingleOrDefaultAsync(b => b.Id == businessId) ?? throw new CliException("Entreprise introuvable.");
            // --id: reuse the branch id an existing gateway was initialized with (stable association, no re-init).
            var branch = new Branch { OrganizationId = business.OrganizationId, BusinessId = business.Id, Id = options.Id("id") ?? Guid.NewGuid(), Name = options.Name(), CreatedAt = DateTimeOffset.UtcNow };
            db.Branches.Add(branch);
            db.BranchSyncStates.Add(new BranchSyncState { OrganizationId = branch.OrganizationId, BusinessId = branch.BusinessId, BranchId = branch.Id });
            await db.SaveChangesAsync();
            Console.WriteLine($"branch_id={branch.Id:D}");
            return 0;
        }

        case "gateway-code":
        {
            await using var db = Cli.Db();
            var branchId = options.Id("branch") ?? throw new CliException("Option requise : --branch");
            var branch = await db.Branches.SingleOrDefaultAsync(b => b.Id == branchId) ?? throw new CliException("Succursale introuvable.");
            var minutes = Math.Clamp(int.Parse(options.One("minutes") ?? "15", CultureInfo.InvariantCulture), 1, 60);
            var code = GatewayRegistration.NewCode();
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(minutes);
            db.ProvisioningCodes.Add(new ProvisioningCode
            {
                CodeHash = GatewayRegistration.HashCode(code),
                OrganizationId = branch.OrganizationId,
                BusinessId = branch.BusinessId,
                BranchId = branch.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expiresAt,
                ReplacesActive = options.Has("replace"),
            });
            await db.SaveChangesAsync();
            Console.WriteLine($"code={code}");
            Console.WriteLine($"expires_at={expiresAt:O}");
            Console.WriteLine($"replaces_active={(options.Has("replace") ? 1 : 0)}");
            return 0;
        }

        case "gateways":
        {
            await using var db = Cli.Db();
            var branchId = options.Id("branch");
            foreach (var g in await db.GatewayCredentials.Where(g => branchId == null || g.BranchId == branchId).OrderBy(g => g.RegisteredAt).ToListAsync())
            {
                Console.WriteLine($"{g.Id:D} branch={g.BranchId:D} registered={g.RegisteredAt:O} revoked={g.RevokedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "-"} seen={g.LastSeenAt?.ToString("O", CultureInfo.InvariantCulture) ?? "-"}");
            }

            return 0;
        }

        case "gateway-revoke":
        {
            await using var db = Cli.Db();
            var gatewayId = options.Id("gateway") ?? throw new CliException("Option requise : --gateway");
            var revoked = await db.GatewayCredentials.Where(g => g.Id == gatewayId && g.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, DateTimeOffset.UtcNow));
            Console.WriteLine(revoked == 1 ? "revoked=1" : "revoked=0");
            return revoked == 1 ? 0 : 1;
        }

        case "report-key":
        {
            await using var db = Cli.Db();
            var organizationId = options.Id("org") ?? throw new CliException("Option requise : --org");
            var businessId = options.Id("business");
            var branchId = options.Id("branch");
            var valid = branchId is { } br
                ? businessId is { } bu && await db.Branches.AnyAsync(b => b.Id == br && b.BusinessId == bu && b.OrganizationId == organizationId)
                : businessId is { } bz ? await db.Businesses.AnyAsync(b => b.Id == bz && b.OrganizationId == organizationId)
                : await db.Organizations.AnyAsync(o => o.Id == organizationId);
            if (!valid)
            {
                throw new CliException("Portée invalide : la succursale doit appartenir à l'entreprise, et l'entreprise à l'organisation.");
            }

            var (key, token) = ReportKeyHandler.Create(organizationId, businessId, branchId, options.Name(), DateTimeOffset.UtcNow);
            db.ReportKeys.Add(key);
            await db.SaveChangesAsync();
            Console.WriteLine($"report_key={token}");
            return 0;
        }

        case "dev-cert":
        {
            var path = Path.GetFullPath(options.Required("out"));
            using var certificate = DevCertificate.Create(options.All("host"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, Cli.CertificatePassword()));
            File.WriteAllText(Path.ChangeExtension(path, ".pem"), certificate.ExportCertificatePem());
            Console.WriteLine($"sha256={Convert.ToHexString(SHA256.HashData(certificate.RawData))}");
            return 0;
        }

        case "run":
            return await RunAsync(options, loggerFactory, log);

        case "owner-invite":
        {
            // Platform-operator onboarding of an organization's first owner: there is no public way to create an
            // organization or a privileged account. The one-time link is only emailed, never printed or logged.
            var organizationId = options.Id("org") ?? throw new CliException("Option requise : --org");
            var email = options.Required("email");
            var problems = new List<string>();
            var auth = CloudSettings.LoadAuth(Environment.GetEnvironmentVariable, problems) ?? throw new CliException(string.Join(" ", problems));
            await using var app = CloudHost.Build(Cli.ConnectionString(CloudSettings.RuntimeDb), auth, loggerFactory, kestrel: null, backgroundMail: false);
            await using var scope = app.Services.CreateAsyncScope();
            var expires = await scope.ServiceProvider.GetRequiredService<Pos.Cloud.Identity.AuthService>().InviteOwnerAsync(organizationId, email, CancellationToken.None);
            Console.WriteLine("invite=sent");
            Console.WriteLine($"expires_at={expires:O}");
            return 0;
        }

        case "data-key-new":
            // A new data-protection key entry for the secret store (printed once; put it FIRST in POS_CLOUD_DATA_KEYS
            // to make it active, keep the previous keys after it until « mfa-rekey » has moved everything).
            Console.WriteLine($"data_key={Pos.Cloud.Identity.SecretKeyRing.NewKeyEntry(DateTimeOffset.UtcNow)}");
            return 0;

        case "mfa-rekey":
        {
            var problems = new List<string>();
            var keys = Pos.Cloud.Identity.SecretKeyRing.Parse(Environment.GetEnvironmentVariable(CloudSettings.DataKeys), CloudSettings.DataKeys, problems)
                ?? throw new CliException(string.Join(" ", problems));
            await using var db = Cli.Db();
            var result = await Pos.Cloud.Identity.MfaSecrets.RekeyAllAsync(db, keys, 200, CancellationToken.None);
            Console.WriteLine($"resealed={result.Resealed}");
            Console.WriteLine($"unreadable={result.Unreadable}");
            Console.WriteLine($"remaining={result.Remaining}");
            return result.Remaining == 0 ? 0 : 1;
        }

        case "cleanup":
        {
            // The same pass the server runs hourly (runtime role): ended sessions, expired tokens, abandoned MFA setups.
            await using var db = Cli.Db();
            var result = await Pos.Cloud.Identity.AuthRetention.CleanupAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);
            Console.WriteLine($"sessions={result.Sessions}");
            Console.WriteLine($"tokens={result.Tokens}");
            Console.WriteLine($"abandoned_mfa={result.AbandonedMfa}");
            Console.WriteLine($"invitations={result.Invitations}");
            Console.WriteLine($"complete={(result.Complete ? 1 : 0)}");
            return 0;
        }

        case "audit-prune":
        {
            // Owner role only: the runtime role cannot delete audit rows. Minimum retention enforced.
            var days = int.TryParse(options.One("older-than-days") ?? Pos.Cloud.Identity.AuthRetention.DefaultAuditDays.ToString(CultureInfo.InvariantCulture),
                NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : throw new CliException("--older-than-days : nombre entier attendu.");
            await using var db = Cli.Db(CloudSettings.MigrationDb);
            var deleted = await Pos.Cloud.Identity.AuthRetention.PruneAuditAsync(db, DateTimeOffset.UtcNow, days, CancellationToken.None);
            Console.WriteLine($"audit_deleted={deleted}");
            return 0;
        }

        case "check-config":
            return await CheckConfigAsync(options, log);

        case "openapi":
            return await WriteOpenApiAsync(options, loggerFactory);

        default:
            Console.Error.WriteLine($"Commande inconnue : {command}");
            return 2;
    }
}
catch (CliException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (Exception ex)
{
    log.LogCritical(ex, "Pos.Cloud {Command} failed", command);
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<int> RunAsync(Cli options, ILoggerFactory loggerFactory, ILogger log)
{
    using var settings = ValidatedSettings(options, log);
    if (settings is null)
    {
        return 2;
    }

    var app = CloudHost.Build(settings.ConnectionString, settings.Auth, loggerFactory, kestrel => kestrel.Listen(settings.Listen, o => o.UseHttps(settings.Certificate)));

    // Refuse to serve on an outdated schema (migrations are applied separately, by the schema owner).
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var pending = (await scope.ServiceProvider.GetRequiredService<CloudDb>().Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
        {
            throw new InvalidOperationException($"Schéma non à jour ({string.Join(", ", pending)}) : exécutez « Pos.Cloud migrate ».");
        }
    }

#if DEBUG
    CloudFaults.Configure(Environment.GetEnvironmentVariable("POS_CLOUD_FAULT"), log);
#endif

    CloudHost.UsePipeline(app, exposeOpenApi: settings.OpenApi);
    app.Lifetime.ApplicationStarted.Register(() => log.LogInformation("Cloud API listening on https://{ListenEndpoint}", settings.Listen));
    await app.RunAsync();
    return 0;
}

/// <summary>All settings checked at once; problems are printed by setting name, never with their values.</summary>
static CloudRunSettings? ValidatedSettings(Cli options, ILogger log)
{
    var report = CloudSettings.LoadRun(options, Environment.GetEnvironmentVariable, DateTimeOffset.UtcNow);
    foreach (var warning in report.Warnings)
    {
        log.LogWarning("Configuration: {Warning}", warning);
    }

    if (report.Settings is null)
    {
        Console.Error.WriteLine("Configuration invalide :");
        foreach (var error in report.Errors)
        {
            Console.Error.WriteLine($"  - {error}");
        }
    }

    return report.Settings;
}

/// <summary>
/// Deploy-time check, without listening: the same validation as <c>run</c>, then the database as the runtime role
/// (reachable, schema up to date, no DDL privilege, not a superuser). Exit 0 = ready to run.
/// </summary>
static async Task<int> CheckConfigAsync(Cli options, ILogger log)
{
    using var settings = ValidatedSettings(options, log);
    if (settings is null)
    {
        return 2;
    }

    var errors = new List<string>();
    await using var db = new CloudDb(new DbContextOptionsBuilder<CloudDb>().UseNpgsql(settings.ConnectionString).Options);
    try
    {
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
        {
            errors.Add($"Base de données : schéma non à jour ({pending.Count} migration(s) en attente) ; exécutez « Pos.Cloud migrate ».");
        }

        var privileges = await db.Database.SqlQueryRaw<RolePrivileges>(
            "SELECT r.rolsuper AS \"Superuser\", has_schema_privilege(current_user, 'public', 'CREATE') AS \"CanCreate\" FROM pg_roles r WHERE r.rolname = current_user")
            .SingleAsync();
        if (privileges.Superuser || privileges.CanCreate)
        {
            errors.Add($"{CloudSettings.RuntimeDb} : le rôle applicatif ne doit avoir que des droits sur les données (ni superutilisateur, ni CREATE sur le schéma).");
        }

        // The security log is append-only for the service: GRANT SELECT, INSERT only (doc 12).
        var auditWritable = await db.Database.SqlQueryRaw<bool>(
            "SELECT has_table_privilege(current_user, 'auth_audit_events', 'UPDATE') OR has_table_privilege(current_user, 'auth_audit_events', 'DELETE') AS \"Value\"")
            .SingleAsync();
        if (auditWritable)
        {
            errors.Add($"{CloudSettings.RuntimeDb} : le rôle applicatif ne doit pas pouvoir modifier ni supprimer le journal de sécurité (REVOKE UPDATE, DELETE ON auth_audit_events).");
        }

        // Every application table must be usable by the service (new tables need the deploy-time GRANT again).
        var unusable = await db.Database.SqlQueryRaw<string>(
            "SELECT c.relname AS \"Value\" FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relname <> '__EFMigrationsHistory' " +
            "AND NOT (has_table_privilege(current_user, c.oid, 'SELECT') AND has_table_privilege(current_user, c.oid, 'INSERT')) ORDER BY 1")
            .ToListAsync();
        if (unusable.Count > 0)
        {
            errors.Add($"{CloudSettings.RuntimeDb} : le rôle applicatif ne peut pas lire/écrire {string.Join(", ", unusable)} (GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public, puis REVOKE UPDATE, DELETE ON auth_audit_events).");
        }

        if (pending.Count == 0)
        {
            // MFA seeds: every one must be readable with the configured keys; legacy or old-key rows need mfa-rekey.
            var keys = settings.Auth.Keys;
            var configured = keys.KeyIds.ToList();
            var unreadable = await db.UserMfa.CountAsync(m => m.TotpKeyId != null && !configured.Contains(m.TotpKeyId));
            if (unreadable > 0)
            {
                errors.Add($"{CloudSettings.DataKeys} : {unreadable} secret(s) MFA chiffré(s) avec une clé absente de la configuration (remettez l'ancienne clé, puis « mfa-rekey »).");
            }

            var legacy = await db.UserMfa.CountAsync(m => m.TotpKeyId == null);
            var stale = await db.UserMfa.CountAsync(m => m.TotpKeyId != null && m.TotpKeyId != keys.ActiveKeyId && configured.Contains(m.TotpKeyId));
            if (legacy + stale > 0)
            {
                log.LogWarning("Configuration: {Count} MFA secret(s) not under the active key ({Legacy} unencrypted, {Stale} on an older key): run « Pos.Cloud mfa-rekey »", legacy + stale, legacy, stale);
                Console.WriteLine($"mfa_rekey_needed={legacy + stale}");
            }
        }
    }
    catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
    {
        // The provider's message can name the host and user; the cause class is enough to act on.
        errors.Add($"{CloudSettings.RuntimeDb} : connexion impossible ({ex.GetType().Name}).");
    }

    if (errors.Count > 0)
    {
        Console.Error.WriteLine("Configuration invalide :");
        errors.ForEach(e => Console.Error.WriteLine($"  - {e}"));
        return 2;
    }

    Console.WriteLine("config=ok");
    return 0;
}

/// <summary>Writes the OpenAPI document of the public contract (no database, no listener).</summary>
static async Task<int> WriteOpenApiAsync(Cli options, ILoggerFactory loggerFactory)
{
    var path = Path.GetFullPath(options.Required("out"));
    // A syntactically valid placeholder: the document is generated from endpoint metadata, the database is never opened.
    // Placeholder settings: the document needs a started host, not a database, mail transport or real keys.
    var placeholder = new Pos.Cloud.Identity.AuthSettings("https://openapi.invalid",
        new Pos.Cloud.Identity.MailSettings(Pos.Cloud.Identity.MailSettings.DefaultFrom, Path.GetTempPath(), null),
        Pos.Cloud.Identity.SecretKeyRing.Parse(Pos.Cloud.Identity.SecretKeyRing.NewKeyEntry(DateTimeOffset.UtcNow), "openapi", [])!);
    await using var app = CloudHost.Build("Host=openapi.invalid;Database=none;Username=none", placeholder, loggerFactory, kestrel: null, backgroundMail: false);
    CloudHost.UsePipeline(app, exposeOpenApi: false);
    await app.StartAsync(); // no-op server: nothing listens
    var json = await CloudHost.OpenApiJsonAsync(app);
    await app.StopAsync();
    await File.WriteAllTextAsync(path, json);
    Console.WriteLine($"openapi={path}");
    return 0;
}

internal sealed record RolePrivileges(bool Superuser, bool CanCreate);

internal static class DevCertificate
{
    /// <summary>Self-signed certificate for development and tests only (gateways must pin it explicitly).</summary>
    public static X509Certificate2 Create(IEnumerable<string> hostNames)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PosSaaS Cloud (development)", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var host in hostNames)
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(host);
            }
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
    }
}

internal sealed class CliException(string message) : Exception(message)
{
    public CliException() : this("Argument invalide.") { }

    public CliException(string message, Exception innerException) : this(message) => _ = innerException;
}

internal sealed class Cli
{
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

    public Guid? Id(string name) => One(name) switch
    {
        null => null,
        var v when Guid.TryParseExact(v, "D", out var id) => id,
        _ => throw new CliException($"--{name} doit être un GUID."),
    };

    public string Name()
    {
        var name = Required("name").Trim();
        return name.Length is > 0 and <= 120 && !name.Any(char.IsControl) ? name : throw new CliException("Nom invalide (1 à 120 caractères).");
    }

    public static string ConnectionString(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : throw new CliException($"Variable d'environnement requise : {variable}");

    public static CloudDb Db(string variable = "POS_CLOUD_DB") =>
        new(new DbContextOptionsBuilder<CloudDb>().UseNpgsql(ConnectionString(variable)).Options);

    public static string CertificatePassword() =>
        Environment.GetEnvironmentVariable("POS_CLOUD_CERT_PASSWORD") is { Length: >= 12 } value
            ? value
            : throw new CliException("Variable d'environnement requise : POS_CLOUD_CERT_PASSWORD (12 caractères minimum).");

    public static IPEndPoint ParseListen(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/")
        {
            throw new CliException("--listen doit être une adresse https://<ip>:<port> (HTTP non chiffré interdit).");
        }

        var host = uri.Host.Trim('[', ']');
        var ip = host == "localhost" ? IPAddress.Loopback : IPAddress.TryParse(host, out var parsed) ? parsed
            : throw new CliException("--listen doit utiliser une adresse IP.");
        return new IPEndPoint(ip, uri.Port);
    }
}
