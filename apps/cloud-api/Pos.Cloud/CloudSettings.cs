using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
using Pos.Cloud.Identity;

namespace Pos.Cloud;

/// <summary>Validated settings for <c>Pos.Cloud run</c>. Owns the loaded certificate.</summary>
internal sealed class CloudRunSettings(IPEndPoint listen, X509Certificate2 certificate, string connectionString, AuthSettings auth, bool openApi) : IDisposable
{
    public AuthSettings Auth { get; } = auth;

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

    /// <summary>Platform operations (provisioning CLI): a role that bypasses row-level security. Defaults to <see cref="MigrationDb"/>.</summary>
    public const string AdminDb = "POS_CLOUD_ADMIN_DB";
    public const string CertificatePassword = "POS_CLOUD_CERT_PASSWORD";
    public const string PublicOrigin = "POS_CLOUD_PUBLIC_ORIGIN";
    public const string MailDirectory = "POS_CLOUD_MAIL_DIR";
    public const string MailFrom = "POS_CLOUD_MAIL_FROM";
    public const string SmtpHost = "POS_CLOUD_SMTP_HOST";
    public const string SmtpPort = "POS_CLOUD_SMTP_PORT";
    public const string SmtpTls = "POS_CLOUD_SMTP_TLS";
    public const string SmtpUser = "POS_CLOUD_SMTP_USER";
    public const string SmtpPassword = "POS_CLOUD_SMTP_PASSWORD";
    public const string DataKeys = "POS_CLOUD_DATA_KEYS";
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

        var connectionString = ValidateConnectionString(environment(RuntimeDb), [(MigrationDb, environment(MigrationDb)), (AdminDb, environment(AdminDb))], errors, warnings);
        var certificate = LoadCertificate(options.One("certificate"), environment(CertificatePassword), now, errors, warnings);
        var auth = LoadAuth(environment, errors);

        if (errors.Count > 0 || listen is null || connectionString is null || certificate is null || auth is null)
        {
            certificate?.Dispose();
            return new Report(null, errors, warnings);
        }

        return new Report(new CloudRunSettings(listen, certificate, connectionString, auth, options.Has("openapi")), errors, warnings);
    }

    /// <summary>
    /// User authentication: the public origin of the dashboard (links in emails, CSRF origin check), the mail
    /// transport and the data-protection keys. All are required: a misconfigured host must not run with
    /// half-working account recovery or unreadable MFA secrets.
    /// </summary>
    public static AuthSettings? LoadAuth(Func<string, string?> environment, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(errors);
        string? origin = null;
        var originValue = environment(PublicOrigin);
        if (string.IsNullOrWhiteSpace(originValue))
        {
            errors.Add($"{PublicOrigin} : variable d'environnement requise (origine HTTPS du tableau de bord, ex. https://app.exemple.tn).");
        }
        else if (!Uri.TryCreate(originValue, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || originValue.EndsWith('/'))
        {
            errors.Add($"{PublicOrigin} : doit être une origine https://hôte[:port], sans chemin ni barre finale.");
        }
        else
        {
            origin = uri.GetLeftPart(UriPartial.Authority);
        }

        var mail = LoadMail(environment, errors);
        var keys = SecretKeyRing.Parse(environment(DataKeys), DataKeys, errors);
        return origin is null || mail is null || keys is null ? null : new AuthSettings(origin, mail, keys);
    }

    /// <summary>
    /// Exactly one mail transport: <c>POS_CLOUD_MAIL_DIR</c> (pickup directory: development, or a local relay agent)
    /// or <c>POS_CLOUD_SMTP_HOST</c> (SMTP relay, STARTTLS by default). Credentials are checked for presence and
    /// shape only, and never echoed.
    /// </summary>
    private static MailSettings? LoadMail(Func<string, string?> environment, List<string> errors)
    {
        var directory = environment(MailDirectory);
        var host = environment(SmtpHost);
        var hasDirectory = !string.IsNullOrWhiteSpace(directory);
        var hasHost = !string.IsNullOrWhiteSpace(host);
        if (hasDirectory == hasHost)
        {
            errors.Add(hasDirectory
                ? $"{MailDirectory} et {SmtpHost} : choisissez un seul transport (dossier de dépôt OU relais SMTP)."
                : $"{MailDirectory} ou {SmtpHost} : un transport d'e-mails de sécurité est requis (dossier de dépôt ou relais SMTP).");
            return null;
        }

        var before = errors.Count;
        var fromValue = environment(MailFrom);
        string from = MailSettings.DefaultFrom;
        if (!string.IsNullOrWhiteSpace(fromValue))
        {
            if (fromValue.Any(char.IsControl) || !TryMailAddress(fromValue))
            {
                errors.Add($"{MailFrom} : adresse d'expéditeur invalide.");
            }
            else
            {
                from = fromValue;
            }
        }
        else if (hasHost)
        {
            errors.Add($"{MailFrom} : expéditeur requis avec un relais SMTP (adresse d'un domaine que le relais est autorisé à signer).");
        }

        if (hasDirectory)
        {
            if (!Path.IsPathFullyQualified(directory!) || !Directory.Exists(directory))
            {
                errors.Add($"{MailDirectory} : dossier absolu existant requis.");
                return null;
            }

            var probe = Path.Combine(directory!, $".write-test-{Guid.NewGuid():N}");
            try
            {
                File.WriteAllBytes(probe, []);
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{MailDirectory} : dossier non accessible en écriture.");
            }

            return errors.Count > before ? null : new MailSettings(from, directory, null);
        }

        host = host!.Trim();
        var loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            errors.Add($"{SmtpHost} : nom d'hôte ou adresse IP invalide.");
        }

        var port = 587;
        var portValue = environment(SmtpPort);
        if (!string.IsNullOrWhiteSpace(portValue) && (!int.TryParse(portValue, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            errors.Add($"{SmtpPort} : port entre 1 et 65535 attendu.");
        }
        else if (port == 465)
        {
            errors.Add($"{SmtpPort} : 465 (TLS implicite) n'est pas pris en charge ; utilisez la soumission STARTTLS (587).");
        }

        var tls = environment(SmtpTls)?.Trim().ToLowerInvariant() ?? "starttls";
        if (tls is not ("starttls" or "none"))
        {
            errors.Add($"{SmtpTls} : « starttls » (défaut) ou « none » attendu.");
        }
        else if (tls == "none" && !loopback)
        {
            errors.Add($"{SmtpTls} : « none » n'est autorisé que vers un relais local (localhost / 127.0.0.1) ; les liens à usage unique ne doivent pas circuler en clair.");
        }

        var user = environment(SmtpUser);
        var password = environment(SmtpPassword);
        if (string.IsNullOrEmpty(user) != string.IsNullOrEmpty(password))
        {
            errors.Add($"{SmtpUser} et {SmtpPassword} : à définir ensemble (ou aucun des deux).");
        }
        else if (!string.IsNullOrEmpty(user) && (user.Any(char.IsControl) || password!.Any(char.IsControl)))
        {
            errors.Add($"{SmtpUser} / {SmtpPassword} : caractères de contrôle interdits.");
        }

        return errors.Count > before ? null
            : new MailSettings(from, null, new SmtpSettings(host, port, tls == "starttls", string.IsNullOrEmpty(user) ? null : user, string.IsNullOrEmpty(user) ? null : password));
    }

    private static bool TryMailAddress(string value)
    {
        try
        {
            _ = new System.Net.Mail.MailAddress(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? ValidateConnectionString(string? runtime, (string Name, string? Value)[] privileged, List<string> errors, List<string> warnings)
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

        // The service never runs as a role that owns the schema or bypasses row-level security (doc 15).
        foreach (var (name, value) in privileged.Where(p => !string.IsNullOrWhiteSpace(p.Value)))
        {
            try
            {
                var other = new NpgsqlConnectionStringBuilder(value);
                if (!string.IsNullOrEmpty(builder.Username) && string.Equals(other.Username, builder.Username, StringComparison.Ordinal))
                {
                    errors.Add($"{RuntimeDb} : le service doit utiliser un rôle de données distinct du rôle privilégié ({name}).");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
            {
                warnings.Add($"{name} : chaîne de connexion illisible (ignorée par « run »).");
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
