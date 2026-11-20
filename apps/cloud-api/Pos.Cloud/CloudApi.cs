using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Pos.Cloud.Identity;
using Pos.Sync;

namespace Pos.Cloud;

public static class CloudApi
{
    public const string GatewayPolicy = "gateway";
    public const string ReportPolicy = "report";
    public const string RegisterRateLimit = "register";
    private const int MaxReportOrders = 500;

    /// <summary>The only API version. A breaking change adds /v2 next to it (doc 10 §5).</summary>
    public const string VersionPrefix = "/v1";

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Every route lives under /v1 (unchanged paths). Errors use the CloudError envelope, so it is declared once.
        var v1 = app.MapGroup(VersionPrefix).ProducesErrors(400, 401, 429, 503);

        v1.MapPost("/gateways/register", (RegisterGatewayRequest request, GatewayRegistration registration, CancellationToken ct) =>
            registration.RegisterAsync(request, ct))
            .AllowAnonymous()
            .RequireRateLimiting(RegisterRateLimit)
            .WithName("RegisterGateway").WithTags("Gateways")
            .WithSummary("Register a gateway with a one-time provisioning code and proof of key possession.")
            .Produces<RegisterGatewayResponse>()
            .Produces<CloudError>(StatusCodes.Status409Conflict);

        // No organization/business/branch in the path or body: they come from the authenticated credential.
        v1.MapPost("/sync/events", async (SyncBatch batch, HttpContext http, SyncService sync, CancellationToken ct) =>
        {
            var result = await sync.ProcessAsync(Gateway(http), batch, ct);
#if DEBUG
            if (result.Outcomes.Any(o => o.Status == SyncOutcomeStatus.Accepted) && CloudFaults.AfterCommit(http.RequestServices.GetRequiredService<ILogger<SyncService>>()) is { } fault)
            {
                if (fault == CloudFaults.DropAck)
                {
                    http.Abort(); // committed in PostgreSQL, but the gateway never receives the acknowledgment
                    return Results.Empty;
                }

                System.Diagnostics.Process.GetCurrentProcess().Kill(); // committed, then the cloud dies
            }
#endif
            return Results.Ok(result);
        }).RequireAuthorization(GatewayPolicy)
            .WithName("PushSyncEvents").WithTags("Sync")
            .WithSummary("Deliver the next contiguous batch of the authenticated gateway's branch events (idempotent).")
            .Produces<SyncBatchResult>()
            .Produces<CloudError>(StatusCodes.Status403Forbidden)
            .Produces<CloudError>(StatusCodes.Status413PayloadTooLarge);

        // Phase 1.5 recovery: a gateway reads back ITS OWN branch's confirmed events (branch from the credential).
        v1.MapGet("/sync/state", async (HttpContext http, CloudDb db, CancellationToken ct) =>
        {
            var gateway = Gateway(http);
            var state = await db.BranchSyncStates.AsNoTracking().SingleAsync(s => s.BranchId == gateway.BranchId && s.OrganizationId == gateway.OrganizationId, ct);
            return Results.Ok(new SyncCloudState(state.LastSequence));
        }).RequireAuthorization(GatewayPolicy)
            .WithName("GetSyncState").WithTags("Sync")
            .WithSummary("Last sequence the cloud confirmed for the authenticated gateway's branch.")
            .Produces<SyncCloudState>()
            .Produces<CloudError>(StatusCodes.Status403Forbidden);

        v1.MapGet("/sync/events", async (long? after, int? limit, HttpContext http, CloudDb db, CancellationToken ct) =>
        {
            var gateway = Gateway(http);
            if (after is null or < 0 || limit is < 1 or > SyncProtocol.MaxBatchEvents)
            {
                return Results.BadRequest(new CloudError("invalid_query", $"after >= 0, limit = 1..{SyncProtocol.MaxBatchEvents}."));
            }

            var state = await db.BranchSyncStates.AsNoTracking().SingleAsync(s => s.BranchId == gateway.BranchId && s.OrganizationId == gateway.OrganizationId, ct);
            var rows = await db.SyncEvents.AsNoTracking()
                .Where(e => e.BranchId == gateway.BranchId && e.OrganizationId == gateway.OrganizationId && e.Sequence > after)
                .OrderBy(e => e.Sequence).Take(limit ?? SyncProtocol.MaxBatchEvents).Select(e => e.Payload).ToListAsync(ct);
            return Results.Ok(new SyncEventPage(state.LastSequence, [.. rows.Select(p => JsonSerializer.Deserialize<SyncEvent>(p, SyncProtocol.Json)!)]));
        }).RequireAuthorization(GatewayPolicy)
            .WithName("ListSyncEvents").WithTags("Sync")
            .WithSummary("Confirmed events of the authenticated gateway's branch after a sequence (recovery).")
            .Produces<SyncEventPage>()
            .Produces<CloudError>(StatusCodes.Status403Forbidden);

        // Cloud users (sessions); separate from the gateway and report-key schemes.
        AuthApi.Map(v1);
        MembershipApi.Map(v1); // organization access (roles, scopes, memberships): deny by default

        var reports = v1.MapGroup("/reports").RequireAuthorization(ReportPolicy).WithTags("Reports").ProducesErrors(404);

        reports.MapGet("/organizations/{organizationId:guid}", async (Guid organizationId, HttpContext http, CloudDb db, CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == organizationId, ct);
            if (organization is null || !Report(http).CoversOrganization(organizationId))
            {
                return NotFound();
            }

            var businesses = await db.Businesses.AsNoTracking().Where(b => b.OrganizationId == organizationId).OrderBy(b => b.Name).ToListAsync(ct);
            var branches = await BranchReports(db, b => b.OrganizationId == organizationId, ct);
            return Results.Ok(new OrganizationReport(organization.Id.ToString("D"), organization.Name,
                [.. businesses.Select(b => new BusinessReport(b.Id.ToString("D"), b.Name, b.OrganizationId.ToString("D"),
                    [.. branches.Where(r => r.BusinessId == b.Id.ToString("D"))]))]));
        })
            .WithName("GetOrganizationReport").WithSummary("Organization summary: businesses and branches with sync freshness.")
            .Produces<OrganizationReport>();

        reports.MapGet("/businesses/{businessId:guid}", async (Guid businessId, HttpContext http, CloudDb db, CancellationToken ct) =>
        {
            var business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(b => b.Id == businessId, ct);
            if (business is null || !Report(http).CoversBusiness(business))
            {
                return NotFound();
            }

            var branches = await BranchReports(db, b => b.BusinessId == businessId && b.OrganizationId == business.OrganizationId, ct);
            return Results.Ok(new BusinessReport(business.Id.ToString("D"), business.Name, business.OrganizationId.ToString("D"), branches));
        })
            .WithName("GetBusinessReport").WithSummary("Business summary: its branches with sync freshness.")
            .Produces<BusinessReport>();

        reports.MapGet("/branches/{branchId:guid}", async (Guid branchId, HttpContext http, CloudDb db, CancellationToken ct) =>
        {
            var branch = await FindBranch(db, http, branchId, ct);
            return branch is null
                ? NotFound()
                : Results.Ok((await BranchReports(db, b => b.Id == branch.Id && b.BusinessId == branch.BusinessId && b.OrganizationId == branch.OrganizationId, ct)).Single());
        })
            .WithName("GetBranchReport").WithSummary("Branch summary with sync freshness.")
            .Produces<BranchReport>();

        reports.MapGet("/branches/{branchId:guid}/orders", async (Guid branchId, string? status, int? limit, HttpContext http, CloudDb db, CancellationToken ct) =>
        {
            var branch = await FindBranch(db, http, branchId, ct);
            if (branch is null)
            {
                return NotFound();
            }

            if (status is not (null or "open" or "submitted") || limit is < 1 or > MaxReportOrders)
            {
                return Results.BadRequest(new CloudError("invalid_query", $"status = open|submitted, limit = 1..{MaxReportOrders}."));
            }

            var query = db.Orders.AsNoTracking().Include(o => o.Lines)
                .Where(o => o.OrganizationId == branch.OrganizationId && o.BusinessId == branch.BusinessId && o.BranchId == branch.Id);
            if (status is not null)
            {
                query = query.Where(o => o.Status == status);
            }

            var orders = await query.OrderBy(o => o.Number).Take(limit ?? MaxReportOrders).ToListAsync(ct);
            return Results.Ok(orders.Select(ToReport).ToList());
        })
            .WithName("ListBranchOrders").WithSummary("Synced orders of a branch, by number (status = open|submitted, limit 1..500).")
            .Produces<List<ReportOrder>>();

        reports.MapGet("/branches/{branchId:guid}/catalog", async (Guid branchId, HttpContext http, CloudDb db, CancellationToken ct) =>
        {
            var branch = await FindBranch(db, http, branchId, ct);
            if (branch is null)
            {
                return NotFound();
            }

            var products = await db.BranchProducts.AsNoTracking()
                .Where(p => p.OrganizationId == branch.OrganizationId && p.BusinessId == branch.BusinessId && p.BranchId == branch.Id)
                .OrderBy(p => p.ProductId).ToListAsync(ct);
            return Results.Ok(new ReportCatalog(branch.Id.ToString("D"), products.Count == 0 ? 0 : products.Max(p => p.LastSequence),
                [.. products.Select(p => new SyncProduct(p.ProductId, p.Name, p.Category, p.PriceMinor, p.Currency, p.StationId, p.IsActive))]));
        })
            .WithName("GetBranchCatalog").WithSummary("Catalog last published by a branch gateway.")
            .Produces<ReportCatalog>();
    }

    /// <summary>Declares <see cref="CloudError"/> bodies for these statuses (works on groups, unlike Produces).</summary>
    public static TBuilder ProducesErrors<TBuilder>(this TBuilder builder, params int[] statuses) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata([.. statuses.Select(s => new ProducesResponseTypeMetadata(s, typeof(CloudError), ["application/json"]))]);

    /// <summary>Maps exceptions to JSON errors without leaking internals.</summary>
    public static async Task HandleErrors(HttpContext http, Func<Task> next)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            await next();
        }
        catch (CloudException ex) when (!http.Response.HasStarted)
        {
            await WriteError(http, ex.StatusCode, ex.Code, ex.Message);
        }
        catch (BadHttpRequestException ex) when (!http.Response.HasStarted)
        {
            // Keep the framework's 4xx (400 malformed, 413 too large, 415 media type…); messages never echo the input.
            var status = ex.StatusCode is >= 400 and < 500 ? ex.StatusCode : StatusCodes.Status400BadRequest;
            var (code, message) = ErrorFor(status);
            await WriteError(http, status, code, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !http.Response.HasStarted)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pos.Cloud.Api").LogError(ex, "Unhandled error on {Path}", http.Request.Path);
            await WriteError(http, 503, "unavailable", "Service momentanément indisponible ; réessayez.");
        }
    }

    /// <summary>
    /// Status-code pages: responses the framework ends without a body (unknown route, wrong method, rate limit,
    /// unsupported media type…) get the same <see cref="CloudError"/> envelope. Responses with a body are untouched.
    /// </summary>
    public static Task WriteStatusCodeError(StatusCodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var status = context.HttpContext.Response.StatusCode;
        var (code, message) = ErrorFor(status);
        return WriteError(context.HttpContext, status, code, message);
    }

    /// <summary>Default code and message for a status (the envelope's code set is part of the contract).</summary>
    public static (string Code, string Message) ErrorFor(int status) => status switch
    {
        400 => ("invalid_request", "Requête invalide."),
        401 => ("unauthorized", "Authentification requise ou refusée."),
        403 => ("forbidden", "Accès refusé."),
        404 => ("not_found", "Ressource introuvable."),
        405 => ("method_not_allowed", "Méthode non autorisée pour cette ressource."),
        406 => ("not_acceptable", "Format de réponse non disponible."),
        408 => ("request_timeout", "Requête trop lente ; réessayez."),
        409 => ("conflict", "Conflit avec l'état actuel."),
        411 => ("length_required", "Longueur de requête requise."),
        413 => ("payload_too_large", "Requête trop volumineuse."),
        415 => ("unsupported_media_type", "Type de contenu non pris en charge (application/json attendu)."),
        429 => ("rate_limited", "Trop de requêtes ; réessayez plus tard."),
        >= 500 => ("unavailable", "Service momentanément indisponible ; réessayez."),
        _ => ("error", "Requête refusée."),
    };

    private static readonly object s_challengeWritten = new();

    /// <summary>
    /// Called by each scheme's challenge (the fallback policy names both): only the first one writes, otherwise the
    /// second body is appended to the first. The per-request marker does not depend on buffering/flush timing.
    /// </summary>
    public static Task WriteUnauthorized(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Response.HasStarted || !http.Items.TryAdd(s_challengeWritten, true))
        {
            return Task.CompletedTask;
        }

        return http.Items.ContainsKey(GatewaySignatureHandler.RevokedMarker)
            ? WriteError(http, 403, CloudErrorCodes.GatewayRevoked, "Cette passerelle a été révoquée (remplacée ?) : elle ne doit plus servir la succursale.")
            : WriteError(http, 401, "unauthorized", "Authentification requise ou refusée.");
    }

    /// <summary>Writes the CloudError envelope (used by other modules' handlers).</summary>
    public static Task WriteErrorAsync(HttpContext http, int status, string code, string message) => WriteError(http, status, code, message);

    private static async Task WriteError(HttpContext http, int status, string code, string message)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new CloudError(code, message), SyncProtocol.Json);
    }

    private static GatewayPrincipal Gateway(HttpContext http) =>
        http.Items[typeof(GatewayPrincipal)] as GatewayPrincipal ?? throw new CloudException(401, "unauthorized", "Passerelle non authentifiée.");

    private static ReportPrincipal Report(HttpContext http) =>
        http.Items[typeof(ReportPrincipal)] as ReportPrincipal ?? throw new CloudException(401, "unauthorized", "Clé de lecture requise.");

    /// <summary>Out-of-scope and non-existent resources are indistinguishable (404), so ids cannot be probed.</summary>
    private static IResult NotFound() => Results.NotFound(new CloudError("not_found", "Ressource introuvable."));

    private static async Task<Branch?> FindBranch(CloudDb db, HttpContext http, Guid branchId, CancellationToken ct)
    {
        var branch = await db.Branches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == branchId, ct);
        return branch is not null && Report(http).CoversBranch(branch) ? branch : null;
    }

    private static async Task<List<BranchReport>> BranchReports(CloudDb db, System.Linq.Expressions.Expression<Func<Branch, bool>> filter, CancellationToken ct)
    {
        var rows = await db.Branches.AsNoTracking().Where(filter)
            .Join(db.BranchSyncStates, b => new { b.OrganizationId, b.BusinessId, BranchId = b.Id }, s => new { s.OrganizationId, s.BusinessId, s.BranchId }, (b, s) => new
            {
                b.Id,
                b.Name,
                b.BusinessId,
                s.LastSequence,
                s.LastSyncAt,
                OrderCount = db.Orders.Count(o => o.BranchId == b.Id && o.OrganizationId == b.OrganizationId),
                SubmittedCount = db.Orders.Count(o => o.BranchId == b.Id && o.OrganizationId == b.OrganizationId && o.Status == "submitted"),
                SubmittedTotal = db.Orders.Where(o => o.BranchId == b.Id && o.OrganizationId == b.OrganizationId && o.Status == "submitted").Sum(o => (long?)o.TotalMinor) ?? 0,
            })
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
        return [.. rows.Select(r => new BranchReport(r.Id.ToString("D"), r.Name, r.BusinessId.ToString("D"), r.OrderCount, r.SubmittedCount,
            r.SubmittedTotal, r.LastSequence, r.LastSyncAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)))];
    }

    private static ReportOrder ToReport(BranchOrder o) => new(o.BranchId.ToString("D"), o.LastSequence, new SyncOrder(
        o.Id.ToString("D"), o.Number, o.Label, o.Status, o.Version, o.Currency,
        [.. o.Lines.OrderBy(l => l.LineNo).Select(l => new SyncOrderLine(l.ProductId, l.Name, l.UnitPriceMinor, l.Quantity, l.LineTotalMinor, l.StationId))],
        o.ItemCount, o.TotalMinor, o.CreatedByName, o.UpdatedByName, o.SubmittedByName,
        Iso(o.CreatedAt), Iso(o.UpdatedAt), o.SubmittedAt is { } s ? Iso(s) : null));

    private static string Iso(DateTimeOffset value) => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    internal static JsonSerializerOptions Json => SyncProtocol.Json;
}

#if DEBUG
/// <summary>Test-only fault injection (Debug builds), from POS_CLOUD_FAULT: drop-ack:N | crash-after-commit:N | crash-before-commit:N.</summary>
internal static class CloudFaults
{
    public const string DropAck = "drop-ack";
    public const string CrashAfterCommit = "crash-after-commit";

    private static string? s_mode;
    private static int s_remaining;

    public static void Configure(string? value, ILogger logger)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var parts = value.Split(':');
        var count = parts.Length == 2 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1;
        if (parts[0] == "crash-before-commit")
        {
            SyncService.CrashBeforeCommitOfBatch = count;
        }
        else
        {
            (s_mode, s_remaining) = (parts[0], count);
        }

        logger.LogWarning("Fault injection armed: {Fault}", value);
    }

    /// <summary>For drop-ack: the first N committed batches lose their response. For crash-after-commit: the Nth kills the process.</summary>
    public static string? AfterCommit(ILogger logger)
    {
        if (s_mode == DropAck && Interlocked.Decrement(ref s_remaining) >= 0)
        {
            SyncService.LogFault(logger, "acknowledgment dropped after commit");
            return DropAck;
        }

        if (s_mode == CrashAfterCommit && Interlocked.Decrement(ref s_remaining) == 0)
        {
            SyncService.LogFault(logger, "crash after commit");
            return CrashAfterCommit;
        }

        return null;
    }
}
#endif
