using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Domain.Vault;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using VeilGate.Domain.Audit;

namespace VeilGate.Infrastructure.Persistence;

public sealed class VeilGateDbContext(DbContextOptions<VeilGateDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
        public DbSet<MfaFactor> MfaFactors => Set<MfaFactor>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
        public DbSet<Session> Sessions => Set<Session>();
    public DbSet<VaultSession> VaultSessions => Set<VaultSession>();
    public DbSet<VaultToken> VaultTokens => Set<VaultToken>();
    public DbSet<TenantKey> TenantKeys => Set<TenantKey>();
        public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

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

                b.Entity<MfaFactor>(e =>
        {
            e.ToTable("mfa_factors");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            e.Property(x => x.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.ProtectedSecret).HasColumnName("protected_secret").IsRequired();
            e.Property(x => x.IsConfirmed).HasColumnName("is_confirmed");
            e.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at");
            e.Property(x => x.LastUsedCounter).HasColumnName("last_used_counter").HasDefaultValue(-1L);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

            // A user has at most one factor of each type.
            e.HasIndex(x => new { x.UserId, x.Type }).IsUnique().HasDatabaseName("ux_mfa_factors_user_type");
            e.ToTable(t => t.HasCheckConstraint("ck_mfa_factors_type", "type in ('Totp')"));
        });
    
            b.Entity<RecoveryCode>(e =>
        {
            e.ToTable("recovery_codes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            e.Property(x => x.CodeHash).HasColumnName("code_hash").HasMaxLength(64).IsRequired();
            e.Property(x => x.UsedAt).HasColumnName("used_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Ignore(x => x.IsUsed);   // computed from UsedAt, not a column

            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

            // The same code can't be stored twice for one user, and looking up by hash stays fast.
            e.HasIndex(x => new { x.UserId, x.CodeHash }).IsUnique().HasDatabaseName("ux_recovery_codes_user_hash");
            e.ToTable(t => t.HasCheckConstraint("ck_recovery_codes_hash", "length(code_hash) = 64"));
        });
    
            b.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            e.Property(x => x.CurrentRefreshHash).HasColumnName("current_refresh_hash").HasMaxLength(64).IsRequired();
            e.Property(x => x.PreviousRefreshHash).HasColumnName("previous_refresh_hash").HasMaxLength(64);
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.RevokeReason).HasColumnName("revoke_reason").HasMaxLength(32);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

            // Refresh looks a session up by the token's hash, so both hashes need an index.
            e.HasIndex(x => x.CurrentRefreshHash).IsUnique().HasDatabaseName("ux_sessions_current_hash");
            e.HasIndex(x => x.PreviousRefreshHash).HasDatabaseName("ix_sessions_previous_hash");
            // "Sign out everywhere" finds all of one user's sessions.
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_sessions_user");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_sessions_current_hash", "length(current_refresh_hash) = 64");
                t.HasCheckConstraint("ck_sessions_expiry", "expires_at > created_at");
            });
        });

        b.Entity<VaultSession>(e =>
        {
            e.ToTable("vault_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.ClientId).HasColumnName("client_id").IsRequired();   // no FK yet: api_clients arrives in M4
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");

            // These live only in memory for the duration of one request. They are never saved.
            e.Ignore(x => x.DateShiftKey);
            e.Ignore(x => x.SubjectId);
            e.Ignore(x => x.DateShiftSeed);
            e.Ignore(x => x.Tokens);

            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            // Lets vault_tokens point at (tenant_id, id), so a token can never reference another tenant's session.
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_vault_sessions_expires");
            e.ToTable(t => t.HasCheckConstraint("ck_vault_sessions_ttl", "expires_at <= created_at + interval '24 hours'"));
        });

        b.Entity<VaultToken>(e =>
        {
            e.ToTable("vault_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.SessionId).HasColumnName("session_id").IsRequired();
            e.Property(x => x.Token).HasColumnName("token").HasMaxLength(40).IsRequired();
            e.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(40).IsRequired();
            e.Property(x => x.Ciphertext).HasColumnName("ciphertext").IsRequired();
            e.Property(x => x.Nonce).HasColumnName("nonce").IsRequired();
            e.Property(x => x.Tag).HasColumnName("tag").IsRequired();
            e.Property(x => x.DekVersion).HasColumnName("dek_version");

            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            // The one cascade in the whole schema: vault data is temporary by design (Database doc 3.3).
            e.HasOne<VaultSession>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.SessionId })
                .HasPrincipalKey(s => new { s.TenantId, s.Id })
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.SessionId, x.Token }).IsUnique().HasDatabaseName("ux_vault_tokens_session_token");
            // Reusing a nonce under the same key breaks AES-GCM. The database refuses it.
            e.HasIndex(x => new { x.TenantId, x.DekVersion, x.Nonce }).IsUnique().HasDatabaseName("ux_vault_tokens_nonce");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_vault_tokens_nonce", "length(nonce) = 12");
                t.HasCheckConstraint("ck_vault_tokens_tag", "length(tag) = 16");
            });
        });

        b.Entity<TenantKey>(e =>
        {
            e.ToTable("tenant_keys");
            e.HasKey(x => new { x.TenantId, x.DekVersion });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.DekVersion).HasColumnName("dek_version");
            e.Property(x => x.WrappedDek).HasColumnName("wrapped_dek").IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(10).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_tenant_keys_status", "status in ('Active','Retired')"));
        });
                b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.Seq).HasColumnName("seq");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.ActorType).HasColumnName("actor_type").HasMaxLength(10).IsRequired();
            e.Property(x => x.ActorId).HasColumnName("actor_id");
            e.Property(x => x.Action).HasColumnName("action").HasMaxLength(60).IsRequired();
            e.Property(x => x.ResourceType).HasColumnName("resource_type").HasMaxLength(40);
            e.Property(x => x.ResourceId).HasColumnName("resource_id");
            e.Property(x => x.Result).HasColumnName("result").HasMaxLength(10).IsRequired();
            e.Property(x => x.Ip).HasColumnName("ip").HasColumnType("inet")
                .HasConversion(v => IPAddress.Parse(v), v => v.ToString());   // text in our code, inet in the database
            e.Property(x => x.Details).HasColumnName("details").HasColumnType("jsonb").IsRequired()
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new Dictionary<string, string>(),
                    new ValueComparer<IReadOnlyDictionary<string, string>>(
                        (x, y) => x!.Count == y!.Count && !x.Except(y).Any(),
                        v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                        v => new Dictionary<string, string>(v)));
            e.Property(x => x.PrevHash).HasColumnName("prev_hash").HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.Hash).HasColumnName("hash").HasMaxLength(64).IsFixedLength().IsRequired();

            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

            // Chain order and cursor paging. Gap-free numbering per tenant is enforced by this unique index.
            e.HasIndex(x => new { x.TenantId, x.Seq }).IsUnique().HasDatabaseName("ux_audit_events_tenant_seq");
            e.HasIndex(x => new { x.TenantId, x.OccurredAt }).IsDescending(false, true).HasDatabaseName("ix_audit_events_time");
            e.HasIndex(x => new { x.TenantId, x.ActorId, x.OccurredAt }).IsDescending(false, false, true).HasDatabaseName("ix_audit_events_actor");
            e.HasIndex(x => new { x.TenantId, x.Action, x.OccurredAt }).IsDescending(false, false, true).HasDatabaseName("ix_audit_events_action");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_audit_events_seq", "seq >= 1");
                t.HasCheckConstraint("ck_audit_events_hash", "length(hash) = 64 and length(prev_hash) = 64");
                t.HasCheckConstraint("ck_audit_events_actor", "actor_type in ('user','client','system')");
                t.HasCheckConstraint("ck_audit_events_result", "result in ('success','failure','denied')");
            });
        });
    }
}
