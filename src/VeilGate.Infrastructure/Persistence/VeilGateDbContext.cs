using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;

namespace VeilGate.Infrastructure.Persistence;

public sealed class VeilGateDbContext(DbContextOptions<VeilGateDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("citext");
        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Name).HasColumnName("name").HasColumnType("citext").HasMaxLength(120).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.Name).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_tenants_status", "status in ('Active','Suspended')"));
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.Email).HasColumnName("email").HasColumnType("citext").IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(120).IsRequired();
            e.Property(x => x.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(10).IsRequired();
            e.Property(x => x.PasswordHash).HasColumnName("password_hash");
            e.Property(x => x.PasswordChangedAt).HasColumnName("password_changed_at");
            e.Property(x => x.FailedAttempts).HasColumnName("failed_attempts").HasColumnType("smallint").HasDefaultValue((short)0);
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
            e.Property(x => x.TokenVersion).HasColumnName("token_version").HasDefaultValue(1);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            // One account belongs to one tenant, so login can find the user by email alone.
            e.HasIndex(x => x.Email).IsUnique().HasDatabaseName("ux_users_email");
            e.HasIndex(x => new { x.TenantId, x.Role }).HasDatabaseName("ix_users_tenant_role");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_users_role", "role in ('PlatformAdmin','TenantAdmin','SecurityOfficer','Developer','Auditor')");
                t.HasCheckConstraint("ck_users_status", "status in ('Invited','Active','Disabled','Locked')");
                t.HasCheckConstraint("ck_users_failed_attempts", "failed_attempts >= 0");
                t.HasCheckConstraint("ck_users_token_version", "token_version >= 1");
            });
        });
    }
}
