using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading.Channels;

namespace Pos.Cloud.Identity;

/// <summary>Outgoing security mail. Implementations must never log the body (one-time links) or the recipient.</summary>
public interface IAuthMailer
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

public sealed record OutgoingMail(Guid Id, string To, string Subject, string Body);

/// <summary>SMTP relay settings. TLS is STARTTLS (submission port 587); plain SMTP only to a loopback relay.</summary>
public sealed record SmtpSettings(string Host, int Port, bool StartTls, string? User, string? Password);

/// <summary>Exactly one transport: a pickup directory (development, or a local relay agent) or an SMTP relay.</summary>
public sealed record MailSettings(string From, string? PickupDirectory, SmtpSettings? Smtp)
{
    public const string DefaultFrom = "PosSaaS <no-reply@possaas.invalid>";

    public string Transport => Smtp is null ? "pickup" : "smtp";
}

/// <summary>One delivery attempt. Throws on failure; <see cref="MailDelivery.IsTransient"/> decides on retries.</summary>
public interface IMailTransport
{
    string Name { get; }

    Task DeliverAsync(OutgoingMail mail, CancellationToken ct);
}

/// <summary>Writes RFC 5322 <c>.eml</c> files atomically (temporary file, then rename) into a pickup directory.</summary>
public sealed class PickupDirectoryTransport(string directory, string from) : IMailTransport
{
    public string Name => "pickup";

    public async Task DeliverAsync(OutgoingMail mail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mail);
        var message = new StringBuilder()
            .Append("From: ").Append(from).Append("\r\n")
            .Append("To: ").Append(mail.To).Append("\r\n")
            .Append("Subject: =?utf-8?B?").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(mail.Subject))).Append("?=\r\n")
            .Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Message-ID: <").Append(mail.Id.ToString("N")).Append("@possaas.invalid>\r\n")
            .Append("MIME-Version: 1.0\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("Content-Transfer-Encoding: 8bit\r\n\r\n")
            .Append(mail.Body.ReplaceLineEndings("\r\n"))
            .ToString();

        var final = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{mail.Id:N}.eml");
        var temporary = final + ".tmp";
        await File.WriteAllTextAsync(temporary, message, new UTF8Encoding(false), ct);
        File.Move(temporary, final);
    }
}

/// <summary>
/// SMTP submission through <see cref="SmtpClient"/> (no extra dependency): STARTTLS with certificate validation by
/// the operating system, optional AUTH. Implicit TLS (port 465) is not supported by this client and is refused at
/// configuration time.
/// </summary>
public sealed class SmtpTransport(SmtpSettings settings, string from) : IMailTransport
{
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

    public string Name => "smtp";

    public async Task DeliverAsync(OutgoingMail mail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mail);
        using var message = new MailMessage(MailAddressFrom(from), new MailAddress(mail.To))
        {
            Subject = mail.Subject,
            SubjectEncoding = Encoding.UTF8,
            Body = mail.Body.ReplaceLineEndings("\r\n"),
            BodyEncoding = Encoding.UTF8,
            BodyTransferEncoding = TransferEncoding.Base64,
            IsBodyHtml = false,
        };
        message.Headers.Add("Message-ID", $"<{mail.Id:N}@possaas.invalid>");
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.StartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = settings.User is null ? null : new NetworkCredential(settings.User, settings.Password),
            Timeout = (int)AttemptTimeout.TotalMilliseconds,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AttemptTimeout);
        await client.SendMailAsync(message, timeout.Token);
    }

    private static MailAddress MailAddressFrom(string value) => new(value);
}

/// <summary>Delivery with bounded retries (transient failures only); logs ids and failure classes, never content.</summary>
public sealed partial class MailDelivery(IMailTransport transport, ILogger<MailDelivery> logger)
{
    public const int MaxAttempts = 5;

    /// <summary>Backoff before attempt n+1: 2, 4, 8, 16 s (about 30 s in total, then the mail is dropped).</summary>
    public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    /// <summary>Returns true if delivered.</summary>
    public async Task<bool> DeliverAsync(OutgoingMail mail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mail);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await transport.DeliverAsync(mail, ct);
                LogDelivered(logger, mail.Id, transport.Name, attempt);
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
            {
                var failure = Describe(ex);
                if (!IsTransient(ex))
                {
                    LogPermanent(logger, mail.Id, transport.Name, attempt, failure);
                    return false;
                }

                if (attempt >= MaxAttempts)
                {
                    LogGaveUp(logger, mail.Id, transport.Name, attempt, failure);
                    return false;
                }

                var delay = Backoff(attempt);
                LogRetry(logger, mail.Id, transport.Name, attempt, failure, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>4xx SMTP replies, network and timeout failures are retried; 5xx replies, TLS and address errors are not.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        SmtpFailedRecipientsException many => many.InnerExceptions.All(e => IsTransientStatus(e.StatusCode)),
        SmtpException smtp when smtp.InnerException is AuthenticationException => false,
        SmtpException smtp when smtp.InnerException is IOException or SocketException or TimeoutException => true,
        SmtpException smtp => IsTransientStatus(smtp.StatusCode),
        IOException or SocketException or TimeoutException or OperationCanceledException => true,
        _ => false,
    };

    private static bool IsTransientStatus(SmtpStatusCode status) =>
        status is SmtpStatusCode.ServiceNotAvailable or SmtpStatusCode.MailboxBusy or SmtpStatusCode.LocalErrorInProcessing
            or SmtpStatusCode.InsufficientStorage or SmtpStatusCode.GeneralFailure;

    // Exception messages can quote server replies (addresses): only the class and the SMTP status are logged.
    private static string Describe(Exception ex) => ex switch
    {
        SmtpException smtp => $"{ex.GetType().Name} {(int)smtp.StatusCode} {smtp.StatusCode}{(smtp.InnerException is null ? "" : " / " + smtp.InnerException.GetType().Name)}",
        _ => ex.GetType().Name,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Security mail {MessageId} delivered via {Transport} (attempt {Attempt})")]
    private static partial void LogDelivered(ILogger logger, Guid messageId, string transport, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security mail {MessageId}: attempt {Attempt} via {Transport} failed ({Failure}); retrying in {DelaySeconds} s")]
    private static partial void LogRetry(ILogger logger, Guid messageId, string transport, int attempt, string failure, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Security mail {MessageId} not delivered via {Transport}: permanent failure at attempt {Attempt} ({Failure})")]
    private static partial void LogPermanent(ILogger logger, Guid messageId, string transport, int attempt, string failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "Security mail {MessageId} not delivered via {Transport}: gave up after {Attempt} attempts ({Failure})")]
    private static partial void LogGaveUp(ILogger logger, Guid messageId, string transport, int attempt, string failure);
}

/// <summary>
/// Request path: validates and enqueues in memory, so a response never waits for (or reveals, by its timing) a mail
/// delivery. Bounded; a full queue drops the mail with an error log. Mails still queued at a crash are lost: every
/// flow lets the user ask again (resend, forgot), and invitations are reissued by the operator (doc 12).
/// </summary>
public sealed partial class QueuedMailer(ILogger<QueuedMailer> logger) : IAuthMailer
{
    public const int Capacity = 1000;

    private readonly Channel<OutgoingMail> _queue = Channel.CreateBounded<OutgoingMail>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait, // TryWrite then fails instead of blocking
    });

    public ChannelReader<OutgoingMail> Reader => _queue.Reader;

    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var mail = Mails.Create(to, subject, body);
        if (!_queue.Writer.TryWrite(mail))
        {
            LogDropped(logger, mail.Id);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Security mail {MessageId} dropped: outgoing queue full")]
    private static partial void LogDropped(ILogger logger, Guid messageId);
}

/// <summary>Single background sender for <see cref="QueuedMailer"/> (one at a time, in order).</summary>
public sealed class MailDispatcher(QueuedMailer queue, MailDelivery delivery) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var mail in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await delivery.DeliverAsync(mail, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: whatever is still queued is lost (see QueuedMailer).
        }
    }
}

/// <summary>CLI path (no host running): deliver now, with the same retries, and fail loudly.</summary>
public sealed class DirectMailer(MailDelivery delivery) : IAuthMailer
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        if (!await delivery.DeliverAsync(Mails.Create(to, subject, body), ct))
        {
            throw new CliException("E-mail non remis (voir le journal) : relancez la commande une fois le relais de messagerie disponible.");
        }
    }
}

internal static class Mails
{
    public static OutgoingMail Create(string to, string subject, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(body);
        if (to.Any(char.IsControl) || subject.Any(char.IsControl))
        {
            throw new ArgumentException("Header injection.", nameof(to));
        }

        return new OutgoingMail(Guid.CreateVersion7(), to, subject, body);
    }
}
