using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pos.Core.Gateway;

namespace Pos.Gateway;

/// <summary>
/// Self-signed TLS certificate of this gateway (ECDSA P-256), created on first start by the account that
/// runs the gateway. The PKCS#12 file is encrypted with DPAPI (CurrentUser scope of that account), so a
/// copy of the data folder is useless on another account or machine. Terminals do not trust a CA: they
/// pin the SHA-256 fingerprint received out of band at enrollment.
/// </summary>
public sealed class GatewayCertificate : IDisposable
{
    public const string FileName = "gateway-tls.pfx.dpapi";
    private static readonly byte[] Entropy = "PosSaaS.Gateway.Tls.v1"u8.ToArray();

    private GatewayCertificate(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Fingerprint = GatewayProtocol.Fingerprint(certificate);
    }

    public X509Certificate2 Certificate { get; }

    public string Fingerprint { get; }

    public static string FormatFingerprint(string hex) => string.Join(':', hex.Chunk(2).Select(c => new string(c)));

    public static GatewayCertificate LoadOrCreate(string dataDir, IEnumerable<string> hostNames, ILogger logger)
    {
        var path = Path.Combine(dataDir, FileName);
        if (!File.Exists(path))
        {
            var created = Create(hostNames);
            var pfx = created.Export(X509ContentType.Pkcs12);
            File.WriteAllBytes(path, ProtectedData.Protect(pfx, Entropy, DataProtectionScope.CurrentUser));
            CryptographicOperations.ZeroMemory(pfx);
            created.Dispose();
            logger.LogInformation("Created gateway TLS certificate {CertificateFile}", path);
        }

        byte[] decrypted;
        try
        {
            decrypted = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"Impossible de déchiffrer {path} : il a été créé par un autre compte Windows ou une autre machine. " +
                "Restaurez le fichier d'origine ou supprimez-le (les terminaux devront alors être ré-appairés avec la nouvelle empreinte).", ex);
        }

        try
        {
            return new GatewayCertificate(X509CertificateLoader.LoadPkcs12(decrypted, password: null));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    private static X509Certificate2 Create(IEnumerable<string> hostNames)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=PosSaaS Branch Gateway {Environment.MachineName}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
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
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(5));
    }

    public void Dispose() => Certificate.Dispose();
}
