using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pos.Core.Catalog;
using Pos.Core.Orders;
using Pos.Core.Printing;

namespace Pos.Core.Bridge;

/// <summary>
/// Handles UI â†’ host messages. Only allowlisted commands are routed; the envelope and every
/// payload are strictly validated (unknown fields, wrong types and oversize messages are rejected).
/// Transport-agnostic: takes and returns JSON strings, so it is testable without WebView2.
/// </summary>
public sealed partial class BridgeDispatcher
{
    public const int ProtocolVersion = 1;
    public const int MaxMessageLength = 64 * 1024;
    private const int MaxLogMessageLength = 2_000;
    private const int MaxLogDetailLength = 8_000;

    private static readonly HashSet<string> EnvelopeFields = new(StringComparer.Ordinal) { "v", "id", "type", "payload" };

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 16,
    };

    private readonly Dictionary<string, Func<JsonElement, object?>> _handlers;
    private readonly ILogger _logger;
    private readonly ILogger _uiLogger;

    public BridgeDispatcher(ProductRepository products, DraftOrderRepository drafts, PrintService printing, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(printing);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<BridgeDispatcher>();
        _uiLogger = loggerFactory.CreateLogger("Pos.Ui");

        _handlers = new(StringComparer.Ordinal)
        {
            ["products.list"] = p =>
            {
                Read<EmptyPayload>(p);
                return products.ListActive();
            },
            ["draft.load"] = p =>
            {
                Read<EmptyPayload>(p);
                return drafts.GetCurrent();
            },
            ["draft.save"] = p =>
            {
                var req = Read<DraftSavePayload>(p);
                return drafts.Save(req.OrderId, req.Lines.Select(l => new DraftLineInput(l.ProductId, l.Quantity)).ToList());
            },
            ["draft.clear"] = p =>
            {
                var req = Read<DraftClearPayload>(p);
                return new { cleared = drafts.Delete(req.OrderId) };
            },
            ["log.client"] = p =>
            {
                LogClient(Read<ClientLogPayload>(p));
                return new { };
            },

            // --- Phase 1.2 printing ---
            ["printers.list"] = p =>
            {
                Read<EmptyPayload>(p);
                return printing.ListPrinters();
            },
            ["print.destinations.list"] = p =>
            {
                Read<EmptyPayload>(p);
                return printing.ListDestinations();
            },
            ["print.destinations.update"] = p =>
            {
                var req = Read<DestinationUpdatePayload>(p);
                return printing.UpdateDestination(req.Id, req.PrinterName, req.PaperWidthMm);
            },
            ["routing.update"] = p =>
            {
                var req = Read<RoutingUpdatePayload>(p);
                printing.SetProductStation(req.ProductId, req.StationId);
                return new { };
            },
            ["print.receipt"] = p =>
            {
                var req = Read<OrderPrintPayload>(p);
                return printing.EnqueueReceipt(req.OrderId, req.ConfirmDuplicate);
            },
            ["print.tickets"] = p =>
            {
                var req = Read<OrderPrintPayload>(p);
                return printing.EnqueueStationTickets(req.OrderId, req.ConfirmDuplicate);
            },
            ["print.jobs.list"] = p =>
            {
                Read<EmptyPayload>(p);
                return printing.ListJobs();
            },
            ["print.jobs.preview"] = p => printing.Preview(Read<JobPayload>(p).JobId),
            ["print.jobs.retry"] = p => printing.Retry(Read<JobPayload>(p).JobId),
            ["print.jobs.reprint"] = p =>
            {
                var req = Read<ReprintPayload>(p);
                return printing.Reprint(req.JobId, req.ConfirmDuplicate);
            },
        };
    }

    public IReadOnlyCollection<string> Commands => _handlers.Keys;

    /// <summary>Processes one request message and always returns a response message (never throws).</summary>
    public string Handle(string requestJson)
    {
        var started = Stopwatch.GetTimestamp();
        string? id = null;
        var type = "(unknown)";
        try
        {
            if (requestJson is null || requestJson.Length > MaxMessageLength)
            {
                throw new BridgeException("payload_too_large", "Message trop volumineux.");
            }

            using var document = JsonDocument.Parse(requestJson, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new BridgeException("bad_request", "Message invalide.");
            }

            id = ReadId(root);
            foreach (var property in root.EnumerateObject())
            {
                if (!EnvelopeFields.Contains(property.Name))
                {
                    throw new BridgeException("bad_request", "Message invalide.");
                }
            }

            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v != ProtocolVersion)
            {
                throw new BridgeException("unsupported_version", "Version de protocole non prise en charge.");
            }

            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                throw new BridgeException("bad_request", "Message invalide.");
            }

            var requestedType = typeElement.GetString()!;
            if (!_handlers.TryGetValue(requestedType, out var handler))
            {
                throw new BridgeException("unknown_command", "Commande non autorisée.");
            }

            type = requestedType;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                throw new BridgeException("bad_request", "Message invalide.");
            }

            var result = handler(payload);
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            LogRequestCompleted(_logger, type, id, elapsedMs);
            return JsonSerializer.Serialize(new SuccessResponse(id, true, result), JsonOptions);
        }
        catch (Exception ex) when (ex is BridgeException or PosValidationException or JsonException)
        {
            var (code, message) = ex switch
            {
                BridgeException b => (b.Code, b.Message),
                PosValidationException p => (p.Code, p.Message),
                _ => ("bad_request", "Message invalide."),
            };
            LogRequestRejected(_logger, type, id, code, ex.Message);
            return Error(id, code, message);
        }
#pragma warning disable CA1031 // The bridge must always answer; unexpected failures are logged with details and reported generically.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRequestFailed(_logger, ex, type, id);
            return Error(id, "internal_error", "Une erreur interne est survenue. Consultez les journaux du poste.");
        }
    }

    private static string? ReadId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
        {
            throw new BridgeException("bad_request", "Message invalide.");
        }

        var id = idElement.GetString()!;
        return RequestIdPattern().IsMatch(id) ? id : throw new BridgeException("bad_request", "Message invalide.");
    }

    private static T Read<T>(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<T>(JsonOptions) ?? throw new BridgeException("bad_request", "Paramètres invalides.");
        }
        catch (JsonException ex)
        {
            throw new BridgeException("bad_request", "Paramètres invalides.", ex);
        }
    }

    private void LogClient(ClientLogPayload log)
    {
        if (log.Message.Length > MaxLogMessageLength || (log.Detail?.Length ?? 0) > MaxLogDetailLength)
        {
            throw new BridgeException("payload_too_large", "Message de journal trop long.");
        }

        var level = log.Level switch
        {
            "info" => LogLevel.Information,
            "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => throw new BridgeException("bad_request", "Niveau de journal invalide."),
        };
        _uiLogger.Log(level, "UI: {UiMessage} {UiDetail}", log.Message, log.Detail);
    }

    private static string Error(string? id, string code, string message) =>
        JsonSerializer.Serialize(new ErrorResponse(id, false, new ErrorBody(code, message)), JsonOptions);

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Bridge {Command} {RequestId} completed in {ElapsedMs} ms")]
    private static partial void LogRequestCompleted(ILogger logger, string command, string? requestId, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bridge {Command} {RequestId} rejected {ErrorCode}: {Reason}")]
    private static partial void LogRequestRejected(ILogger logger, string command, string? requestId, string errorCode, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bridge {Command} {RequestId} failed")]
    private static partial void LogRequestFailed(ILogger logger, Exception exception, string command, string? requestId);

    private sealed record SuccessResponse(string? Id, bool Ok, object? Result);

    private sealed record ErrorResponse(string? Id, bool Ok, ErrorBody Error);

    private sealed record ErrorBody(string Code, string Message);

    private sealed record EmptyPayload;

    private sealed record DraftSavePayload(string? OrderId, IReadOnlyList<DraftLinePayload> Lines);

    private sealed record DraftLinePayload(string ProductId, int Quantity);

    private sealed record DraftClearPayload(string OrderId);

    private sealed record ClientLogPayload(string Level, string Message, string? Detail = null);

    private sealed record DestinationUpdatePayload(string Id, string? PrinterName, int PaperWidthMm);

    private sealed record RoutingUpdatePayload(string ProductId, string? StationId);

    private sealed record OrderPrintPayload(string OrderId, bool ConfirmDuplicate);

    private sealed record JobPayload(string JobId);

    private sealed record ReprintPayload(string JobId, bool ConfirmDuplicate);
}

public sealed class BridgeException : Exception
{
    public BridgeException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public BridgeException() : this("bad_request", "Message invalide.") { }

    public BridgeException(string message) : this("bad_request", message) { }

    public BridgeException(string message, Exception innerException) : this("bad_request", message, innerException) { }

    public string Code { get; }
}
