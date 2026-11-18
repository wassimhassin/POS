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
//   Pos.Cloud report-key      --org <guid> [--business <guid> [--branch <guid>]] --name <name>
//   Pos.Cloud dev-cert        --out <file.pfx> [--host <name-or-ip>]...  (POS_CLOUD_CERT_PASSWORD)
//   Pos.Cloud run             --listen https://<ip>:<port> --certificate <file.pfx>
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
    var listen = Cli.ParseListen(options.Required("listen"));
    using var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.Required("certificate"), Cli.CertificatePassword());
    var connectionString = Cli.ConnectionString("POS_CLOUD_DB");

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.Configuration.Sources.Clear(); // explicit configuration only: nothing can add a plain-HTTP listener
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton(loggerFactory);
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        k.Limits.MaxRequestBodySize = SyncProtocol.MaxBodyBytes;
        k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
        k.Listen(listen, o => o.UseHttps(certificate));
    });

    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        var strict = SyncProtocol.Json;
        o.SerializerOptions.UnmappedMemberHandling = strict.UnmappedMemberHandling;
        o.SerializerOptions.RespectNullableAnnotations = strict.RespectNullableAnnotations;
        o.SerializerOptions.RespectRequiredConstructorParameters = strict.RespectRequiredConstructorParameters;
        o.SerializerOptions.NumberHandling = strict.NumberHandling;
        o.SerializerOptions.MaxDepth = strict.MaxDepth;
    });
    builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddDbContext<CloudDb>(o => o.UseNpgsql(connectionString));
    builder.Services.AddScoped<SyncService>();
    builder.Services.AddScoped<GatewayRegistration>();

    builder.Services.AddAuthentication()
        .AddScheme<AuthenticationSchemeOptions, GatewaySignatureHandler>(SyncProtocol.GatewayScheme, null)
        .AddScheme<AuthenticationSchemeOptions, ReportKeyHandler>(SyncProtocol.ReportScheme, null);
    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(CloudApi.GatewayPolicy, p => p.AddAuthenticationSchemes(SyncProtocol.GatewayScheme).RequireAuthenticatedUser())
        .AddPolicy(CloudApi.ReportPolicy, p => p.AddAuthenticationSchemes(SyncProtocol.ReportScheme).RequireAuthenticatedUser())
        // Deny by default: an endpoint without an explicit policy or AllowAnonymous requires authentication.
        .SetFallbackPolicy(new AuthorizationPolicyBuilder(SyncProtocol.GatewayScheme, SyncProtocol.ReportScheme).RequireAuthenticatedUser().Build());
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = 429;
        o.AddPolicy(CloudApi.RegisterRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    });

    var app = builder.Build();

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

    app.Use(CloudApi.HandleErrors);
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
    CloudApi.Map(app);

    app.Lifetime.ApplicationStarted.Register(() => log.LogInformation("Cloud API listening on https://{ListenEndpoint}", listen));
    await app.RunAsync();
    return 0;
}

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
