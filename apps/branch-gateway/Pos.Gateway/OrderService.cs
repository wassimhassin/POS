using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pos.Core.Gateway;
using Pos.Core.Printing;

namespace Pos.Gateway;

/// <summary>A rejected request. <see cref="StatusCode"/> is the HTTP status; <see cref="Current"/> is set on conflicts.</summary>
public sealed class GatewayException : Exception
{
    public GatewayException(int statusCode, string code, string message, SharedOrder? current = null) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Current = current;
    }

    public GatewayException() : this(400, "bad_request", "Requête invalide.") { }

    public GatewayException(string message) : this(400, "bad_request", message) { }

    public GatewayException(string message, Exception innerException) : base(message, innerException)
    {
        StatusCode = 400;
        Code = "bad_request";
    }

    public int StatusCode { get; }

    public string Code { get; }

    public SharedOrder? Current { get; }
}

/// <summary>
/// Authoritative shared-order state for the branch. Single serialized writer: every mutation runs in
/// one SQLite transaction that (1) checks the idempotency key, (2) validates and applies the change
/// with optimistic concurrency, (3) appends to the operation journal, (4) stores the response for
/// replay and, for a submission, (5) creates one print intent + durable print job per station.
/// The client is acknowledged only after commit.
/// </summary>
public sealed partial class OrderService(GatewayStore store, PrintQueue printQueue, ILogger<OrderService> logger)
{
    public const int MaxAddQuantity = 99;
    private static readonly TimeSpan SubmittedVisibleFor = TimeSpan.FromHours(24);

    private readonly Lock _writer = new();

    /// <summary>Called after a submission committed print jobs (wakes the print worker).</summary>
    public Action? PrintJobsCreated { get; set; }

#if DEBUG
    /// <summary>Test-only fault injection (Debug builds): kill the process right after a commit of this operation type.</summary>
    public string? CrashAfterCommitOf { get; set; }
#endif

    public OrdersSnapshot Snapshot()
    {
        using var connection = store.Database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: true);
        var position = Convert.ToInt64(GatewayStore.Scalar(connection, tx, "SELECT COALESCE(MAX(position), 0) FROM operations;"), CultureInfo.InvariantCulture);
        using var query = GatewayStore.Command(connection, tx, """
            SELECT id FROM shared_orders WHERE status = 'open' OR submitted_at >= $since ORDER BY number DESC LIMIT 100;
            """);
        query.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.Subtract(SubmittedVisibleFor).ToString("O", CultureInfo.InvariantCulture));
        var ids = new List<string>();
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        var orders = ids.Select(id => LoadOrder(connection, tx, id)!).ToList();
        tx.Commit();
        return new OrdersSnapshot(position, orders);
    }

    public SharedOrder Get(string orderId)
    {
        RequireId(orderId, "order_not_found");
        using var connection = store.Database.OpenConnection();
        return LoadOrder(connection, null, orderId) ?? throw NotFound();
    }

    public IReadOnlyList<PrintIntent> PrintIntents(string orderId)
    {
        RequireId(orderId, "order_not_found");
        using var connection = store.Database.OpenConnection();
        return LoadIntents(connection, null, orderId);
    }

    public OperationResult CreateOrder(TerminalSession session, CreateOrderRequest request)
    {
        var label = ValidateLabel(request.Label);
        return Execute(session, "create_order", null, request.OperationId, request, (connection, tx, now) =>
        {
            var id = Guid.NewGuid().ToString("D");
            var number = Convert.ToInt32(GatewayStore.Scalar(connection, tx, "SELECT COALESCE(MAX(number), 0) + 1 FROM shared_orders;"), CultureInfo.InvariantCulture);
            var currency = GatewayStore.Scalar(connection, tx, "SELECT currency FROM products ORDER BY sort_order LIMIT 1;") as string ?? "TND";
            using var insert = GatewayStore.Command(connection, tx, """
                INSERT INTO shared_orders (id, branch_id, number, label, status, version, currency, created_by, created_at, updated_by, updated_at)
                VALUES ($id, $branch, $number, $label, 'open', 1, $currency, $terminal, $now, $terminal, $now);
                """);
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$branch", session.BranchId);
            insert.Parameters.AddWithValue("$number", number);
            insert.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
            insert.Parameters.AddWithValue("$currency", currency);
            insert.Parameters.AddWithValue("$terminal", session.TerminalId);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
            return new Applied(id, null, [], []);
        });
    }

    public OperationResult AddItem(TerminalSession session, string orderId, AddItemRequest request)
    {
        RequireId(orderId, "order_not_found");
        if (string.IsNullOrWhiteSpace(request.ProductId) || request.ProductId.Length > 64)
        {
            throw new GatewayException(400, "invalid_product", "Produit invalide.");
        }

        if (request.Quantity is < 1 or > MaxAddQuantity)
        {
            throw new GatewayException(400, "invalid_quantity", $"La quantité doit être comprise entre 1 et {MaxAddQuantity}.");
        }

        return Execute(session, "add_item", orderId, request.OperationId, request, (connection, tx, now) =>
        {
            var order = RequireOpen(connection, tx, orderId);
            var existing = order.Lines.FirstOrDefault(l => l.ProductId == request.ProductId);
            if (existing is not null)
            {
                var quantity = existing.Quantity + request.Quantity;
                if (quantity > GatewayProtocol.MaxQuantity)
                {
                    throw new GatewayException(409, "quantity_limit", $"Quantité maximale atteinte ({GatewayProtocol.MaxQuantity}).", order);
                }

                SetLineQuantity(connection, tx, orderId, request.ProductId, quantity);
            }
            else
            {
                using var product = GatewayStore.Command(connection, tx, "SELECT name, price_minor FROM products WHERE id = $id AND is_active = 1;");
                product.Parameters.AddWithValue("$id", request.ProductId);
                using var reader = product.ExecuteReader();
                if (!reader.Read())
                {
                    throw new GatewayException(400, "unknown_product", "Produit inconnu ou indisponible.");
                }

                using var insert = GatewayStore.Command(connection, tx, """
                    INSERT INTO shared_order_lines (order_id, product_id, line_no, product_name, unit_price_minor, quantity)
                    VALUES ($order, $product, (SELECT COALESCE(MAX(line_no), 0) + 1 FROM shared_order_lines WHERE order_id = $order), $name, $price, $qty);
                    """);
                insert.Parameters.AddWithValue("$order", orderId);
                insert.Parameters.AddWithValue("$product", request.ProductId);
                insert.Parameters.AddWithValue("$name", reader.GetString(0));
                insert.Parameters.AddWithValue("$price", reader.GetInt64(1));
                insert.Parameters.AddWithValue("$qty", request.Quantity);
                insert.ExecuteNonQuery();
            }

            BumpVersion(connection, tx, orderId, session.TerminalId, now);
            return new Applied(orderId, null, [], []);
        });
    }

    public OperationResult SetQuantity(TerminalSession session, string orderId, SetQuantityRequest request)
    {
        RequireId(orderId, "order_not_found");
        if (request.Quantity is < 0 or > GatewayProtocol.MaxQuantity)
        {
            throw new GatewayException(400, "invalid_quantity", $"La quantité doit être comprise entre 0 et {GatewayProtocol.MaxQuantity}.");
        }

        return Execute(session, "set_quantity", orderId, request.OperationId, request, (connection, tx, now) =>
        {
            var order = RequireOpen(connection, tx, orderId);
            RequireVersion(order, request.ExpectedVersion);
            if (order.Lines.All(l => l.ProductId != request.ProductId))
            {
                throw new GatewayException(409, "line_not_found", "Cet article n'est plus dans la commande.", order);
            }

            SetLineQuantity(connection, tx, orderId, request.ProductId, request.Quantity);
            BumpVersion(connection, tx, orderId, session.TerminalId, now);
            return new Applied(orderId, request.ExpectedVersion, [], []);
        });
    }

    public OperationResult Submit(TerminalSession session, string orderId, SubmitRequest request)
    {
        RequireId(orderId, "order_not_found");
        var result = Execute(session, "submit", orderId, request.OperationId, request, (connection, tx, now) =>
        {
            var order = RequireOpen(connection, tx, orderId);
            RequireVersion(order, request.ExpectedVersion);
            if (order.Lines.Count == 0)
            {
                throw new GatewayException(409, "empty_order", "Impossible d'envoyer une commande vide.", order);
            }

            using (var update = GatewayStore.Command(connection, tx, """
                UPDATE shared_orders SET status = 'submitted', submitted_by = $terminal, submitted_at = $now WHERE id = $id;
                """))
            {
                update.Parameters.AddWithValue("$id", orderId);
                update.Parameters.AddWithValue("$terminal", session.TerminalId);
                update.Parameters.AddWithValue("$now", now);
                update.ExecuteNonQuery();
            }

            BumpVersion(connection, tx, orderId, session.TerminalId, now);
            var (intents, unrouted) = CreatePrintIntents(connection, tx, order, request.OperationId, now);
            return new Applied(orderId, request.ExpectedVersion, intents, unrouted);
        });

        if (!result.Replayed && result.PrintIntents.Count > 0)
        {
            PrintJobsCreated?.Invoke();
        }

        return result;
    }

    /// <summary>
    /// Settles an operation whose response the terminal never received. If it was applied, returns its
    /// result. Otherwise the ID is burned ("abandoned") inside the writer lock, so a delayed duplicate
    /// can never apply it later: the outcome becomes definitive either way.
    /// </summary>
    public OperationResolution Resolve(TerminalSession session, string operationId)
    {
        RequireId(operationId, "invalid_operation_id");
        lock (_writer)
        {
            using var connection = store.Database.OpenConnection();
            using var tx = connection.BeginTransaction();
            var existing = FindOperation(connection, tx, operationId);
            if (existing is not null)
            {
                if (existing.TerminalId != session.TerminalId)
                {
                    throw new GatewayException(404, "operation_not_found", "Opération inconnue.");
                }

                return existing.Status == OperationStatus.Applied
                    ? new OperationResolution(operationId, OperationStatus.Applied, Replay(existing))
                    : new OperationResolution(operationId, OperationStatus.Abandoned, null);
            }

            using var insert = GatewayStore.Command(connection, tx, """
                INSERT INTO operations (operation_id, terminal_id, type, status, at) VALUES ($op, $terminal, 'resolve', 'abandoned', $now);
                """);
            insert.Parameters.AddWithValue("$op", operationId);
            insert.Parameters.AddWithValue("$terminal", session.TerminalId);
            insert.Parameters.AddWithValue("$now", store.Now());
            insert.ExecuteNonQuery();
            tx.Commit();
            LogAbandoned(logger, operationId, session.TerminalId);
            return new OperationResolution(operationId, OperationStatus.Abandoned, null);
        }
    }

    private sealed record Applied(string OrderId, long? ExpectedVersion, IReadOnlyList<PrintIntent> Intents, IReadOnlyList<string> Unrouted);

    private sealed record StoredOperation(long Position, string TerminalId, string Type, string Status, string? RequestHash, string? ResponseJson);

    private OperationResult Execute(
        TerminalSession session,
        string type,
        string? orderId,
        string operationId,
        object request,
        Func<SqliteConnection, SqliteTransaction, string, Applied> apply)
    {
        RequireId(operationId, "invalid_operation_id");
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{type}\n{orderId}\n{JsonSerializer.Serialize(request, request.GetType(), GatewayProtocol.Json)}")));

        OperationResult result;
        lock (_writer)
        {
            using var connection = store.Database.OpenConnection();
            using var tx = connection.BeginTransaction(); // BEGIN IMMEDIATE: single writer

            var existing = FindOperation(connection, tx, operationId);
            if (existing is not null)
            {
                if (existing.TerminalId != session.TerminalId || existing.Status == OperationStatus.Applied && existing.RequestHash != requestHash)
                {
                    throw new GatewayException(409, "idempotency_key_reused", "Cet identifiant d'opération a déjà été utilisé pour une autre requête.");
                }

                if (existing.Status == OperationStatus.Abandoned)
                {
                    throw new GatewayException(409, "operation_abandoned", "Cette opération a été annulée après une interruption ; elle n'a pas été appliquée.");
                }

                LogReplayed(logger, operationId, type, session.TerminalId);
                return Replay(existing);
            }

            var now = store.Now();
            long position;
            using (var insert = GatewayStore.Command(connection, tx, """
                INSERT INTO operations (operation_id, terminal_id, type, status, request_hash, at)
                VALUES ($op, $terminal, $type, 'applied', $hash, $now) RETURNING position;
                """))
            {
                insert.Parameters.AddWithValue("$op", operationId);
                insert.Parameters.AddWithValue("$terminal", session.TerminalId);
                insert.Parameters.AddWithValue("$type", type);
                insert.Parameters.AddWithValue("$hash", requestHash);
                insert.Parameters.AddWithValue("$now", now);
                position = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            var applied = apply(connection, tx, now);
            var order = LoadOrder(connection, tx, applied.OrderId)!;
            result = new OperationResult(operationId, position, false, order, applied.Intents, applied.Unrouted);

            using (var update = GatewayStore.Command(connection, tx, """
                UPDATE operations SET order_id = $order, expected_version = $expected, result_version = $version, response_json = $response
                WHERE position = $position;
                """))
            {
                update.Parameters.AddWithValue("$order", order.Id);
                update.Parameters.AddWithValue("$expected", (object?)applied.ExpectedVersion ?? DBNull.Value);
                update.Parameters.AddWithValue("$version", order.Version);
                update.Parameters.AddWithValue("$response", JsonSerializer.Serialize(result, GatewayProtocol.Json));
                update.Parameters.AddWithValue("$position", position);
                update.ExecuteNonQuery();
            }

            tx.Commit();
            LogApplied(logger, type, operationId, session.TerminalId, order.Id, order.Version, position);
        }

#if DEBUG
        if (CrashAfterCommitOf == type)
        {
            LogFaultInjected(logger, type);
            System.Diagnostics.Process.GetCurrentProcess().Kill(); // simulated power cut: committed, never acknowledged
        }
#endif
        return result;
    }

    private static OperationResult Replay(StoredOperation existing) =>
        JsonSerializer.Deserialize<OperationResult>(existing.ResponseJson!, GatewayProtocol.Json)! with { Replayed = true };

    private static StoredOperation? FindOperation(SqliteConnection connection, SqliteTransaction tx, string operationId)
    {
        using var query = GatewayStore.Command(connection, tx, """
            SELECT position, terminal_id, type, status, request_hash, response_json FROM operations WHERE operation_id = $op;
            """);
        query.Parameters.AddWithValue("$op", operationId);
        using var reader = query.ExecuteReader();
        return reader.Read()
            ? new StoredOperation(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5))
            : null;
    }

    private (IReadOnlyList<PrintIntent>, IReadOnlyList<string>) CreatePrintIntents(
        SqliteConnection connection, SqliteTransaction tx, SharedOrder order, string operationId, string now)
    {
        var routing = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in order.Lines)
        {
            using var query = GatewayStore.Command(connection, tx, "SELECT station_id FROM products WHERE id = $id;");
            query.Parameters.AddWithValue("$id", line.ProductId);
            routing[line.ProductId] = query.ExecuteScalar() as string;
        }

        var stations = new List<(string Id, string Name, int Width)>();
        using (var query = GatewayStore.Command(connection, tx, "SELECT id, name, paper_width_mm FROM print_destinations WHERE kind = 'station' ORDER BY sort_order;"))
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                stations.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            }
        }

        var number = order.Number.ToString("D3", CultureInfo.InvariantCulture);
        var orderedAt = DateTimeOffset.Parse(now, CultureInfo.InvariantCulture);
        var intents = new List<PrintIntent>();
        foreach (var station in stations)
        {
            var lines = order.Lines.Where(l => routing[l.ProductId] == station.Id).ToList();
            if (lines.Count == 0)
            {
                continue;
            }

            var source = new TicketSource(TicketLayout.StationKind, number, orderedAt, station.Name,
                lines.Select(l => new TicketItem(l.Name, l.Quantity, l.UnitPriceMinor, l.LineTotalMinor)).ToList(),
                lines.Sum(l => l.Quantity), lines.Sum(l => l.LineTotalMinor), order.Currency);
            var jobId = printQueue.Enqueue(connection, tx, new NewPrintJob(
                TicketLayout.StationKind, station.Id, order.Id, number, source, station.Width, 0, null));

            // The primary key (order_id, station_id) makes a second intent for the same station impossible.
            using var insert = GatewayStore.Command(connection, tx, """
                INSERT INTO print_intents (order_id, station_id, print_job_id, operation_id, created_at) VALUES ($order, $station, $job, $op, $now);
                """);
            insert.Parameters.AddWithValue("$order", order.Id);
            insert.Parameters.AddWithValue("$station", station.Id);
            insert.Parameters.AddWithValue("$job", jobId);
            insert.Parameters.AddWithValue("$op", operationId);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
            intents.Add(new PrintIntent(station.Id, jobId));
        }

        var unrouted = order.Lines.Where(l => routing[l.ProductId] is null).Select(l => l.Name).ToList();
        return (intents, unrouted);
    }

    private static SharedOrder RequireOpen(SqliteConnection connection, SqliteTransaction tx, string orderId)
    {
        var order = LoadOrder(connection, tx, orderId) ?? throw NotFound();
        return order.Status == SharedOrderStatus.Open
            ? order
            : throw new GatewayException(409, "order_already_submitted", $"La commande n°{order.Number} a déjà été envoyée en préparation.", order);
    }

    private static void RequireVersion(SharedOrder order, long expectedVersion)
    {
        if (order.Version != expectedVersion)
        {
            throw new GatewayException(409, "version_conflict",
                $"La commande n°{order.Number} a été modifiée par {order.UpdatedByName}. Vérifiez la commande à jour puis recommencez.", order);
        }
    }

    private static void SetLineQuantity(SqliteConnection connection, SqliteTransaction tx, string orderId, string productId, int quantity)
    {
        using var command = GatewayStore.Command(connection, tx, quantity == 0
            ? "DELETE FROM shared_order_lines WHERE order_id = $order AND product_id = $product;"
            : "UPDATE shared_order_lines SET quantity = $qty WHERE order_id = $order AND product_id = $product;");
        command.Parameters.AddWithValue("$order", orderId);
        command.Parameters.AddWithValue("$product", productId);
        command.Parameters.AddWithValue("$qty", quantity);
        command.ExecuteNonQuery();
    }

    private static void BumpVersion(SqliteConnection connection, SqliteTransaction tx, string orderId, string terminalId, string now)
    {
        using var update = GatewayStore.Command(connection, tx, """
            UPDATE shared_orders SET version = version + 1, updated_by = $terminal, updated_at = $now WHERE id = $id;
            """);
        update.Parameters.AddWithValue("$id", orderId);
        update.Parameters.AddWithValue("$terminal", terminalId);
        update.Parameters.AddWithValue("$now", now);
        update.ExecuteNonQuery();
    }

    internal static SharedOrder? LoadOrder(SqliteConnection connection, SqliteTransaction? tx, string orderId)
    {
        using var query = GatewayStore.Command(connection, tx, """
            SELECT o.id, o.number, o.label, o.status, o.version, o.currency, c.name, u.name, o.created_at, o.updated_at, o.submitted_at, s.name
            FROM shared_orders o
            JOIN terminals c ON c.id = o.created_by
            JOIN terminals u ON u.id = o.updated_by
            LEFT JOIN terminals s ON s.id = o.submitted_by
            WHERE o.id = $id;
            """);
        query.Parameters.AddWithValue("$id", orderId);
        using var reader = query.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        var (id, number, label, status, version, currency) =
            (reader.GetString(0), reader.GetInt32(1), Text(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5));
        var (createdBy, updatedBy, createdAt, updatedAt, submittedAt, submittedBy) =
            (reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), Text(10), Text(11));

        var lines = new List<SharedOrderLine>();
        using var linesQuery = GatewayStore.Command(connection, tx, """
            SELECT l.product_id, l.product_name, l.unit_price_minor, l.quantity, p.station_id
            FROM shared_order_lines l LEFT JOIN products p ON p.id = l.product_id
            WHERE l.order_id = $id ORDER BY l.line_no;
            """);
        linesQuery.Parameters.AddWithValue("$id", orderId);
        using (var lineReader = linesQuery.ExecuteReader())
        {
            while (lineReader.Read())
            {
                var price = lineReader.GetInt64(2);
                var quantity = lineReader.GetInt32(3);
                lines.Add(new SharedOrderLine(lineReader.GetString(0), lineReader.GetString(1), price, quantity, price * quantity,
                    lineReader.IsDBNull(4) ? null : lineReader.GetString(4)));
            }
        }

        return new SharedOrder(id, number, label, status, version, lines, lines.Sum(l => l.Quantity), lines.Sum(l => l.LineTotalMinor),
            currency, createdBy, updatedBy, createdAt, updatedAt, submittedAt, submittedBy);
    }

    private static List<PrintIntent> LoadIntents(SqliteConnection connection, SqliteTransaction? tx, string orderId)
    {
        using var query = GatewayStore.Command(connection, tx, "SELECT station_id, print_job_id FROM print_intents WHERE order_id = $id ORDER BY station_id;");
        query.Parameters.AddWithValue("$id", orderId);
        using var reader = query.ExecuteReader();
        var list = new List<PrintIntent>();
        while (reader.Read())
        {
            list.Add(new PrintIntent(reader.GetString(0), reader.GetString(1)));
        }

        return list;
    }

    private static string? ValidateLabel(string? label)
    {
        if (label is null)
        {
            return null;
        }

        label = label.Trim();
        if (label.Length > GatewayProtocol.MaxLabelLength || label.Any(char.IsControl))
        {
            throw new GatewayException(400, "invalid_label", $"Libellé invalide ({GatewayProtocol.MaxLabelLength} caractères maximum).");
        }

        return label.Length == 0 ? null : label;
    }

    private static void RequireId(string? id, string code)
    {
        if (!GatewayProtocol.IsId(id))
        {
            throw code == "order_not_found" ? NotFound() : new GatewayException(400, code, "Identifiant invalide.");
        }
    }

    private static GatewayException NotFound() => new(404, "order_not_found", "Commande introuvable.");

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {OperationType} {OperationId} by {TerminalId} on order {OrderId} → version {OrderVersion} at position {LogPosition}")]
    private static partial void LogApplied(ILogger logger, string operationType, string operationId, string terminalId, string orderId, long orderVersion, long logPosition);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replayed {OperationId} ({OperationType}) for {TerminalId}: original result returned, nothing re-applied")]
    private static partial void LogReplayed(ILogger logger, string operationId, string operationType, string terminalId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation {OperationId} from {TerminalId} was never applied and is now abandoned")]
    private static partial void LogAbandoned(ILogger logger, string operationId, string terminalId);

#if DEBUG
    [LoggerMessage(Level = LogLevel.Critical, Message = "FAULT INJECTION: killing the gateway after committing {OperationType}")]
    private static partial void LogFaultInjected(ILogger logger, string operationType);
#endif
}
