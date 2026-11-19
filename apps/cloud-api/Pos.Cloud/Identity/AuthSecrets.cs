using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pos.Cloud.Identity;

/// <summary>Random tokens, their hashes, recovery codes and TOTP (RFC 6238: HMAC-SHA1, 6 digits, 30 s).</summary>
public static class AuthSecrets
{
    public const int TotpDigits = 6;
    public const int TotpPeriodSeconds = 30;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>256 random bits, base64url (cookie-safe, URL-safe).</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 of a high-entropy token: a slow hash adds nothing at 256 bits.</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>16 base32 characters (80 bits), shown as XXXX-XXXX-XXXX-XXXX.</summary>
    public static string NewRecoveryCode()
    {
        var code = Base32(RandomNumberGenerator.GetBytes(10));
        return string.Join('-', code.Chunk(4).Select(c => new string(c)));
    }

    /// <summary>Case, spaces and dashes do not matter when a code is typed back.</summary>
    public static byte[] HashRecoveryCode(string code) =>
        Hash(new string([.. code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]));

    public static byte[] NewTotpSecret() => RandomNumberGenerator.GetBytes(20);

    public static string Base32(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    public static long TimeStep(DateTimeOffset now) => now.ToUnixTimeSeconds() / TotpPeriodSeconds;

    public static string TotpCode(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> mac = stackalloc byte[20];
#pragma warning disable CA5350 // RFC 6238 / authenticator apps use HMAC-SHA1; its use here is not a collision-resistance concern.
        HMACSHA1.HashData(secret, counter, mac);
#pragma warning restore CA5350
        var offset = mac[^1] & 0x0F;
        var value = (BinaryPrimitives.ReadInt32BigEndian(mac[offset..]) & 0x7FFFFFFF) % 1_000_000;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The matching step within ±1 step of <paramref name="now"/> that is newer than <paramref name="lastUsedStep"/>
    /// (a code is never accepted twice), or null. Comparison is constant-time per candidate.
    /// </summary>
    public static long? VerifyTotp(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        if (code is null || code.Length != TotpDigits || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = TimeStep(now);
        long? match = null;
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (step > lastUsedStep && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(TotpCode(secret, step)), Encoding.ASCII.GetBytes(code)))
            {
                match ??= step;
            }
        }

        return match;
    }

    public static string OtpAuthUri(string issuer, string account, ReadOnlySpan<byte> secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={Base32(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={TotpDigits}&period={TotpPeriodSeconds}";

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
