using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Pos.Core.Data;

namespace Pos.Core.Gateway;

public sealed record GatewayLink(string GatewayUrl, string CertificateSha256, string BranchId, string BranchName, string TerminalName, string EnrolledAt);

public sealed record PendingOperation(string OperationId, string Type, string Description, string CreatedAt);

/// <summary>
/// Terminal-side persistence for the gateway link (POS SQLite, schema v3).
/// The terminal identity is created once and never changes: a GUID + an ECDSA P-256 key pair whose
/// private key is encrypted with DPAPI for the current Windows user (useless if the file is copied).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TerminalStore(PosDatabase database)
{
    private static readonly byte[] Entropy = "PosSaaS.Terminal.Key.v1"u8.ToArray();

    public string TerminalId => GetOrCreateIdentity().TerminalId;

    public (string TerminalId, byte[] PublicKey) GetOrCreateIdentity()
    {
        using var connection = database.OpenConnection();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT terminal_id, public_key FROM terminal_identity WHERE id = 1;";
            using var reader = query.ExecuteReader();
            if (reader.Read())
            {
                return (reader.GetString(0), (byte[])reader.GetValue(1));
            }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = key.ExportPkcs8PrivateKey();
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var terminalId = Guid.NewGuid().ToString("D");
        using (var insert = connection.CreateCommand())
        {
            // INSERT OR IGNORE + re-read: two racing first starts still end with one identity.
            insert.CommandText = """
                INSERT OR IGNORE INTO terminal_identity (id, terminal_id, private_key, public_key, created_at)
                VALUES (1, $id, $private, $public, $now);
                """;
            insert.Parameters.AddWithValue("$id", terminalId);
            insert.Parameters.AddWithValue("$private", ProtectedData.Protect(privateKey, Entropy, DataProtectionScope.CurrentUser));
            insert.Parameters.AddWithValue("$public", publicKey);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        CryptographicOperations.ZeroMemory(privateKey);
        return GetOrCreateIdentity();
    }

    public byte[] Sign(byte[] payload)
    {
        GetOrCreateIdentity();
        using var connection = database.OpenConnection();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT private_key FROM terminal_identity WHERE id = 1;";
        var privateKey = ProtectedData.Unprotect((byte[])query.ExecuteScalar()!, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(privateKey, out _);
            return key.SignData(payload, HashAlgorithmName.SHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    public GatewayLink? GetLink()
    {
        using var connection = database.OpenConnection();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT gateway_url, cert_sha256, branch_id, branch_name, terminal_name, enrolled_at FROM gateway_link WHERE id = 1;";
        using var reader = query.ExecuteReader();
        return reader.Read()
            ? new GatewayLink(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5))
            : null;
    }

    public void SaveLink(GatewayLink link)
    {
        using var connection = database.OpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO gateway_link (id, gateway_url, cert_sha256, branch_id, branch_name, terminal_name, enrolled_at)
            VALUES (1, $url, $cert, $branch, $branchName, $name, $at);
            """;
        insert.Parameters.AddWithValue("$url", link.GatewayUrl);
        insert.Parameters.AddWithValue("$cert", link.CertificateSha256);
        insert.Parameters.AddWithValue("$branch", link.BranchId);
        insert.Parameters.AddWithValue("$branchName", link.BranchName);
        insert.Parameters.AddWithValue("$name", link.TerminalName);
        insert.Parameters.AddWithValue("$at", link.EnrolledAt);
        insert.ExecuteNonQuery();
    }

    /// <summary>Forgets the link, the identity (new GUID + key at the next pairing) and pending operations. Returns the old terminal id.</summary>
    public string? Unpair()
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction();
        string? previous;
        using (var query = connection.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT terminal_id FROM terminal_identity WHERE id = 1;";
            previous = query.ExecuteScalar() as string;
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM gateway_pending_operations; DELETE FROM gateway_link; DELETE FROM terminal_identity;";
            delete.ExecuteNonQuery();
        }

        tx.Commit();
        return previous;
    }

    public void AddPending(string operationId, string type, string description)
    {
        using var connection = database.OpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO gateway_pending_operations (operation_id, type, description, created_at) VALUES ($id, $type, $desc, $now);
            """;
        insert.Parameters.AddWithValue("$id", operationId);
        insert.Parameters.AddWithValue("$type", type);
        insert.Parameters.AddWithValue("$desc", description);
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        insert.ExecuteNonQuery();
    }

    public void RemovePending(string operationId)
    {
        using var connection = database.OpenConnection();
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM gateway_pending_operations WHERE operation_id = $id;";
        delete.Parameters.AddWithValue("$id", operationId);
        delete.ExecuteNonQuery();
    }

    public IReadOnlyList<PendingOperation> ListPending()
    {
        using var connection = database.OpenConnection();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT operation_id, type, description, created_at FROM gateway_pending_operations ORDER BY created_at;";
        using var reader = query.ExecuteReader();
        var list = new List<PendingOperation>();
        while (reader.Read())
        {
            list.Add(new PendingOperation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return list;
    }
}
