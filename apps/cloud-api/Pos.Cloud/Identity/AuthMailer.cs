using System.Globalization;
using System.Text;

namespace Pos.Cloud.Identity;

/// <summary>Outgoing security mail. Implementations must never log the body: it can contain a one-time link.</summary>
public interface IAuthMailer
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

/// <summary>
/// Writes each message as an RFC 5322 <c>.eml</c> file into a pickup directory (atomically: temporary file, then
/// rename), the drop folder an SMTP relay or a provider agent forwards from. The directory holds live one-time
/// links: it must be readable only by the service and the relay (doc 12). A provider/SMTP adapter replaces this
/// class behind <see cref="IAuthMailer"/> without other changes.
/// </summary>
public sealed partial class PickupDirectoryMailer(string directory, ILogger<PickupDirectoryMailer> logger) : IAuthMailer
{
    public const string From = "PosSaaS <no-reply@possaas.invalid>";

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        if (to.Any(c => c is '\r' or '\n') || subject.Any(c => c is '\r' or '\n'))
        {
            throw new ArgumentException("Header injection.", nameof(to));
        }

        var id = Guid.CreateVersion7();
        var message = new StringBuilder()
            .Append("From: ").Append(From).Append("\r\n")
            .Append("To: ").Append(to).Append("\r\n")
            .Append("Subject: =?utf-8?B?").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(subject))).Append("?=\r\n")
            .Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Message-ID: <").Append(id.ToString("N")).Append("@possaas.invalid>\r\n")
            .Append("MIME-Version: 1.0\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("Content-Transfer-Encoding: 8bit\r\n\r\n")
            .Append(body.ReplaceLineEndings("\r\n"))
            .ToString();

        var final = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{id:N}.eml");
        var temporary = final + ".tmp";
        await File.WriteAllTextAsync(temporary, message, new UTF8Encoding(false), ct);
        File.Move(temporary, final);
        LogQueued(logger, id);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Security mail {MessageId} queued in the pickup directory")]
    private static partial void LogQueued(ILogger logger, Guid messageId);
}
