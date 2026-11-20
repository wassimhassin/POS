using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pos.Cloud.Identity;

/// <summary>
/// Application data-protection keys for secrets the cloud must be able to read back (TOTP seeds). Comes from
/// <c>POS_CLOUD_DATA_KEYS</c> (secret store): <c>id:base64(32 bytes)</c> entries separated by commas; the first is
/// the active key (encrypts), the others only decrypt. Rotation: put a new key first, keep the old ones, run
/// <c>mfa-rekey</c>, then drop the old keys once <c>check-config</c> reports nothing left on them.
/// <para>
/// Blob format (version 1): <c>0x01 | len(id) | id | nonce(12) | tag(16) | ciphertext</c>, AES-256-GCM. The
/// associated data binds the purpose and the owning row (user id) and the header, so a ciphertext copied to
/// another user, or relabelled with another key id, does not decrypt.
/// </para>
/// </summary>
public sealed partial class SecretKeyRing
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly Dictionary<string, byte[]> _keys;

    private SecretKeyRing(string activeKeyId, Dictionary<string, byte[]> keys)
    {
        ActiveKeyId = activeKeyId;
        _keys = keys;
    }

    public string ActiveKeyId { get; }

    public IReadOnlyCollection<string> KeyIds => _keys.Keys;

    public bool Has(string keyId) => _keys.ContainsKey(keyId);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex KeyIdFormat();

    /// <summary>Parses the setting; problems are reported by position, never by value.</summary>
    public static SecretKeyRing? Parse(string? value, string settingName, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{settingName} : variable d'environnement requise (clés de chiffrement « id:base64 », la première est active ; voir « Pos.Cloud data-key-new »).");
            return null;
        }

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string? active = null;
        var before = errors.Count;
        var entries = value.Split(',', StringSplitOptions.TrimEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var separator = entries[i].IndexOf(':', StringComparison.Ordinal);
            var id = separator > 0 ? entries[i][..separator] : "";
            var material = new byte[32];
            if (!KeyIdFormat().IsMatch(id) || !Convert.TryFromBase64String(entries[i][(separator + 1)..], material, out var length) || length != 32
                || entries[i][(separator + 1)..].Length != 44)
            {
                errors.Add($"{settingName} : entrée {i + 1} invalide (attendu id:base64 d'exactement 32 octets ; id en minuscules, chiffres, tirets).");
                continue;
            }

            if (!keys.TryAdd(id, material))
            {
                errors.Add($"{settingName} : entrée {i + 1} : identifiant de clé en double.");
                continue;
            }

            active ??= id;
        }

        return active is null || errors.Count > before ? null : new SecretKeyRing(active, keys);
    }

    /// <summary>A new key entry for an operator to add to the secret store (printed once by the CLI).</summary>
    public static string NewKeyEntry(DateTimeOffset now) =>
        $"k{now:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}:{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose, Guid owner)
    {
        var id = Encoding.ASCII.GetBytes(ActiveKeyId);
        var header = 2 + id.Length;
        var blob = new byte[header + NonceSize + TagSize + plaintext.Length];
        blob[0] = Version;
        blob[1] = (byte)id.Length;
        id.CopyTo(blob, 2);
        var nonce = blob.AsSpan(header, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_keys[ActiveKeyId], TagSize);
        aes.Encrypt(nonce, plaintext, blob.AsSpan(header + NonceSize + TagSize), blob.AsSpan(header + NonceSize, TagSize), Aad(blob.AsSpan(0, header), purpose, owner));
        return blob;
    }

    /// <summary>Decrypts; throws <see cref="CryptographicException"/> for an unknown key, a tampered blob or another owner.</summary>
    public byte[] Unprotect(byte[] blob, string purpose, Guid owner)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var id = KeyIdOf(blob) ?? throw new CryptographicException("Unsupported protected secret format.");
        if (!_keys.TryGetValue(id, out var key))
        {
            throw new CryptographicException("Protected secret uses a key that is not configured.");
        }

        var header = 2 + blob[1];
        var plaintext = new byte[blob.Length - header - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(blob.AsSpan(header, NonceSize), blob.AsSpan(header + NonceSize + TagSize), blob.AsSpan(header + NonceSize, TagSize), plaintext,
            Aad(blob.AsSpan(0, header), purpose, owner));
        return plaintext;
    }

    /// <summary>The key id of a protected blob, or null if it is not a version-1 blob.</summary>
    public static string? KeyIdOf(byte[] blob)
    {
        if (blob is not { Length: > 2 } || blob[0] != Version || blob[1] is 0 or > 32 || blob.Length < 2 + blob[1] + NonceSize + TagSize)
        {
            return null;
        }

        return Encoding.ASCII.GetString(blob, 2, blob[1]);
    }

    private static byte[] Aad(ReadOnlySpan<byte> header, string purpose, Guid owner) =>
        [.. header, .. Encoding.UTF8.GetBytes($"|{purpose}|{owner:D}")];
}
