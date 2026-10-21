using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;

namespace VeilGate.Infrastructure.Persistence;

public sealed class VeilGateDbContext(DbContextOptions<VeilGateDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

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
    }
}
