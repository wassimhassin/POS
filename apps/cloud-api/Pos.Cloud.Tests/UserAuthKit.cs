using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pos.Sync;

namespace Pos.Cloud.Tests;

/// <summary>
/// A browser-like cloud user client: its own cookie jar and its own loopback source address (127.0.0.x), so rate
/// limits of one test never affect another.
/// </summary>
public sealed partial class UserClient : IDisposable
{
    private static int s_nextHost = 20;
    private readonly CloudProcess _cloud;

    public UserClient(CloudProcess cloud)
    {
        _cloud = cloud;
        var host = 20 + (Interlocked.Increment(ref s_nextHost) % 230);
        Source = IPAddress.Parse($"127.0.0.{host}");
        Http = cloud.Http(source: Source, cookies: Cookies);
    }

    public IPAddress Source { get; }

    public CookieContainer Cookies { get; } = new();

    public HttpClient Http { get; }

    public const string CookieName = "__Host-pos_session";

    /// <summary>The current session cookie value (null when signed out).</summary>
    public string? SessionToken => Cookies.GetCookies(new Uri(_cloud.Url))[CookieName]?.Value is { Length: > 0 } v ? v : null;

    /// <summary>Puts a raw cookie value in the jar (replaying a stolen or old token).</summary>
    public void SetSessionToken(string token) => Cookies.Add(new Uri(_cloud.Url), new Cookie(CookieName, token, "/") { Secure = true, HttpOnly = true });

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: SyncProtocol.Json),
        };
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => Http.GetAsync(path);

    public Task<HttpResponseMessage> DeleteAsync(string path) => Http.DeleteAsync(path);

    public void Dispose() => Http.Dispose();
}

public static partial class Auth
{
    public const string Password = "correct horse battery staple";
    public const string OtherPassword = "another long passphrase 42";

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Asserts the CloudError envelope and its code.</summary>
    public static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var json = await JsonAsync(response, status);
        Assert.Equal(["code", "message"], json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(code, json.GetProperty("code").GetString());
    }

    public static string NewEmail(string prefix) => $"{prefix}.{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 13, 40)] + "@example.tn";

    /// <summary>
    /// The token in the newest mail to <paramref name="email"/> whose link path is <paramref name="linkPath"/>. Mails are
    /// delivered by a background dispatcher: waits up to 20 s for one to appear.
    /// </summary>
    public static string? LatestToken(CloudProcess cloud, string email, string linkPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var token = FindToken(cloud, email, linkPath);
            if (token is not null || DateTime.UtcNow > deadline)
            {
                return token;
            }

            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Waits until every mail queued before this call has been delivered: the dispatcher is a single FIFO sender, so
    /// once a mail requested now (fresh address, own client) has arrived, everything earlier has too.
    /// </summary>
    public static async Task MailBarrierAsync(CloudProcess cloud)
    {
        using var probe = new UserClient(cloud);
        var email = NewEmail("barrier");
        await JsonAsync(await probe.PostAsync("v1/auth/register", new { email, password = Password }), HttpStatusCode.Accepted);
        Assert.NotNull(LatestToken(cloud, email, "/auth/verify-email"));
    }

    private static string? FindToken(CloudProcess cloud, string email, string linkPath)
    {
        foreach (var file in Directory.GetFiles(cloud.MailDirectory, "*.eml").OrderByDescending(f => f, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            if (text.Contains($"To: {email}\r\n", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(text, Regex.Escape($"{CloudProcess.PublicOrigin}{linkPath}#token=") + "([A-Za-z0-9_-]+)");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }

        return null;
    }

    public static int MailCount(CloudProcess cloud, string email) =>
        Directory.GetFiles(cloud.MailDirectory, "*.eml").Count(f => File.ReadAllText(f).Contains($"To: {email}\r\n", StringComparison.OrdinalIgnoreCase));

    public static async Task RegisterVerifiedAsync(CloudProcess cloud, UserClient client, string email, string password = Password)
    {
        await JsonAsync(await client.PostAsync("v1/auth/register", new { email, password }), HttpStatusCode.Accepted);
        var token = LatestToken(cloud, email, "/auth/verify-email") ?? throw new InvalidOperationException("No verification mail.");
        await JsonAsync(await client.PostAsync("v1/auth/verify-email", new { token }), HttpStatusCode.OK);
    }

    public static async Task<string> LoginAsync(UserClient client, string email, string password = Password)
    {
        var json = await JsonAsync(await client.PostAsync("v1/auth/login", new { email, password }), HttpStatusCode.OK);
        return json.GetProperty("status").GetString()!;
    }

    // ---------------------------------------------------------------- TOTP (independent RFC 6238 implementation)

    public static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }

    public static long CurrentStep => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

    public static string Totp(byte[] secret, long step)
    {
        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
#pragma warning disable CA5350 // RFC 6238 test vector algorithm
        var mac = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        var offset = mac[^1] & 0x0F;
        var value = (BinaryPrimitives.ReadInt32BigEndian(mac.AsSpan(offset)) & 0x7FFFFFFF) % 1_000_000;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A code the server has not accepted yet: steps are accepted once and only within ±1 step, so after using
    /// <paramref name="lastUsed"/> the next usable one may require waiting for the clock.
    /// </summary>
    public static async Task<(string Code, long Step)> NextTotpAsync(byte[] secret, long lastUsed)
    {
        while (true)
        {
            var current = CurrentStep;
            // The server accepts current±1; a future step only gets "more current" with time, so current+1 is safe.
            var step = Math.Max(lastUsed + 1, current);
            if (step <= current + 1)
            {
                return (Totp(secret, step), step);
            }

            await Task.Delay(500);
        }
    }

    /// <summary>Starts and confirms TOTP; returns the secret, the step used and the recovery codes.</summary>
    public static async Task<(byte[] Secret, long Step, List<string> RecoveryCodes)> EnableTotpAsync(UserClient client)
    {
        var setup = await JsonAsync(await client.PostAsync("v1/me/mfa/totp"), HttpStatusCode.OK);
        var secret = Base32Decode(setup.GetProperty("sharedKey").GetString()!);
        Assert.StartsWith("otpauth://totp/PosSaaS:", setup.GetProperty("otpAuthUri").GetString(), StringComparison.Ordinal);
        var (code, step) = await NextTotpAsync(secret, 0);
        var codes = await JsonAsync(await client.PostAsync("v1/me/mfa/totp/confirm", new { code }), HttpStatusCode.OK);
        return (secret, step, [.. codes.GetProperty("codes").EnumerateArray().Select(c => c.GetString()!)]);
    }

    [GeneratedRegex("^[A-Z2-7]{4}-[A-Z2-7]{4}-[A-Z2-7]{4}-[A-Z2-7]{4}$")]
    public static partial Regex RecoveryCodeFormat();

    public static string Utf8(byte[] data) => Encoding.UTF8.GetString(data);
}
