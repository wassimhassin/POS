using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Pos.Sync;

namespace Pos.Gateway;

/// <summary>
/// Gateway → cloud HTTPS client shared by the sync worker and the « reconcile » command: pinned (or CA-validated) TLS,
/// signed requests with the gateway's DPAPI-protected key, one-time-code registration.
/// </summary>
public sealed class CloudClient(GatewayStore store, TimeSpan timeout) : IDisposable
{
    private HttpClient? _http;
    private string? _httpFor;
    private ECDsa? _key;
    private string? _keyFor;

    /// <summary>Registers with the pending one-time code (proof of key possession) and stores the credential.</summary>
    public async Task RegisterAsync(CloudLink link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        var identity = store.GetIdentity() ?? throw new SyncFailureException("Passerelle non initialisée.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var signature = key.SignData(SyncProtocol.RegisterPayload(link.PendingCode!, identity.BranchId, publicKey), HashAlgorithmName.SHA256);
        var request = new RegisterGatewayRequest(link.PendingCode!, identity.BranchId, Convert.ToBase64String(publicKey), Convert.ToBase64String(signature));

        using var response = await Http(link).PostAsJsonAsync("v1/gateways/register", request, SyncProtocol.Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new SyncFailureException($"Enregistrement auprès du cloud refusé ({(int)response.StatusCode} {await ReadErrorAsync(response, ct)}). " +
                "Générez un nouveau code avec « Pos.Cloud gateway-code » puis « Pos.Gateway cloud-link ».");
        }

        var registration = await response.Content.ReadFromJsonAsync<RegisterGatewayResponse>(SyncProtocol.Json, ct)
            ?? throw new SyncFailureException("Réponse d'enregistrement vide.");
        if (registration.BranchId != identity.BranchId)
        {
            throw new SyncFailureException($"Le cloud a associé cette passerelle à la succursale {registration.BranchId}, attendu {identity.BranchId}.");
        }

        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            store.CompleteCloudLink(registration, pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    public async Task<HttpResponseMessage> SendSignedAsync(CloudLink link, HttpMethod method, string pathAndQuery, byte[]? body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (_key is null || _keyFor != link.GatewayId)
        {
            _key?.Dispose();
            _key = GatewayStore.LoadCloudKey(link);
            _keyFor = link.GatewayId;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = _key.SignData(SyncProtocol.RequestPayload(link.GatewayId!, method.Method, pathAndQuery, timestamp, SyncProtocol.Sha256Hex(body ?? [])),
            HashAlgorithmName.SHA256);
        using var request = new HttpRequestMessage(method, pathAndQuery.TrimStart('/'));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        }

        request.Headers.Authorization = new AuthenticationHeaderValue(SyncProtocol.GatewayScheme, SyncProtocol.AuthorizationParameter(link.GatewayId!, timestamp, signature));
        return await Http(link).SendAsync(request, ct);
    }

    /// <summary>Signed GET; a refused credential is reported distinctly (revoked = this gateway must stop serving).</summary>
    public async Task<T> GetAsync<T>(CloudLink link, string pathAndQuery, CancellationToken ct)
    {
        using var response = await SendSignedAsync(link, HttpMethod.Get, pathAndQuery, null, ct);
        await ThrowIfRefusedAsync(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new SyncFailureException($"Le cloud a répondu {(int)response.StatusCode} ({await ReadErrorAsync(response, ct)}).");
        }

        return await response.Content.ReadFromJsonAsync<T>(SyncProtocol.Json, ct) ?? throw new SyncFailureException("Réponse du cloud vide.");
    }

    /// <summary>401/403: the cloud refuses this gateway's credential. Revocation is signalled with <see cref="CloudRevokedException"/>.</summary>
    public static async Task ThrowIfRefusedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return;
        }

        var error = await ReadErrorAsync(response, ct);
        if (error.StartsWith(CloudErrorCodes.GatewayRevoked, StringComparison.Ordinal))
        {
            throw new CloudRevokedException();
        }

        throw new SyncFailureException("Le cloud refuse les identifiants de cette passerelle (révoquée ou inconnue). Les événements restent en attente.");
    }

    /// <summary>HTTPS only. With a pin (development/test certificates) the cloud certificate must match it exactly; otherwise normal CA validation.</summary>
    public HttpClient Http(CloudLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var key = $"{link.CloudUrl}|{link.CloudPin}";
        if (_http is not null && _httpFor == key)
        {
            return _http;
        }

        _http?.Dispose();
        var pin = link.CloudPin is null ? null : Convert.FromHexString(link.CloudPin);
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = pin is null
                    ? null
                    : (_, certificate, _, _) => certificate is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), pin),
            },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(link.CloudUrl), Timeout = timeout };
        _httpFor = key;
        return _http;
    }

    public static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            var error = await response.Content.ReadFromJsonAsync<CloudError>(SyncProtocol.Json, ct);
            return error is null ? "?" : $"{error.Code}: {error.Message}";
        }
        catch (JsonException)
        {
            return "réponse illisible";
        }
    }

    public void Dispose()
    {
        _http?.Dispose();
        _key?.Dispose();
    }
}

/// <summary>The cloud says this gateway's credential was revoked (typically: replaced by another gateway).</summary>
public sealed class CloudRevokedException : Exception
{
    public CloudRevokedException() : base("Le cloud a révoqué cette passerelle (remplacée par une autre ?). Elle cesse de servir les modifications partagées.") { }

    public CloudRevokedException(string message) : base(message) { }

    public CloudRevokedException(string message, Exception innerException) : base(message, innerException) { }
}
