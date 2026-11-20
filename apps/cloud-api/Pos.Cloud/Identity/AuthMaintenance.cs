using Microsoft.EntityFrameworkCore;

namespace Pos.Cloud.Identity;

/// <summary>TOTP seeds at rest: sealed with the active data key, bound to the user id.</summary>
public static class MfaSecrets
{
    public const string Purpose = "totp";

    public static void Seal(SecretKeyRing keys, UserMfa mfa, byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(mfa);
        mfa.TotpSecret = keys.Protect(seed, Purpose, mfa.UserId);
        mfa.TotpKeyId = keys.ActiveKeyId;
    }

    /// <summary>The plaintext seed (callers zero it after use). Legacy rows (key id null) are plaintext.</summary>
    public static byte[] Open(SecretKeyRing keys, UserMfa mfa)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(mfa);
        return mfa.TotpKeyId is null ? (byte[])mfa.TotpSecret.Clone() : keys.Unprotect(mfa.TotpSecret, Purpose, mfa.UserId);
    }

    /// <summary>Re-seals under the active key unless the row changed meanwhile (a new setup): returns true if written.</summary>
    public static async Task<bool> ResealAsync(CloudDb db, SecretKeyRing keys, UserMfa row, byte[] seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(row);
        var old = row.TotpSecret;
        var sealedSeed = keys.Protect(seed, Purpose, row.UserId);
        var active = keys.ActiveKeyId;
        return await db.UserMfa
            .Where(m => m.UserId == row.UserId && m.TotpSecret == old)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.TotpSecret, sealedSeed).SetProperty(m => m.TotpKeyId, active), ct) == 1;
    }

    public sealed record RekeyResult(int Resealed, int Unreadable, int Remaining);

    /// <summary>
    /// Migrates every seed that is plaintext (pre-2.1C) or under a non-active key, in batches. Seeds under a key that is
    /// no longer configured are counted as unreadable and left untouched (restore the key, then run again).
    /// </summary>
    public static async Task<RekeyResult> RekeyAllAsync(CloudDb db, SecretKeyRing keys, int batchSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(keys);
        var active = keys.ActiveKeyId;
        var readable = keys.KeyIds.ToList();
        int resealed = 0;
        while (true)
        {
            // A re-sealed row no longer matches, so each pass picks up what is left; a pass without progress stops.
            var batch = await db.UserMfa.AsNoTracking()
                .Where(m => m.TotpKeyId == null || (m.TotpKeyId != active && readable.Contains(m.TotpKeyId)))
                .OrderBy(m => m.UserId)
                .Take(batchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            var progress = 0;
            foreach (var row in batch)
            {
                var seed = Open(keys, row);
                try
                {
                    progress += await ResealAsync(db, keys, row, seed, ct) ? 1 : 0;
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
                }
            }

            resealed += progress;
            if (progress == 0)
            {
                break;
            }
        }

        var unreadable = await db.UserMfa.CountAsync(m => m.TotpKeyId != null && !readable.Contains(m.TotpKeyId), ct);
        var remaining = await db.UserMfa.CountAsync(m => m.TotpKeyId == null || m.TotpKeyId != active, ct);
        return new RekeyResult(resealed, unreadable, remaining);
    }
}

/// <summary>
/// Retention of authentication data (doc 12 §4). Runtime role, bounded batches, idempotent (several instances may run
/// it). Never touches a live session, a token that can still be used, or the audit log, which is append-only for the
/// runtime role and pruned only by the operator command <c>audit-prune</c> with the owner role.
/// </summary>
public static class AuthRetention
{
    /// <summary>Ended (revoked or expired) sessions are kept this long for investigations, then deleted.</summary>
    public static readonly TimeSpan EndedSessions = TimeSpan.FromDays(30);

    /// <summary>Expired or consumed single-use tokens (hashes only) are kept this long after expiry.</summary>
    public static readonly TimeSpan Tokens = TimeSpan.FromDays(7);

    /// <summary>Membership invitations (they hold an address) are deleted this long after they expired.</summary>
    public static readonly TimeSpan Invitations = TimeSpan.FromDays(30);

    /// <summary>A TOTP setup never confirmed is abandoned after this long (it holds a secret for nothing).</summary>
    public static readonly TimeSpan AbandonedMfaSetup = TimeSpan.FromDays(1);

    /// <summary>Security audit history: minimum kept, whatever the operator asks (default retention 400 days).</summary>
    public const int MinimumAuditDays = 365;

    public const int DefaultAuditDays = 400;

    public const int BatchSize = 1000;

    public const int MaxBatchesPerRun = 50;

    public sealed record CleanupResult(int Sessions, int Tokens, int AbandonedMfa, bool Complete, int Invitations = 0);

    public static async Task<CleanupResult> CleanupAsync(CloudDb db, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var sessionCutoff = now - EndedSessions;
        var tokenCutoff = now - Tokens;
        var mfaCutoff = now - AbandonedMfaSetup;
        var invitationCutoff = now - Invitations;
        int sessions = 0, tokens = 0, mfa = 0, invitations = 0, batches = 0;
        int deleted;
        do
        {
            // revoked_at < cutoff only matches revoked rows; expires_at < cutoff only expired ones: a live session
            // (not revoked, not expired) can never match.
            deleted = await db.Database.ExecuteSqlAsync($"""
                DELETE FROM user_sessions WHERE id IN (
                    SELECT id FROM user_sessions WHERE revoked_at < {sessionCutoff} OR expires_at < {sessionCutoff} LIMIT {BatchSize})
                """, ct);
            sessions += deleted;
        }
        while (deleted == BatchSize && ++batches < MaxBatchesPerRun);

        do
        {
            deleted = await db.Database.ExecuteSqlAsync($"""
                DELETE FROM auth_tokens WHERE token_hash IN (
                    SELECT token_hash FROM auth_tokens WHERE expires_at < {tokenCutoff} LIMIT {BatchSize})
                """, ct);
            tokens += deleted;
        }
        while (deleted == BatchSize && ++batches < MaxBatchesPerRun);

        do
        {
            deleted = await db.Database.ExecuteSqlAsync($"""
                DELETE FROM user_mfa WHERE user_id IN (
                    SELECT user_id FROM user_mfa WHERE enabled_at IS NULL AND created_at < {mfaCutoff} LIMIT {BatchSize})
                """, ct);
            mfa += deleted;
        }
        while (deleted == BatchSize && ++batches < MaxBatchesPerRun);

        do
        {
            // Accepted or revoked invitations expire within 72 h of creation, so expiry bounds every state.
            // Cross-tenant maintenance through one narrow privileged function (the runtime role sees no tenant rows here).
            deleted = await db.PurgeExpiredInvitationsAsync(invitationCutoff, BatchSize, ct);
            invitations += deleted;
        }
        while (deleted == BatchSize && ++batches < MaxBatchesPerRun);

        return new CleanupResult(sessions, tokens, mfa, batches < MaxBatchesPerRun, invitations);
    }

    /// <summary>Operator command with the owner role: deletes audit rows older than <paramref name="days"/> (≥ 365), in batches.</summary>
    public static async Task<int> PruneAuditAsync(CloudDb ownerDb, DateTimeOffset now, int days, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ownerDb);
        if (days < MinimumAuditDays)
        {
            throw new CliException($"--older-than-days : au moins {MinimumAuditDays} jours (historique de sécurité minimal).");
        }

        var cutoff = now - TimeSpan.FromDays(days);
        int total = 0, deleted;
        do
        {
            deleted = await ownerDb.Database.ExecuteSqlAsync($"""
                DELETE FROM auth_audit_events WHERE id IN (
                    SELECT id FROM auth_audit_events WHERE occurred_at < {cutoff} LIMIT {BatchSize})
                """, ct);
            total += deleted;
        }
        while (deleted == BatchSize);

        return total;
    }
}

/// <summary>Runs <see cref="AuthRetention.CleanupAsync"/> a minute after start, then hourly.</summary>
public sealed partial class AuthMaintenance(IServiceScopeFactory scopes, TimeProvider time, ILogger<AuthMaintenance> logger) : BackgroundService
{
    public static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await AuthRetention.CleanupAsync(scope.ServiceProvider.GetRequiredService<CloudDb>(), time.GetUtcNow(), stoppingToken);
                    LogCleanup(logger, result.Sessions, result.Tokens, result.AbandonedMfa, result.Invitations, result.Complete);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(logger, ex.GetType().Name);
                }

                await Task.Delay(Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Auth cleanup: {Sessions} ended sessions, {Tokens} expired tokens, {Mfa} abandoned MFA setups, {Invitations} old invitations removed (complete: {Complete})")]
    private static partial void LogCleanup(ILogger logger, int sessions, int tokens, int mfa, int invitations, bool complete);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auth cleanup failed ({Failure}); retrying at the next interval")]
    private static partial void LogFailed(ILogger logger, string failure);
}
