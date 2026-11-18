using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pos.Cloud;

// Ownership: Organization → Business → Branch. Every branch-owned row carries all three ids and has a
// composite foreign key to branches (organization_id, business_id, id), so a row can never claim a branch
// of another business or organization, even through an application bug.

public sealed class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Business
{
    public Guid OrganizationId { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Branch
{
    public Guid OrganizationId { get; set; }
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A registered branch gateway: its ECDSA P-256 public key. No shared secret is stored.</summary>
public sealed class GatewayCredential
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BusinessId { get; set; }
    public Guid BranchId { get; set; }
    public byte[] PublicKey { get; set; } = [];
    public string PublicKeySha256 { get; set; } = "";
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

/// <summary>One-time gateway provisioning code (only its SHA-256 is stored), bound to one branch.</summary>
public sealed class ProvisioningCode
{
    public string CodeHash { get; set; } = "";
    public Guid OrganizationId { get; set; }
    public Guid BusinessId { get; set; }
    public Guid BranchId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByGatewayId { get; set; }
}

/// <summary>Per-branch sync checkpoint: highest contiguous accepted sequence. Row-locked while a batch is processed.</summary>
public sealed class BranchSyncState
{
    public Guid OrganizationId { get; set; }
    public Guid BusinessId { get; set; }
    public Guid BranchId { get; set; }
    public long LastSequence { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
}

/// <summary>Accepted branch event (append-only). Unique per (branch, sequence) and per (branch, event id).</summary>
public sealed class SyncEventRecord
{
    public Guid OrganizationId { get; set; }
    public Guid BusinessId { get; set; }
    public Guid BranchId { get; set; }
    public long Sequence { get; set; }
    public Guid EventId { get; set; }
    public string Type { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string EventHash { get; set; } = "";
    public string Payload { get; set; } = "";
    public Guid GatewayId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>Projection of the branch's authoritative order state (written only from accepted events).</summary>
public sealed class BranchOrder
{
    public Guid OrganizationId { get; set; }
    public Guid BusinessId { get; set; }
    public Guid BranchId { get; set; }
    public Guid Id { get; set; }
    public int Number { get; set; }
    public string? Label { get; set; }
    public string Status { get; set; } = "";
    public long Version { get; set; }
    public string Currency { get; set; } = "";
    public int ItemCount { get; set; }
    public long TotalMinor { get; set; }
    public string CreatedByName { get; set; } = "";
    public string UpdatedByName { get; set; } = "";
    public string? SubmittedByName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public long LastSequence { get; set; }
    public List<BranchOrderLine> Lines { get; set; } = [];
}

public sealed class BranchOrderLine
{
    public Guid BranchId { get; set; }
    public Guid OrderId { get; set; }
    public string ProductId { get; set; } = "";
    public int LineNo { get; set; }
    public string Name { get; set; } = "";
    public long UnitPriceMinor { get; set; }
    public int Quantity { get; set; }
    public long LineTotalMinor { get; set; }
    public string? StationId { get; set; }
}

/// <summary>Branch catalog as published by its gateway (one-way, reporting only; no cloud → branch catalog sync).</summary>
public sealed class BranchProduct
{
    public Guid OrganizationId { get; set; }
    public Guid BusinessId { get; set; }
    public Guid BranchId { get; set; }
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public long PriceMinor { get; set; }
    public string Currency { get; set; } = "";
    public string? StationId { get; set; }
    public bool IsActive { get; set; }
    public long LastSequence { get; set; }
}

/// <summary>Read-only reporting credential scoped to an organization, a business or a branch.</summary>
public sealed class ReportKey
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? BusinessId { get; set; }
    public Guid? BranchId { get; set; }
    public string Name { get; set; } = "";
    public string SecretSha256 { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class CloudDb(DbContextOptions<CloudDb> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<GatewayCredential> GatewayCredentials => Set<GatewayCredential>();
    public DbSet<ProvisioningCode> ProvisioningCodes => Set<ProvisioningCode>();
    public DbSet<BranchSyncState> BranchSyncStates => Set<BranchSyncState>();
    public DbSet<SyncEventRecord> SyncEvents => Set<SyncEventRecord>();
    public DbSet<BranchOrder> Orders => Set<BranchOrder>();
    public DbSet<BranchOrderLine> OrderLines => Set<BranchOrderLine>();
    public DbSet<BranchProduct> BranchProducts => Set<BranchProduct>();
    public DbSet<ReportKey> ReportKeys => Set<ReportKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Organization>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120);
        });

        modelBuilder.Entity<Business>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Branch>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.OrganizationId, x.BusinessId, x.Id });
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasIndex(x => new { x.BusinessId, x.Name }).IsUnique();
            e.HasOne<Business>().WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.BusinessId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<GatewayCredential>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PublicKeySha256).IsUnique();
            e.HasIndex(x => x.BranchId);
            e.Property(x => x.PublicKeySha256).HasMaxLength(64);
            BranchOwned(e);
        });

        modelBuilder.Entity<ProvisioningCode>(e =>
        {
            e.HasKey(x => x.CodeHash);
            e.Property(x => x.CodeHash).HasMaxLength(64);
            BranchOwned(e);
        });

        modelBuilder.Entity<BranchSyncState>(e =>
        {
            e.HasKey(x => x.BranchId);
            e.ToTable(t => t.HasCheckConstraint("ck_branch_sync_states_last_sequence", "last_sequence >= 0"));
            BranchOwned(e);
        });

        modelBuilder.Entity<SyncEventRecord>(e =>
        {
            e.HasKey(x => new { x.BranchId, x.Sequence });
            e.HasIndex(x => new { x.BranchId, x.EventId }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.BusinessId, x.BranchId, x.ReceivedAt });
            e.Property(x => x.Type).HasMaxLength(40);
            e.Property(x => x.EventHash).HasMaxLength(64);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.ToTable(t => t.HasCheckConstraint("ck_sync_events_sequence", "sequence >= 1"));
            BranchOwned(e);
            e.HasOne<GatewayCredential>().WithMany().HasForeignKey(x => x.GatewayId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BranchOrder>(e =>
        {
            e.ToTable("orders", t =>
            {
                t.HasCheckConstraint("ck_orders_status", "status IN ('open', 'submitted')");
                t.HasCheckConstraint("ck_orders_version", "version >= 1");
                t.HasCheckConstraint("ck_orders_total", "total_minor >= 0");
            });
            e.HasKey(x => new { x.BranchId, x.Id });
            e.HasIndex(x => new { x.BranchId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.BusinessId, x.BranchId, x.Status });
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Label).HasMaxLength(60);
            e.HasMany(x => x.Lines).WithOne()
                .HasForeignKey(x => new { x.BranchId, x.OrderId })
                .HasPrincipalKey(x => new { x.BranchId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
            BranchOwned(e);
        });

        modelBuilder.Entity<BranchOrderLine>(e =>
        {
            e.ToTable("order_lines", t => t.HasCheckConstraint("ck_order_lines_quantity", "quantity BETWEEN 1 AND 999"));
            e.HasKey(x => new { x.BranchId, x.OrderId, x.ProductId });
            e.Property(x => x.ProductId).HasMaxLength(64);
        });

        modelBuilder.Entity<BranchProduct>(e =>
        {
            e.HasKey(x => new { x.BranchId, x.ProductId });
            e.Property(x => x.ProductId).HasMaxLength(64);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            BranchOwned(e);
        });

        modelBuilder.Entity<ReportKey>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SecretSha256).HasMaxLength(64);
            e.ToTable(t => t.HasCheckConstraint("ck_report_keys_scope", "branch_id IS NULL OR business_id IS NOT NULL"));
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            // Composite FKs with a NULL component are not checked (MATCH SIMPLE): enforced only when scoped.
            e.HasOne<Business>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.BusinessId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.BusinessId, x.BranchId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.BusinessId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        ApplySnakeCase(modelBuilder);
    }

    private static void BranchOwned<T>(EntityTypeBuilder<T> e) where T : class =>
        e.HasOne<Branch>().WithMany()
            .HasForeignKey("OrganizationId", "BusinessId", "BranchId")
            .HasPrincipalKey("OrganizationId", "BusinessId", "Id")
            .OnDelete(DeleteBehavior.Restrict);

    private static void ApplySnakeCase(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            entity.SetTableName(Snake(entity.GetTableName()!));
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(Snake(property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(Snake(key.GetName()!));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName(Snake(foreignKey.GetConstraintName()!));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(Snake(index.GetDatabaseName()!));
            }
        }
    }

    internal static string Snake(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}

/// <summary>Used only by « dotnet ef migrations add » (no database connection is opened).</summary>
public sealed class CloudDbDesignTimeFactory : IDesignTimeDbContextFactory<CloudDb>
{
    public CloudDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CloudDb>().UseNpgsql("Host=localhost;Database=design_time_only").Options);
}
