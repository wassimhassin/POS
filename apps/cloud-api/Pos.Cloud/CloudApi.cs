using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Sync;

namespace Pos.Cloud;

public static class CloudApi
{
    public const string GatewayPolicy = "gateway";
    public const string ReportPolicy = "report";
    public const string RegisterRateLimit = "register";
    private const int MaxReportOrders = 500;

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/v1/gateways/register", (RegisterGatewayRequest request, GatewayRegistration registration, CancellationToken ct) =>
            registration.RegisterAsync(request, ct))
            .AllowAnonymous()
            .RequireRateLimiting(RegisterRateLimit);

        // No organization/business/branch in the path or body: they come from the authenticated credential.
        app.MapPost("/v1/sync/events", async (SyncBatch batch, HttpContext http, SyncService sync, CancellationToken ct) =>
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
        }).RequireAuthorization(GatewayPolicy);

        // Phase 1.5 recovery: a gateway reads back ITS OWN branch's confirmed events (branch from the credential).
        app.MapGet("/v1/sync/state", async (HttpContext http, CloudDb db, CancellationToken ct) =>
        {
            var gateway = Gateway(http);
            var state = await db.BranchSyncStates.AsNoTracking().SingleAsync(s => s.BranchId == gateway.BranchId && s.OrganizationId == gateway.OrganizationId, ct);
            return Results.Ok(new SyncCloudState(state.LastSequence));
        }).RequireAuthorization(GatewayPolicy);

        app.MapGet("/v1/sync/events", async (long? after, int? limit, HttpContext http, CloudDb db, CancellationToken ct) =>
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
        }).RequireAuthorization(GatewayPolicy);

        var reports = app.MapGroup("/v1/reports").RequireAuthorization(ReportPolicy);

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
        });

        reports.MapGet("/businesses/{businessId:guid}", async (Guid businessId, HttpContext http, CloudDb db, CancellationToken ct) =>
        {
            var business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(b => b.Id == businessId, ct);
            if (business is null || !Report(http).CoversBusiness(business))
            {
                return NotFound();
            }

            var branches = await BranchReports(db, b => b.BusinessId == businessId && b.OrganizationId == business.OrganizationId, ct);
            return Results.Ok(new BusinessReport(business.Id.ToString("D"), business.Name, business.OrganizationId.ToString("D"), branches));
        });

        reports.MapGet("/branches/{branchId:guid}", async (Guid branchId, HttpContext http, CloudDb db, CancellationToken ct) =>
        {
            var branch = await FindBranch(db, http, branchId, ct);
            return branch is null
                ? NotFound()
                : Results.Ok((await BranchReports(db, b => b.Id == branch.Id && b.BusinessId == branch.BusinessId && b.OrganizationId == branch.OrganizationId, ct)).Single());
        });

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
        });

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
        });
    }

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
            var tooLarge = ex.StatusCode == StatusCodes.Status413PayloadTooLarge;
            await WriteError(http, tooLarge ? 413 : 400, tooLarge ? "payload_too_large" : "invalid_request", tooLarge ? "Requête trop volumineuse." : "Requête invalide.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !http.Response.HasStarted)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pos.Cloud.Api").LogError(ex, "Unhandled error on {Path}", http.Request.Path);
            await WriteError(http, 503, "unavailable", "Service momentanément indisponible ; réessayez.");
        }
    }

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
