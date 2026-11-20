using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Pos.Cloud.Tests;

/// <summary>
/// Minimal loopback SMTP server for tests (no mail leaves the machine): EHLO, AUTH LOGIN, MAIL, RCPT, DATA, RSET, QUIT.
/// Failures can be injected per transaction (reply code at MAIL FROM) to exercise the cloud's retry policy.
/// </summary>
public sealed partial class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private readonly ConcurrentQueue<string> _failures = new();

    public FakeSmtpServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptAsync);
    }

    public sealed record Received(string From, IReadOnlyList<string> To, string Data, string? User, string? Password)
    {
        /// <summary>The decoded text body (SmtpClient sends it base64-encoded, as configured by the cloud).</summary>
        public string Body
        {
            get
            {
                var split = Data.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var raw = Data[(split + 4)..].Replace("\r\n", "", StringComparison.Ordinal);
                return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            }
        }
    }

    public int Port { get; }

    /// <summary>Delay before the 220 greeting of each connection (a slow relay).</summary>
    public TimeSpan GreetingDelay { get; set; }

    public ConcurrentQueue<Received> Messages { get; } = new();

    /// <summary>Every MAIL FROM seen (one per delivery attempt), including rejected ones.</summary>
    public int Attempts => _attempts;

    private int _attempts;

    /// <summary>The next transactions are rejected at MAIL FROM with these replies, in order (e.g. "451 4.3.0 busy").</summary>
    public void FailNext(params string[] replies)
    {
        foreach (var reply in replies)
        {
            _failures.Enqueue(reply);
        }
    }

    public string? TokenFor(string email, string linkPath)
    {
        foreach (var message in Messages.Reverse())
        {
            if (message.To.Contains(email, StringComparer.OrdinalIgnoreCase))
            {
                var match = Regex.Match(message.Body, Regex.Escape($"{CloudProcess.PublicOrigin}{linkPath}#token=") + "([A-Za-z0-9_-]+)");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }

        return null;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => SessionAsync(client));
        }
    }

    private async Task SessionAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                if (GreetingDelay > TimeSpan.Zero)
                {
                    await Task.Delay(GreetingDelay, _stop.Token);
                }

                await writer.WriteLineAsync("220 fake.test ESMTP");
                string from = "";
                var to = new List<string>();
                string? user = null, password = null;
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO":
                            await writer.WriteLineAsync("250-fake.test");
                            await writer.WriteLineAsync("250-AUTH LOGIN");
                            await writer.WriteLineAsync("250 8BITMIME");
                            break;
                        case "HELO":
                            await writer.WriteLineAsync("250 fake.test");
                            break;
                        case "AUTH":
                            var parts = line.Split(' ');
                            if (parts.Length >= 3)
                            {
                                user = Decode(parts[2]);
                            }
                            else
                            {
                                await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                                user = Decode(await reader.ReadLineAsync(_stop.Token) ?? "");
                            }

                            await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                            password = Decode(await reader.ReadLineAsync(_stop.Token) ?? "");
                            await writer.WriteLineAsync("235 2.7.0 ok");
                            break;
                        case "MAIL":
                            Interlocked.Increment(ref _attempts);
                            if (_failures.TryDequeue(out var failure))
                            {
                                await writer.WriteLineAsync(failure);
                                break;
                            }

                            from = line;
                            to.Clear();
                            await writer.WriteLineAsync("250 2.1.0 ok");
                            break;
                        case "RCPT":
                            to.Add(Address().Match(line).Groups[1].Value);
                            await writer.WriteLineAsync("250 2.1.5 ok");
                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 go ahead");
                            var data = new StringBuilder();
                            while (await reader.ReadLineAsync(_stop.Token) is { } dataLine && dataLine != ".")
                            {
                                data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine).Append("\r\n");
                            }

                            Messages.Enqueue(new Received(from, [.. to], data.ToString().TrimEnd('\r', '\n'), user, password));
                            await writer.WriteLineAsync("250 2.0.0 queued");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 bye");
                            return;
                        default:
                            await writer.WriteLineAsync("250 ok");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
    }

    private static string Decode(string base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return base64;
        }
    }

    [GeneratedRegex("<([^>]+)>")]
    private static partial Regex Address();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accept;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}
