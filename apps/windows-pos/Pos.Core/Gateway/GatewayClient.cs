using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Pos.Core.Gateway;

public static class GatewayState
{
    public const string NotConfigured = "not_configured";
    public const string Connecting = "connecting";
    public const string Connected = "connected";
    public const string Offline = "offline";
}

public sealed record GatewayStatus(
    string State,
    string TerminalId,
    string? TerminalName,
    string? BranchName,
    string? GatewayUrl,
    string? LastError,
    string Since,
    int PendingOperations);

/// <summary>Operator-facing message, e.g. the outcome of an operation interrupted by a disconnection.</summary>
public sealed record GatewayNotice(long Id, string Tone, string Message, string At);

public sealed record GatewaySnapshot(GatewayStatus Status, IReadOnlyList<SharedOrder> Orders, IReadOnlyList<GatewayNotice> Notices);

/// <summary>
/// A terminal's connection to the branch gateway.
///  - TLS with certificate pinning (SHA-256 received at enrollment); no CA, no plain HTTP.
///  - Challenge/response login with the terminal's private key; automatic re-login after a gateway restart.
///  - SignalR only notifies: after every (re)connection the full snapshot is reloaded, and an update is
///    applied only if its version is newer than the cached one.
///  - Fail closed: shared-order mutations are refused while the gateway is not connected (no offline queue).
///  - A mutation whose outcome is unknown (timeout, crash) stays in a durable pending list and is resolved
///    on the next connection: the gateway either returns its result or burns its ID — never applied twice.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class GatewayClient : IAsyncDisposable
{
    private const int MaxNotices = 20;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] ReconnectDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly TerminalStore _store;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _linkChanged = new(0, 1);
    private readonly CancellationTokenSource _disposed = new();

    private Dictionary<string, SharedOrder> _orders = new(StringComparer.Ordinal);
    private readonly List<GatewayNotice> _notices = [];
    private long _nextNoticeId = 1;
    private GatewayStatus _status;
    private HttpClient? _http;
    private GatewayLink? _link;
    private string? _token;
    private CancellationTokenSource? _session; // the current connection; cancelled by an unpair

    public GatewayClient(TerminalStore store, ILogger<GatewayClient> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _logger = logger;
        _link = store.GetLink();
        _status = NewStatus(_link is null ? GatewayState.NotConfigured : GatewayState.Connecting, null);
    }

    /// <summary>Raised (on a background thread) whenever status, orders or notices change.</summary>
    public event Action? Changed;

    public GatewaySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new GatewaySnapshot(
                _status with { PendingOperations = _store.ListPending().Count },
                [.. _orders.Values.OrderByDescending(o => o.Status == SharedOrderStatus.Open).ThenByDescending(o => o.Number)],
                [.. _notices]);
        }
    }

    public GatewayStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public SharedOrder? Order(string orderId)
    {
        lock (_gate)
        {
            return _orders.GetValueOrDefault(orderId);
        }
    }

    // ------------------------------------------------------------------ enrollment

    public async Task<EnrollResponse> EnrollAsync(string gatewayUrl, string fingerprint, string code, CancellationToken cancellationToken = default)
    {
        if (_store.GetLink() is not null)
        {
            throw new PosValidationException("already_enrolled", "Ce terminal est déjà appairé à une passerelle.");
        }

        var baseUri = ParseGatewayUrl(gatewayUrl);
        var pin = GatewayProtocol.NormalizeFingerprint(fingerprint)
            ?? throw new PosValidationException("invalid_fingerprint", "Empreinte invalide : 64 caractères hexadécimaux attendus.");
        code = GatewayProtocol.NormalizeCode(code);
        if (code.Length is < 10 or > 40)
        {
            throw new PosValidationException("invalid_code", "Code d'appairage invalide.");
        }

        var (terminalId, publicKey) = _store.GetOrCreateIdentity();
        var request = new EnrollRequest(code, terminalId, Convert.ToBase64String(publicKey),
            Convert.ToBase64String(_store.Sign(GatewayProtocol.EnrollPayload(terminalId, code, pin))));

        using var http = CreateHttpClient(baseUri, pin);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("v1/enroll", request, GatewayProtocol.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new PosValidationException("certificate_mismatch",
                "Le certificat présenté ne correspond pas à l'empreinte saisie. Vérifiez l'empreinte : ce n'est peut-être pas votre passerelle.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new PosValidationException("gateway_unavailable", $"Passerelle injoignable à {baseUri}: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadError(response).ConfigureAwait(false);
                throw new PosValidationException(error.Code, error.Code == "unauthorized"
                    ? "Code d'appairage invalide, expiré ou déjà utilisé."
                    : error.Message);
            }

            var enrolled = (await response.Content.ReadFromJsonAsync<EnrollResponse>(GatewayProtocol.Json, cancellationToken).ConfigureAwait(false))!;
            var link = new GatewayLink(baseUri.ToString(), pin, enrolled.BranchId, enrolled.BranchName, enrolled.TerminalName,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            _store.SaveLink(link);
            lock (_gate)
            {
                _link = link;
                _status = NewStatus(GatewayState.Connecting, null);
            }

            LogEnrolled(_logger, terminalId, enrolled.TerminalName, enrolled.BranchName, baseUri);
            _linkChanged.Release();
            RaiseChanged();
            return enrolled;
        }
    }

    /// <summary>
    /// Unpairs this till (replacement, reassignment): forgets the gateway link AND the terminal identity, so a later
    /// pairing registers a NEW identity and key (the old one should be revoked on the gateway). Shared orders live on
    /// the gateway and are untouched; local settings stay. Operations still awaiting the gateway's answer are not
    /// dropped silently: unpairing is refused unless the operator explicitly confirms abandoning them (logged).
    /// </summary>
    public void Unpair(bool discardPending)
    {
        var pending = _store.ListPending();
        if (pending.Count > 0 && !discardPending)
        {
            throw new PosValidationException("pending_operations",
                $"{pending.Count} opération(s) partagée(s) attendent encore la réponse de la passerelle. Reconnectez ce terminal pour les résoudre, " +
                "ou confirmez explicitement leur abandon (elles ont peut-être été appliquées par la passerelle).");
        }

        foreach (var p in pending)
        {
            LogPendingAbandoned(_logger, p.OperationId, p.Type, p.Description);
        }

        var previous = _store.Unpair();
        CancellationTokenSource? session;
        lock (_gate)
        {
            _link = null;
            _token = null;
            _orders = new(StringComparer.Ordinal);
            _status = NewStatus(GatewayState.NotConfigured, null);
            session = _session;
        }

        try
        {
            session?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        LogUnpaired(_logger, previous ?? "?", pending.Count);
        RaiseChanged();
    }

    // ------------------------------------------------------------------ connection loop

    /// <summary>Keeps the terminal connected for its whole lifetime (never gives up, never switches gateway).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        var ct = linked.Token;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            GatewayLink? link;
            lock (_gate)
            {
                link = _link;
            }

            if (link is null)
            {
                await _linkChanged.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            SetStatus(failures == 0 ? GatewayState.Connecting : GatewayState.Offline, failures == 0 ? null : _status.LastError);
            HubConnection? hub = null;
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate)
            {
                _session = session;
            }

            try
            {
                _http ??= CreateHttpClient(new Uri(link.GatewayUrl), link.CertificateSha256);
                await AuthenticateAsync(link, session.Token).ConfigureAwait(false);

                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                hub = BuildHub(link);
                hub.On<OrderChangedEvent>(GatewayProtocol.OrderChangedMethod, e => Apply(e.Order));
                hub.Closed += ex =>
                {
                    closed.TrySetResult(ex);
                    return Task.CompletedTask;
                };
                await hub.StartAsync(session.Token).ConfigureAwait(false);

                // Subscribed first, then snapshot: no update can fall in between.
                await ResolvePendingAsync(link, session.Token).ConfigureAwait(false);
                await RefreshAsync(link, session.Token).ConfigureAwait(false);
                failures = 0;
                SetStatus(GatewayState.Connected, null);
                LogConnected(_logger, link.GatewayUrl);

                var reason = await closed.Task.WaitAsync(session.Token).ConfigureAwait(false);
                LogDisconnected(_logger, reason?.Message ?? "closed by gateway");
                failures = 1; // stay "offline" (not "connecting") until a reconnection actually succeeds
                SetStatus(GatewayState.Offline, "Connexion à la passerelle perdue.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                // Unpaired: drop this connection; the loop waits for a new pairing.
                _http?.Dispose();
                _http = null;
                failures = 0;
                continue;
            }
#pragma warning disable CA1031 // The loop must survive any network/protocol failure and keep retrying.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures++;
                LogConnectFailed(_logger, failures, Describe(ex));
                SetStatus(GatewayState.Offline, Describe(ex));
            }
            finally
            {
                if (hub is not null)
                {
                    await hub.DisposeAsync().ConfigureAwait(false);
                }
            }

            try
            {
                await Task.Delay(ReconnectDelays[Math.Min(failures, ReconnectDelays.Length - 1)], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ------------------------------------------------------------------ shared-order commands

    public Task<OperationResult> CreateOrderAsync(string operationId, string? label, CancellationToken ct = default) =>
        MutateAsync(operationId, "create_order", "Nouvelle commande", "orders", new CreateOrderRequest(operationId, label), ct);

    public Task<OperationResult> AddItemAsync(string operationId, string orderId, string productId, int quantity, CancellationToken ct = default) =>
        MutateAsync(operationId, "add_item", $"Ajout de {quantity} × {productId}", $"orders/{RequireId(orderId)}/items",
            new AddItemRequest(operationId, productId, quantity), ct);

    public Task<OperationResult> SetQuantityAsync(string operationId, string orderId, string productId, int quantity, long expectedVersion, CancellationToken ct = default) =>
        MutateAsync(operationId, "set_quantity", $"Quantité {productId} → {quantity}", $"orders/{RequireId(orderId)}/quantity",
            new SetQuantityRequest(operationId, productId, quantity, expectedVersion), ct);

    public Task<OperationResult> SubmitAsync(string operationId, string orderId, long expectedVersion, CancellationToken ct = default) =>
        MutateAsync(operationId, "submit", "Envoi en préparation", $"orders/{RequireId(orderId)}/submit",
            new SubmitRequest(operationId, expectedVersion), ct);

    public async Task<IReadOnlyList<PrintIntent>> PrintIntentsAsync(string orderId, CancellationToken ct = default)
    {
        var link = RequireConnected();
        using var response = await SendAsync(link, HttpMethod.Get, $"orders/{RequireId(orderId)}/print-intents", null, ct).ConfigureAwait(false);
        await EnsureSuccess(response).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<List<PrintIntent>>(GatewayProtocol.Json, ct).ConfigureAwait(false))!;
    }

    private async Task<OperationResult> MutateAsync(string operationId, string type, string description, string path, object body, CancellationToken ct)
    {
        if (!GatewayProtocol.IsId(operationId))
        {
            throw new PosValidationException("invalid_operation_id", "Identifiant d'opération invalide.");
        }

        var link = RequireConnected();
        _store.AddPending(operationId, type, description); // durable BEFORE sending: survives a POS crash

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await SendAsync(link, HttpMethod.Post, path, body, ct).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500)
                {
                    throw new HttpRequestException($"Gateway error {(int)response.StatusCode}");
                }

                if (!response.IsSuccessStatusCode)
                {
                    // Definitive rejection: nothing was applied.
                    _store.RemovePending(operationId);
                    var error = await ReadError(response).ConfigureAwait(false);
                    if (error.Current is not null)
                    {
                        Apply(error.Current);
                    }

                    throw new PosValidationException(error.Code, error.Message);
                }

                var result = (await response.Content.ReadFromJsonAsync<OperationResult>(GatewayProtocol.Json, ct).ConfigureAwait(false))!;
                _store.RemovePending(operationId);
                Apply(result.Order);
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Outcome unknown. Retrying with the SAME operation ID is safe (idempotent on the gateway).
                LogUncertain(_logger, type, operationId, attempt, ex.Message);
                if (attempt >= 3)
                {
                    RaiseChanged();
                    throw new PosValidationException("gateway_unavailable",
                        "La passerelle ne répond pas : résultat inconnu. Il sera vérifié automatiquement à la reconnexion (jamais appliqué deux fois).");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    // ------------------------------------------------------------------ internals

    private async Task ResolvePendingAsync(GatewayLink link, CancellationToken ct)
    {
        foreach (var pending in _store.ListPending())
        {
            using var response = await SendAsync(link, HttpMethod.Post, $"operations/{pending.OperationId}/resolve", null, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _store.RemovePending(pending.OperationId);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Locked)
            {
                // The gateway is recovering: the outcome cannot be decided yet. Kept pending (never guessed).
                AddNotice("warning", $"Opération interrompue « {pending.Description} » : en attente de la fin de la récupération de la passerelle.");
                continue;
            }

            await EnsureSuccess(response).ConfigureAwait(false);
            var resolution = (await response.Content.ReadFromJsonAsync<OperationResolution>(GatewayProtocol.Json, ct).ConfigureAwait(false))!;
            _store.RemovePending(pending.OperationId);
            if (resolution.Status == OperationStatus.Applied)
            {
                Apply(resolution.Result!.Order);
                AddNotice("ok", $"Opération interrompue « {pending.Description} » : appliquée une seule fois par la passerelle.");
            }
            else
            {
                AddNotice("warning", $"Opération interrompue « {pending.Description} » : NON appliquée. Recommencez-la si nécessaire.");
            }

            LogResolved(_logger, pending.OperationId, pending.Type, resolution.Status);
        }
    }

    private async Task RefreshAsync(GatewayLink link, CancellationToken ct)
    {
        using var response = await SendAsync(link, HttpMethod.Get, "orders", null, ct).ConfigureAwait(false);
        await EnsureSuccess(response).ConfigureAwait(false);
        var snapshot = (await response.Content.ReadFromJsonAsync<OrdersSnapshot>(GatewayProtocol.Json, ct).ConfigureAwait(false))!;
        lock (_gate)
        {
            var merged = snapshot.Orders.ToDictionary(o => o.Id, StringComparer.Ordinal);
            foreach (var (id, cached) in _orders)
            {
                if (merged.TryGetValue(id, out var fresh) && cached.Version > fresh.Version)
                {
                    merged[id] = cached; // a newer notification arrived while the snapshot was in flight
                }
            }

            _orders = merged;
        }

        RaiseChanged();
    }

    private void Apply(SharedOrder order)
    {
        lock (_gate)
        {
            if (_orders.TryGetValue(order.Id, out var cached) && cached.Version >= order.Version)
            {
                return; // stale or duplicate notification
            }

            _orders[order.Id] = order;
        }

        RaiseChanged();
    }

    private async Task AuthenticateAsync(GatewayLink link, CancellationToken ct)
    {
        var terminalId = _store.TerminalId;
        using var challengeResponse = await _http!.PostAsJsonAsync("v1/auth/challenge", new ChallengeRequest(terminalId), GatewayProtocol.Json, ct).ConfigureAwait(false);
        await EnsureSuccess(challengeResponse).ConfigureAwait(false);
        var challenge = (await challengeResponse.Content.ReadFromJsonAsync<ChallengeResponse>(GatewayProtocol.Json, ct).ConfigureAwait(false))!;

        var signature = _store.Sign(GatewayProtocol.AuthPayload(link.BranchId, terminalId, challenge.Nonce, link.CertificateSha256));
        using var tokenResponse = await _http!.PostAsJsonAsync("v1/auth/token",
            new TokenRequest(terminalId, challenge.Nonce, Convert.ToBase64String(signature)), GatewayProtocol.Json, ct).ConfigureAwait(false);
        await EnsureSuccess(tokenResponse).ConfigureAwait(false);
        var token = (await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(GatewayProtocol.Json, ct).ConfigureAwait(false))!;
        if (token.BranchId != link.BranchId)
        {
            throw new PosValidationException("branch_mismatch", "La passerelle appartient à une autre succursale.");
        }

        lock (_gate)
        {
            _token = token.Token;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(GatewayLink link, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, $"v1/branches/{link.BranchId}/{path}");
            string? token;
            lock (_gate)
            {
                token = _token;
            }

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: GatewayProtocol.Json);
            }

            var response = await _http!.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
            {
                return response;
            }

            // Token expired or gateway restarted (tokens live in memory): log in again, retry once.
            response.Dispose();
            await AuthenticateAsync(link, ct).ConfigureAwait(false);
        }
    }

    private HubConnection BuildHub(GatewayLink link) => new HubConnectionBuilder()
        .WithUrl(new Uri(new Uri(link.GatewayUrl), GatewayProtocol.HubPath.TrimStart('/')), o =>
        {
            o.Transports = HttpTransportType.WebSockets;
            o.SkipNegotiation = true;
            o.AccessTokenProvider = () =>
            {
                lock (_gate)
                {
                    return Task.FromResult(_token);
                }
            };
            o.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = PinnedValidator(link.CertificateSha256);
        })
        .WithServerTimeout(TimeSpan.FromSeconds(15))
        .WithKeepAliveInterval(TimeSpan.FromSeconds(5))
        .Build();

    private static HttpClient CreateHttpClient(Uri baseUri, string pinnedSha256) => new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = PinnedValidator(pinnedSha256) },
    })
    {
        BaseAddress = baseUri,
        Timeout = RequestTimeout,
    };

    /// <summary>Accepts exactly the enrolled gateway certificate (self-signed, so chain and name are not used).</summary>
    private static RemoteCertificateValidationCallback PinnedValidator(string pinnedSha256) =>
        (_, certificate, _, _) => certificate is not null
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(pinnedSha256));

    private GatewayLink RequireConnected()
    {
        lock (_gate)
        {
            if (_link is null)
            {
                throw new PosValidationException("gateway_not_configured", "Ce terminal n'est pas appairé à une passerelle.");
            }

            return _status.State == GatewayState.Connected
                ? _link
                : throw new PosValidationException("gateway_unavailable",
                    "Passerelle déconnectée : les commandes partagées sont bloquées pour éviter toute divergence entre caisses.");
        }
    }

    private static Uri ParseGatewayUrl(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new PosValidationException("invalid_url", "Adresse invalide : https://<adresse>:<port> attendu.");
        }

        return uri;
    }

    private static string RequireId(string id) =>
        GatewayProtocol.IsId(id) ? id : throw new PosValidationException("order_not_found", "Commande introuvable.");

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadError(response).ConfigureAwait(false);
            throw new PosValidationException(error.Code, error.Message);
        }
    }

    private static async Task<GatewayError> ReadError(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<GatewayError>(GatewayProtocol.Json).ConfigureAwait(false)
                ?? new GatewayError("gateway_error", $"Erreur passerelle ({(int)response.StatusCode}).");
        }
        catch (JsonException)
        {
            return new GatewayError("gateway_error", $"Erreur passerelle ({(int)response.StatusCode}).");
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: AuthenticationException } =>
            "Certificat de la passerelle différent de celui appairé : connexion refusée (passerelle remplacée ou usurpation ?).",
        PosValidationException { Code: "unauthorized" } => "Terminal refusé par la passerelle (révoqué ou inconnu).",
        PosValidationException p => p.Message,
        HttpRequestException or TaskCanceledException or TimeoutException or IOException => "Passerelle injoignable.",
        _ => $"Erreur de connexion : {ex.Message}",
    };

    private void SetStatus(string state, string? error)
    {
        lock (_gate)
        {
            if (_status.State == state && _status.LastError == error)
            {
                return;
            }

            _status = NewStatus(state, error);
        }

        RaiseChanged();
    }

    private GatewayStatus NewStatus(string state, string? error) => new(
        state, _store.TerminalId, _link?.TerminalName, _link?.BranchName, _link?.GatewayUrl, error,
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), 0);

    private void AddNotice(string tone, string message)
    {
        lock (_gate)
        {
            _notices.Add(new GatewayNotice(_nextNoticeId++, tone, message, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            if (_notices.Count > MaxNotices)
            {
                _notices.RemoveAt(0);
            }
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
#pragma warning disable CA1031 // A UI listener failure must not break the connection loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogListenerFailed(_logger, ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposed.CancelAsync().ConfigureAwait(false);
        _http?.Dispose();
        _linkChanged.Dispose();
        _disposed.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Terminal {TerminalId} unpaired; {Abandoned} pending operations abandoned by the operator")]
    private static partial void LogUnpaired(ILogger logger, string terminalId, int abandoned);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pending operation abandoned at unpair: {OperationId} ({OperationType}) {Description}")]
    private static partial void LogPendingAbandoned(ILogger logger, string operationId, string operationType, string description);

    [LoggerMessage(Level = LogLevel.Information, Message = "Terminal {TerminalId} enrolled as {TerminalName} in branch {BranchName} via {GatewayUrl}")]
    private static partial void LogEnrolled(ILogger logger, string terminalId, string terminalName, string branchName, Uri gatewayUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to gateway {GatewayUrl}")]
    private static partial void LogConnected(ILogger logger, string gatewayUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway connection lost: {Reason}")]
    private static partial void LogDisconnected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway connection attempt {Attempt} failed: {Reason}")]
    private static partial void LogConnectFailed(ILogger logger, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outcome of {OperationType} {OperationId} unknown after attempt {Attempt}: {Reason}")]
    private static partial void LogUncertain(ILogger logger, string operationType, string operationId, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Interrupted operation {OperationId} ({OperationType}) resolved as {Resolution}")]
    private static partial void LogResolved(ILogger logger, string operationId, string operationType, string resolution);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gateway change listener failed")]
    private static partial void LogListenerFailed(ILogger logger, Exception exception);
}
