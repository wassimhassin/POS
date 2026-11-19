using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;

namespace Pos.Cloud;

/// <summary>Validated settings for <c>Pos.Cloud run</c>. Owns the loaded certificate.</summary>
internal sealed class CloudRunSettings(IPEndPoint listen, X509Certificate2 certificate, string connectionString, bool openApi) : IDisposable
{
    public IPEndPoint Listen { get; } = listen;

    public X509Certificate2 Certificate { get; } = certificate;

    public string ConnectionString { get; } = connectionString;

    /// <summary>Serve <c>/openapi/v1.json</c> (off unless <c>--openapi</c> is given).</summary>
    public bool OpenApi { get; } = openApi;

    public void Dispose() => Certificate.Dispose();
}

/// <summary>
/// Startup configuration validation. Every problem is collected and reported at once, before anything listens.
/// Messages name the setting at fault, never its value: connection strings and passwords are secrets.
/// </summary>
internal static class CloudSettings
{
    public const string RuntimeDb = "POS_CLOUD_DB";
    public const string MigrationDb = "POS_CLOUD_MIGRATION_DB";
    public const string CertificatePassword = "POS_CLOUD_CERT_PASSWORD";
    public const int MinCertificatePasswordLength = 12;
    private static readonly TimeSpan s_certificateExpiryWarning = TimeSpan.FromDays(30);

    public sealed record Report(CloudRunSettings? Settings, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

    public static Report LoadRun(Cli options, Func<string, string?> environment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var errors = new List<string>();
        var warnings = new List<string>();

        IPEndPoint? listen = null;
        if (options.One("listen") is not { Length: > 0 } listenValue)
        {
            errors.Add("--listen : option requise (https://<ip>:<port>).");
        }
        else
        {
            try
            {
                listen = Cli.ParseListen(listenValue);
            }
            catch (CliException ex)
            {
                errors.Add(ex.Message);
            }
        }

        var connectionString = ValidateConnectionString(environment(RuntimeDb), environment(MigrationDb), errors, warnings);
        var certificate = LoadCertificate(options.One("certificate"), environment(CertificatePassword), now, errors, warnings);

        if (errors.Count > 0 || listen is null || connectionString is null || certificate is null)
        {
            certificate?.Dispose();
            return new Report(null, errors, warnings);
        }

        return new Report(new CloudRunSettings(listen, certificate, connectionString, options.Has("openapi")), errors, warnings);
    }

    private static string? ValidateConnectionString(string? runtime, string? migration, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(runtime))
        {
            errors.Add($"{RuntimeDb} : variable d'environnement requise (chaîne de connexion PostgreSQL du rôle applicatif).");
            return null;
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(runtime);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            // The parser's message may quote the value: do not forward it.
            errors.Add($"{RuntimeDb} : chaîne de connexion illisible.");
            return null;
        }

        foreach (var (key, value) in new[] { ("Host", builder.Host), ("Database", builder.Database), ("Username", builder.Username) })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{RuntimeDb} : « {key} » manquant.");
            }
        }

        if (builder.IncludeErrorDetail)
        {
            warnings.Add($"{RuntimeDb} : « Include Error Detail » est activé ; les journaux peuvent contenir des données.");
        }

        if (!string.IsNullOrWhiteSpace(migration))
        {
            try
            {
                var owner = new NpgsqlConnectionStringBuilder(migration);
                if (!string.IsNullOrEmpty(builder.Username) && string.Equals(owner.Username, builder.Username, StringComparison.Ordinal))
                {
                    errors.Add($"{RuntimeDb} : le service doit utiliser un rôle de données distinct du propriétaire du schéma ({MigrationDb}).");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
            {
                warnings.Add($"{MigrationDb} : chaîne de connexion illisible (ignorée par « run »).");
            }
        }

        return runtime;
    }

    private static X509Certificate2? LoadCertificate(string? path, string? password, DateTimeOffset now, List<string> errors, List<string> warnings)
    {
        var ok = true;
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add("--certificate : option requise (fichier PKCS#12).");
            ok = false;
        }
        else if (!File.Exists(path))
        {
            errors.Add("--certificate : fichier introuvable.");
            ok = false;
        }

        if (password is not { Length: >= MinCertificatePasswordLength })
        {
            errors.Add($"{CertificatePassword} : variable d'environnement requise ({MinCertificatePasswordLength} caractères minimum).");
            ok = false;
        }

        if (!ok)
        {
            return null;
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path!, password);
        }
        catch (CryptographicException)
        {
            errors.Add($"--certificate : impossible d'ouvrir le fichier avec {CertificatePassword} (mot de passe ou format invalide).");
            return null;
        }

        if (!certificate.HasPrivateKey)
        {
            errors.Add("--certificate : le fichier ne contient pas de clé privée.");
        }

        if (now < certificate.NotBefore.ToUniversalTime())
        {
            errors.Add("--certificate : certificat pas encore valide.");
        }
        else if (now > certificate.NotAfter.ToUniversalTime())
        {
            errors.Add("--certificate : certificat expiré.");
        }
        else if (certificate.NotAfter.ToUniversalTime() - now < s_certificateExpiryWarning)
        {
            warnings.Add($"--certificate : expire le {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd} (moins de 30 jours).");
        }

        return certificate;
    }
}
