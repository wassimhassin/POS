using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Pos.Cloud.Identity;

/// <summary>
/// A cloud user (owner/manager dashboard). Passwords, lockout counters, security stamp and email uniqueness are
/// ASP.NET Core Identity's. A user has no access to any tenant by itself: access comes only from memberships.
/// </summary>
public sealed class CloudUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Server-side session. The cookie carries only a random 256-bit token; the database stores its SHA-256, so a
/// database leak does not yield usable sessions, and any session can be revoked by its row.
/// </summary>
public sealed class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public byte[] TokenHash { get; set; } = [];

    /// <summary>The token replaced by the last rotation, accepted for a short grace period (concurrent requests).</summary>
    public byte[]? PreviousTokenHash { get; set; }

    /// <summary><see cref="SessionKinds"/>: a full session, or a password-verified session waiting for the second factor.</summary>
    public string Kind { get; set; } = SessionKinds.Full;

    /// <summary>The user's security stamp at creation: a password reset/change or MFA change invalidates the session.</summary>
    public string SecurityStamp { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset RotatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

public static class SessionKinds
{
    public const string Full = "full";
    public const string MfaPending = "mfa_pending";
}

/// <summary>Single-use emailed token (verification, password reset, owner invitation). Only its SHA-256 is stored.</summary>
public sealed class AuthToken
{
    public byte[] TokenHash { get; set; } = [];
    public string Purpose { get; set; } = "";
    public Guid? UserId { get; set; }

    /// <summary>Owner invitation only: the organization the membership is for, and the invited (normalized) address.</summary>
    public Guid? OrganizationId { get; set; }
    public string? NormalizedEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public static class TokenPurposes
{
    public const string VerifyEmail = "verify_email";
    public const string ResetPassword = "reset_password";
    public const string OwnerInvite = "owner_invite";
}

/// <summary>TOTP enrolment. <see cref="EnabledAt"/> is null while the setup is not confirmed with a valid code.</summary>
public sealed class UserMfa
{
    public Guid UserId { get; set; }

    /// <summary>The TOTP seed, encrypted with <see cref="SecretKeyRing"/> (key id in <see cref="TotpKeyId"/>); legacy rows: plaintext, key id null.</summary>
    public byte[] TotpSecret { get; set; } = [];

    /// <summary>Key that encrypts <see cref="TotpSecret"/>; null only for rows written before 2.1C (run <c>mfa-rekey</c>).</summary>
    public string? TotpKeyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EnabledAt { get; set; }

    /// <summary>Last accepted TOTP time step: a code is accepted once (no replay within its validity window).</summary>
    public long LastUsedStep { get; set; }
}

/// <summary>One recovery code (≈80 random bits); only SHA-256 is stored; each code works once.</summary>
public sealed class RecoveryCode
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public byte[] CodeHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>
/// The link between a user and a tenant (organization): one role and one scope (ADR-023). The scope is the whole
/// organization (both ids null), one business, or one branch; composite foreign keys keep it inside the organization.
/// Roles and permissions: <see cref="MembershipRoles"/>, <see cref="Permissions"/>.
/// </summary>
public sealed class OrganizationMembership
{
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = MembershipRoles.Owner;

    /// <summary>Business scope (also set, to the branch's business, for a branch scope); null = organization-wide.</summary>
    public Guid? BusinessId { get; set; }

    /// <summary>Branch scope; null = business- or organization-wide.</summary>
    public Guid? BranchId { get; set; }

    /// <summary>Incremented by every change; clients send it back to change a membership (optimistic concurrency).</summary>
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// An invitation to join an organization with a role and scope. Only the SHA-256 of its token is stored. It is accepted
/// once, before it expires, by a verified account with the invited address and enabled MFA, and only while the inviter
/// still has the authority to grant that role and scope.
/// </summary>
public sealed class MembershipInvitation
{
    public Guid Id { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public Guid OrganizationId { get; set; }
    public string NormalizedEmail { get; set; } = "";
    public string Role { get; set; } = "";
    public Guid? BusinessId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid InvitedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? AcceptedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Append-only security log (no passwords, tokens, codes or email addresses: ids, event and network origin only).</summary>
public sealed class AuthAuditEvent
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Event { get; set; } = "";
    public Guid? UserId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? SessionId { get; set; }

    /// <summary>The user an access-management event is about (member changed, revoked…); the actor is <see cref="UserId"/>.</summary>
    public Guid? TargetUserId { get; set; }

    /// <summary>Non-sensitive facts only (role, scope ids, invitation id, version, denied permission): never addresses or secrets.</summary>
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

internal static class IdentityModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        // Identity's own tables (users only: no role tables until RBAC is designed).
        modelBuilder.Entity<CloudUser>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
        });
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");

        modelBuilder.Entity<UserSession>(e =>
        {
            e.ToTable("user_sessions", t => t.HasCheckConstraint("ck_user_sessions_kind", "kind IN ('full', 'mfa_pending')"));
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.PreviousTokenHash);
            e.HasIndex(x => new { x.UserId, x.RevokedAt });
            e.HasIndex(x => x.ExpiresAt); // retention cleanup
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.SecurityStamp).HasMaxLength(100);
            e.Property(x => x.RevokeReason).HasMaxLength(40);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(200);
            e.HasOne<CloudUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthToken>(e =>
        {
            e.ToTable("auth_tokens", t =>
            {
                t.HasCheckConstraint("ck_auth_tokens_purpose", "purpose IN ('verify_email', 'reset_password', 'owner_invite')");
                t.HasCheckConstraint("ck_auth_tokens_invite", "purpose <> 'owner_invite' OR (organization_id IS NOT NULL AND normalized_email IS NOT NULL)");
            });
            e.HasKey(x => x.TokenHash);
            e.HasIndex(x => new { x.UserId, x.Purpose, x.CreatedAt });
            e.HasIndex(x => x.ExpiresAt); // retention cleanup
            e.Property(x => x.Purpose).HasMaxLength(20);
            e.Property(x => x.NormalizedEmail).HasMaxLength(256);
            e.HasOne<CloudUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserMfa>(e =>
        {
            e.ToTable("user_mfa");
            e.HasKey(x => x.UserId);
            e.Property(x => x.TotpKeyId).HasMaxLength(32);
            e.HasIndex(x => x.TotpKeyId);
            e.HasOne<CloudUser>().WithOne().HasForeignKey<UserMfa>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecoveryCode>(e =>
        {
            e.ToTable("user_recovery_codes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.HasOne<CloudUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrganizationMembership>(e =>
        {
            e.ToTable("organization_memberships", t =>
            {
                t.HasCheckConstraint("ck_organization_memberships_role", MembershipRoles.CheckSql);
                ScopeConstraints(t, "organization_memberships");
            });
            e.HasKey(x => new { x.OrganizationId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Role).HasMaxLength(20);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CloudUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            ScopeKeys(e);
        });

        modelBuilder.Entity<MembershipInvitation>(e =>
        {
            e.ToTable("membership_invitations", t =>
            {
                t.HasCheckConstraint("ck_membership_invitations_role", MembershipRoles.CheckSql);
                ScopeConstraints(t, "membership_invitations");
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.NormalizedEmail });
            e.HasIndex(x => x.ExpiresAt); // retention cleanup
            e.Property(x => x.NormalizedEmail).HasMaxLength(256);
            e.Property(x => x.Role).HasMaxLength(20);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CloudUser>().WithMany().HasForeignKey(x => x.InvitedBy).OnDelete(DeleteBehavior.Restrict);
            ScopeKeys(e);
        });

        modelBuilder.Entity<AuthAuditEvent>(e =>
        {
            // No foreign keys: the security log outlives the rows it mentions.
            e.ToTable("auth_audit_events");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.OccurredAt });
            e.HasIndex(x => x.OccurredAt);
            e.Property(x => x.Event).HasMaxLength(40);
            e.Property(x => x.Details).HasMaxLength(200);
            e.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(200);
        });
    }

    /// <summary>A branch scope names its business; owners and admins are always organization-wide.</summary>
    private static void ScopeConstraints(Microsoft.EntityFrameworkCore.Metadata.Builders.TableBuilder t, string table)
    {
        t.HasCheckConstraint($"ck_{table}_scope", "branch_id IS NULL OR business_id IS NOT NULL");
        t.HasCheckConstraint($"ck_{table}_org_wide_roles", "role NOT IN ('owner', 'admin') OR business_id IS NULL");
    }

    /// <summary>Composite keys: a scope can only name a business or branch of the same organization.</summary>
    private static void ScopeKeys<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e) where T : class
    {
        e.HasOne<Business>().WithMany()
            .HasForeignKey("OrganizationId", "BusinessId")
            .HasPrincipalKey("OrganizationId", "Id")
            .OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Branch>().WithMany()
            .HasForeignKey("OrganizationId", "BusinessId", "BranchId")
            .HasPrincipalKey("OrganizationId", "BusinessId", "Id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
