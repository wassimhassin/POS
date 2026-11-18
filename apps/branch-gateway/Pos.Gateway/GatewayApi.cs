using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Pos.Core.Gateway;

namespace Pos.Gateway;

/// <summary>HTTP API: every route requires a terminal token except the three authentication steps.</summary>
public static class GatewayApi
{
    public const string AnonymousRateLimit = "anonymous";
    public const string EnrollRateLimit = "enroll";

    public static void Map(WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapPost("/enroll", (EnrollRequest request, TerminalAuth auth) => auth.Enroll(request))
            .AllowAnonymous().RequireRateLimiting(EnrollRateLimit);
        v1.MapPost("/auth/challenge", (ChallengeRequest request, TerminalAuth auth) => auth.Challenge(request))
            .AllowAnonymous().RequireRateLimiting(AnonymousRateLimit);
        v1.MapPost("/auth/token", (TokenRequest request, TerminalAuth auth) => auth.IssueToken(request))
            .AllowAnonymous().RequireRateLimiting(AnonymousRateLimit);

        var branch = v1.MapGroup("/branches/{branchId}").AddEndpointFilter(async (context, next) =>
        {
            // The branch in the URL must be the authenticated terminal's branch (= this gateway's branch).
            var session = Session(context.HttpContext);
            var branchId = context.HttpContext.GetRouteValue("branchId") as string;
            return branchId == session.BranchId
                ? await next(context)
                : throw new GatewayException(403, "branch_mismatch", "Ce terminal n'est pas autorisé pour cette succursale.");
        });

        branch.MapGet("/me", (HttpContext http, GatewayStore store) =>
        {
            var s = Session(http);
            return new TerminalInfo(s.TerminalId, s.TerminalName, s.BranchId, store.GetIdentity()!.BranchName);
        });
        branch.MapGet("/orders", (OrderService orders) => orders.Snapshot());
        branch.MapGet("/orders/{orderId}", (string orderId, OrderService orders) => orders.Get(orderId));
        branch.MapGet("/orders/{orderId}/print-intents", (string orderId, OrderService orders) => orders.PrintIntents(orderId));

        branch.MapPost("/orders", (CreateOrderRequest request, HttpContext http, OrderService orders, Broadcaster hub) =>
            hub.Publish(orders.CreateOrder(Session(http), request)));
        branch.MapPost("/orders/{orderId}/items", (string orderId, AddItemRequest request, HttpContext http, OrderService orders, Broadcaster hub) =>
            hub.Publish(orders.AddItem(Session(http), orderId, request)));
        branch.MapPost("/orders/{orderId}/quantity", (string orderId, SetQuantityRequest request, HttpContext http, OrderService orders, Broadcaster hub) =>
            hub.Publish(orders.SetQuantity(Session(http), orderId, request)));
        branch.MapPost("/orders/{orderId}/submit", (string orderId, SubmitRequest request, HttpContext http, OrderService orders, Broadcaster hub) =>
            hub.Publish(orders.Submit(Session(http), orderId, request)));
        branch.MapPost("/operations/{operationId}/resolve", (string operationId, HttpContext http, OrderService orders) =>
            orders.Resolve(Session(http), operationId));

        app.MapHub<OrdersHub>(GatewayProtocol.HubPath, o => o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets);
    }

    public static TerminalSession Session(HttpContext http) =>
        http.Items[typeof(TerminalSession)] as TerminalSession ?? throw TerminalAuth.Unauthorized();

    /// <summary>Maps every failure to a JSON <see cref="GatewayError"/>; internals are logged, never returned.</summary>
    public static async Task HandleErrors(HttpContext http, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            var (status, error) = ex switch
            {
                GatewayException g => (g.StatusCode, new GatewayError(g.Code, g.Message, g.Current)),
                BadHttpRequestException { StatusCode: 413 } => (413, new GatewayError("payload_too_large", "Requête trop volumineuse.")),
                BadHttpRequestException or JsonException => (400, new GatewayError("bad_request", "Requête invalide.")),
                _ => (500, new GatewayError("internal_error", "Erreur interne de la passerelle.")),
            };
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pos.Gateway.Api");
            if (status >= 500)
            {
                logger.LogError(ex, "Request failed {Method} {Path}", http.Request.Method, http.Request.Path);
            }
            else
            {
                logger.LogWarning("Request rejected {Method} {Path} {StatusCode} {ErrorCode}", http.Request.Method, http.Request.Path, status, error.Code);
            }

            http.Response.Clear();
            http.Response.StatusCode = status;
            await http.Response.WriteAsJsonAsync(error, GatewayProtocol.Json);
        }
    }

    /// <summary>LAN only: refuse connections from public internet addresses even if a port is forwarded by mistake.</summary>
    public static bool IsLocalNetwork(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }
}

/// <summary>Pushes order changes to every connected terminal of the branch. A hint only: terminals re-read the snapshot after reconnecting.</summary>
public sealed class Broadcaster(IHubContext<OrdersHub> hub, GatewayStore store)
{
    public OperationResult Publish(OperationResult result)
    {
        if (!result.Replayed)
        {
            var group = OrdersHub.Group(store.GetIdentity()!.BranchId);
            _ = hub.Clients.Group(group).SendAsync(GatewayProtocol.OrderChangedMethod, new OrderChangedEvent(result.Position, result.Order));
        }

        return result;
    }
}

/// <summary>Server → terminal notifications only; terminals cannot invoke anything on the hub.</summary>
[Authorize]
public sealed class OrdersHub(ConnectedTerminals connected) : Hub
{
    public static string Group(string branchId) => $"branch:{branchId}";

    public override async Task OnConnectedAsync()
    {
        var session = GatewayApi.Session(Context.GetHttpContext()!);
        connected.Add(Context, session);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(session.BranchId));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connected.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

public sealed class ConnectedTerminals
{
    private readonly ConcurrentDictionary<string, (HubCallerContext Context, TerminalSession Session)> _connections = new(StringComparer.Ordinal);

    public void Add(HubCallerContext context, TerminalSession session) => _connections[context.ConnectionId] = (context, session);

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public int Count => _connections.Count;

    public IEnumerable<(HubCallerContext Context, TerminalSession Session)> All => _connections.Values;
}

/// <summary>Closes hub connections of revoked terminals or expired tokens (HTTP calls are checked per request).</summary>
public sealed class HubSessionMonitor(ConnectedTerminals connected, TerminalAuth auth, TimeProvider time, ILogger<HubSessionMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var (context, session) in connected.All)
            {
                if (session.ExpiresAt < time.GetUtcNow() || !auth.IsActive(session.TerminalId))
                {
                    logger.LogWarning("Closing live connection of {TerminalId}: token expired or terminal revoked", session.TerminalId);
                    context.Abort();
                    connected.Remove(context.ConnectionId);
                }
            }
        }
    }
}
