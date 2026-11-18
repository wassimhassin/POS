using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Sync;

namespace Pos.Cloud;

/// <summary>An API error with its HTTP status and stable code.</summary>
public sealed class CloudException : Exception
{
    public CloudException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public CloudException() : this(400, "bad_request", "Requête invalide.") { }

    public CloudException(string message) : this(400, "bad_request", message) { }

    public CloudException(string message, Exception innerException) : base(message, innerException)
    {
        StatusCode = 400;
        Code = "bad_request";
    }

    public int StatusCode { get; }

    public string Code { get; }
}

/// <summary>The authenticated gateway. Tenant, business and branch come from its credential, never from the request.</summary>
public sealed record GatewayPrincipal(Guid GatewayId, Guid OrganizationId, Guid BusinessId, Guid BranchId);

/// <summary>
/// Accepts a gateway's event batch for ITS branch, strictly in sequence order, in ONE PostgreSQL transaction:
/// lock the branch checkpoint row (serializes concurrent/retried batches of the same branch), then per event:
/// duplicate (same sequence, same id, same content) → acknowledged again, nothing re-applied; different
/// content at a known sequence → stop (conflict); gap → stop (resend from the expected sequence); valid →
/// append the event, update projections, advance the checkpoint. Commit, then answer. Nothing is acknowledged
/// before the commit, and an event is never skipped.
/// </summary>
public sealed partial class SyncService(CloudDb db, TimeProvider time, ILogger<SyncService> logger)
{
#if DEBUG
    /// <summary>Test-only fault injection (Debug builds): kill the process after the changes, before COMMIT.</summary>
    public static int CrashBeforeCommitOfBatch { get; set; }

    private static int s_batches;
#endif

    public async Task<SyncBatchResult> ProcessAsync(GatewayPrincipal gateway, SyncBatch batch, CancellationToken ct)
    {
        ValidateBatchShape(batch);
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var state = await db.BranchSyncStates
            .FromSql($"SELECT * FROM branch_sync_states WHERE branch_id = {gateway.BranchId} FOR UPDATE")
            .SingleAsync(ct);

        var outcomes = new List<SyncEventOutcome>(batch.Events.Count);
        SyncRejection? rejected = null;
        var accepted = 0;
        foreach (var e in batch.Events)
        {
            var eventId = Guid.TryParseExact(e.EventId, "D", out var parsed) ? parsed : Guid.Empty;
            if (eventId == Guid.Empty)
            {
                rejected = new(e.Sequence, SyncRejectionCodes.EventRejected, "Identifiant d'événement invalide.", null);
                break;
            }

            var hash = SyncProtocol.EventHash(e);
            if (e.Sequence <= state.LastSequence)
            {
                var existing = await db.SyncEvents.AsNoTracking()
                    .Where(x => x.BranchId == gateway.BranchId && x.Sequence == e.Sequence)
                    .Select(x => new { x.EventId, x.EventHash })
                    .SingleAsync(ct);
                if (existing.EventId == eventId && existing.EventHash == hash)
                {
                    outcomes.Add(new(e.Sequence, e.EventId, SyncOutcomeStatus.Duplicate));
                    continue;
                }

                rejected = new(e.Sequence, SyncRejectionCodes.SequenceConflict,
                    $"Le cloud détient déjà un AUTRE événement à la séquence {e.Sequence} pour cette succursale (passerelle restaurée ou remplacée ?). Réconciliation manuelle requise.", null);
                break;
            }

            if (e.Sequence != state.LastSequence + 1)
            {
                rejected = new(e.Sequence, SyncRejectionCodes.SequenceGap,
                    $"Séquence {e.Sequence} reçue, {state.LastSequence + 1} attendue.", state.LastSequence + 1);
                break;
            }

            if (await db.SyncEvents.AnyAsync(x => x.BranchId == gateway.BranchId && x.EventId == eventId, ct))
            {
                rejected = new(e.Sequence, SyncRejectionCodes.SequenceConflict,
                    $"L'événement {e.EventId} a déjà été accepté à une autre séquence. Réconciliation manuelle requise.", null);
                break;
            }

            var problem = Validate(e) ?? await ApplyAsync(gateway, e, ct);
            if (problem is not null)
            {
                rejected = new(e.Sequence, SyncRejectionCodes.EventRejected, problem, null);
                break;
            }

            db.SyncEvents.Add(new SyncEventRecord
            {
                OrganizationId = gateway.OrganizationId,
                BusinessId = gateway.BusinessId,
                BranchId = gateway.BranchId,
                Sequence = e.Sequence,
                EventId = eventId,
                Type = e.Type,
                SchemaVersion = e.SchemaVersion,
                EventHash = hash,
                Payload = JsonSerializer.Serialize(e, SyncProtocol.Json),
                GatewayId = gateway.GatewayId,
                OccurredAt = Timestamp(e.OccurredAt)!.Value,
                ReceivedAt = now,
            });
            state.LastSequence = e.Sequence;
            state.LastEventAt = Timestamp(e.OccurredAt);
            await db.SaveChangesAsync(ct); // inside the transaction: later events see this one
            outcomes.Add(new(e.Sequence, e.EventId, SyncOutcomeStatus.Accepted));
            accepted++;
        }

        state.LastSyncAt = now;
        await db.GatewayCredentials.Where(g => g.Id == gateway.GatewayId).ExecuteUpdateAsync(s => s.SetProperty(g => g.LastSeenAt, now), ct);
        await db.SaveChangesAsync(ct);
#if DEBUG
        if (accepted > 0 && CrashBeforeCommitOfBatch > 0 && Interlocked.Increment(ref s_batches) == CrashBeforeCommitOfBatch)
        {
            LogFault(logger, "crash before commit");
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
#endif
        await tx.CommitAsync(ct);

        var duplicates = outcomes.Count - accepted;
        LogBatch(logger, gateway.BranchId, gateway.GatewayId, accepted, duplicates, state.LastSequence);
        if (rejected is not null)
        {
            LogRejected(logger, gateway.BranchId, rejected.Sequence, rejected.Code, rejected.Message);
        }

        return new SyncBatchResult(state.LastSequence, outcomes, rejected);
    }

    private static void ValidateBatchShape(SyncBatch batch)
    {
        if (batch.Events is not { Count: > 0 and <= SyncProtocol.MaxBatchEvents })
        {
            throw new CloudException(400, "invalid_batch", $"Un lot contient de 1 à {SyncProtocol.MaxBatchEvents} événements.");
        }

        for (var i = 0; i < batch.Events.Count; i++)
        {
            if (batch.Events[i].Sequence < 1 || i > 0 && batch.Events[i].Sequence != batch.Events[i - 1].Sequence + 1)
            {
                throw new CloudException(400, "invalid_batch", "Les séquences d'un lot doivent être contiguës et croissantes.");
            }
        }
    }

    /// <summary>Validates an event before it is accepted. Returns the reason for rejection, or null.</summary>
    internal static string? Validate(SyncEvent e)
    {
        if (e.SchemaVersion != SyncProtocol.SchemaVersion)
        {
            return $"Version de schéma {e.SchemaVersion} non prise en charge.";
        }

        if (!SyncEventTypes.IsKnown(e.Type))
        {
            return $"Type d'événement inconnu : {Truncate(e.Type)}.";
        }

        if (Timestamp(e.OccurredAt) is null || e.TerminalId is not null && !SyncProtocol.IsId(e.TerminalId))
        {
            return "Horodatage ou terminal invalide.";
        }

        if (e.Type == SyncEventTypes.CatalogPublished)
        {
            return e.Order is null && e.PrintStations is null && e.Catalog is not null ? ValidateCatalog(e.Catalog) : "Catalogue manquant.";
        }

        if (e.Order is null || e.Catalog is not null)
        {
            return "Commande manquante.";
        }

        if (e.PrintStations is { } stations && (stations.Count > 20 || stations.Any(s => !ValidText(s, 40))))
        {
            return "Postes d'impression invalides.";
        }

        var o = e.Order;
        if (!SyncProtocol.IsId(o.Id) || o.Number < 1 || o.Version < 1 || o.Status is not ("open" or "submitted")
            || o.Currency.Length != 3 || !o.Currency.All(char.IsAsciiLetterUpper)
            || o.Label is { Length: > 60 } || !ValidText(o.CreatedByName, 80) || !ValidText(o.UpdatedByName, 80)
            || o.SubmittedByName is not null && !ValidText(o.SubmittedByName, 80)
            || Timestamp(o.CreatedAt) is null || Timestamp(o.UpdatedAt) is null
            || (o.Status == "submitted") != (o.SubmittedAt is not null) || o.SubmittedAt is not null && Timestamp(o.SubmittedAt) is null)
        {
            return "Commande invalide.";
        }

        if (e.Type == SyncEventTypes.Submit && o.Status != "submitted")
        {
            return "Un envoi en préparation doit produire une commande envoyée.";
        }

        if (o.Lines.Count > SyncProtocol.MaxOrderLines || o.Lines.Select(l => l.ProductId).Distinct(StringComparer.Ordinal).Count() != o.Lines.Count)
        {
            return "Lignes de commande invalides.";
        }

        foreach (var l in o.Lines)
        {
            if (!ValidText(l.ProductId, 64) || !ValidText(l.Name, 200) || l.Quantity is < 1 or > 999 || l.UnitPriceMinor is < 0 or > 1_000_000_000
                || l.LineTotalMinor != l.UnitPriceMinor * l.Quantity || l.StationId is not null && !ValidText(l.StationId, 40))
            {
                return $"Ligne invalide ({Truncate(l.ProductId)}).";
            }
        }

        return o.ItemCount == o.Lines.Sum(l => l.Quantity) && o.TotalMinor == o.Lines.Sum(l => l.LineTotalMinor)
            ? null
            : "Totaux de commande incohérents.";
    }

    private static string? ValidateCatalog(IReadOnlyList<SyncProduct> products)
    {
        if (products.Count > SyncProtocol.MaxCatalogProducts || products.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != products.Count)
        {
            return "Catalogue invalide.";
        }

        return products.All(p => ValidText(p.Id, 64) && ValidText(p.Name, 200) && ValidText(p.Category, 80) && p.PriceMinor is >= 0 and <= 1_000_000_000
            && p.Currency.Length == 3 && p.Currency.All(char.IsAsciiLetterUpper) && (p.StationId is null || ValidText(p.StationId, 40)))
            ? null
            : "Produit de catalogue invalide.";
    }

    /// <summary>Updates the projections from an accepted event. Returns a rejection reason if the event contradicts them.</summary>
    private async Task<string?> ApplyAsync(GatewayPrincipal gateway, SyncEvent e, CancellationToken ct)
    {
        if (e.Type == SyncEventTypes.CatalogPublished)
        {
            var current = await db.BranchProducts.Where(p => p.BranchId == gateway.BranchId).ToDictionaryAsync(p => p.ProductId, StringComparer.Ordinal, ct);
            foreach (var p in e.Catalog!)
            {
                if (!current.Remove(p.Id, out var row))
                {
                    row = new BranchProduct { OrganizationId = gateway.OrganizationId, BusinessId = gateway.BusinessId, BranchId = gateway.BranchId, ProductId = p.Id };
                    db.BranchProducts.Add(row);
                }

                (row.Name, row.Category, row.PriceMinor, row.Currency, row.StationId, row.IsActive, row.LastSequence) =
                    (p.Name, p.Category, p.PriceMinor, p.Currency, p.StationId, p.IsActive, e.Sequence);
            }

            db.BranchProducts.RemoveRange(current.Values);
            return null;
        }

        var o = e.Order!;
        var orderId = Guid.Parse(o.Id);
        if (await db.Orders.AnyAsync(x => x.BranchId == gateway.BranchId && x.Number == o.Number && x.Id != orderId, ct))
        {
            return $"Le numéro de commande {o.Number} appartient déjà à une autre commande de cette succursale.";
        }

        var order = await db.Orders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.BranchId == gateway.BranchId && x.Id == orderId, ct);
        if (order is null)
        {
            order = new BranchOrder { OrganizationId = gateway.OrganizationId, BusinessId = gateway.BusinessId, BranchId = gateway.BranchId, Id = orderId };
            db.Orders.Add(order);
        }
        else if (o.Version < order.Version || order.Status == "submitted" && o.Status == "open")
        {
            // Events arrive in branch order, so this means the branch history is inconsistent: never regress.
            return $"La commande n°{o.Number} reviendrait à un état antérieur (version {o.Version} < {order.Version}).";
        }

        order.Number = o.Number;
        order.Label = o.Label;
        order.Status = o.Status;
        order.Version = o.Version;
        order.Currency = o.Currency;
        order.ItemCount = o.ItemCount;
        order.TotalMinor = o.TotalMinor;
        order.CreatedByName = o.CreatedByName;
        order.UpdatedByName = o.UpdatedByName;
        order.SubmittedByName = o.SubmittedByName;
        order.CreatedAt = Timestamp(o.CreatedAt)!.Value;
        order.UpdatedAt = Timestamp(o.UpdatedAt)!.Value;
        order.SubmittedAt = Timestamp(o.SubmittedAt);
        order.LastSequence = e.Sequence;

        var lines = order.Lines.ToDictionary(l => l.ProductId, StringComparer.Ordinal);
        for (var i = 0; i < o.Lines.Count; i++)
        {
            var l = o.Lines[i];
            if (!lines.Remove(l.ProductId, out var line))
            {
                line = new BranchOrderLine { BranchId = gateway.BranchId, OrderId = orderId, ProductId = l.ProductId };
                order.Lines.Add(line);
            }

            (line.LineNo, line.Name, line.UnitPriceMinor, line.Quantity, line.LineTotalMinor, line.StationId) =
                (i + 1, l.Name, l.UnitPriceMinor, l.Quantity, l.LineTotalMinor, l.StationId);
        }

        foreach (var removed in lines.Values)
        {
            order.Lines.Remove(removed);
        }

        return null;
    }

    internal static DateTimeOffset? Timestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;

    private static bool ValidText(string? value, int max) => value is { Length: > 0 } && value.Length <= max && !value.Any(char.IsControl);

    private static string Truncate(string value) => value.Length <= 40 ? value : value[..40];

    [LoggerMessage(Level = LogLevel.Information, Message = "Sync batch for branch {BranchId} from gateway {GatewayId}: {Accepted} accepted, {Duplicates} duplicates (already applied, not re-applied), last sequence {LastSequence}")]
    private static partial void LogBatch(ILogger logger, Guid branchId, Guid gatewayId, int accepted, int duplicates, long lastSequence);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync for branch {BranchId} stopped at sequence {Sequence}: {Code} {Reason}")]
    private static partial void LogRejected(ILogger logger, Guid branchId, long sequence, string code, string reason);

#if DEBUG
    [LoggerMessage(Level = LogLevel.Critical, Message = "FAULT INJECTION: {Fault}")]
    internal static partial void LogFault(ILogger logger, string fault);
#endif
}
