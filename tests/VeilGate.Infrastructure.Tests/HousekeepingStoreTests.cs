using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Domain.Vault;
using VeilGate.Infrastructure.Housekeeping;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class HousekeepingStoreTests(PostgresFixture pg)
{
    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    // The housekeeping workers have no tenant, so these run as the app role with none set.
    private PostgresHousekeepingStore Store() =>
        new(new VeilGateDbContext(new DbContextOptionsBuilder<VeilGateDbContext>().UseNpgsql(pg.AppConnection).Options));

    private async Task<(Tenant Tenant, User User)> SeedUser()
    {
        var s = Guid.NewGuid().ToString("N")[..8];
        var t = Tenant.Create($"Housekeep {s}").Value;
        var u = User.Invite(t.Id, $"alice-{s}@acme.test", "Alice", UserRole.TenantAdmin, DateTimeOffset.UtcNow).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        db.Add(u);
        await db.SaveChangesAsync();
        return (t, u);
    }

    // ------------------------------------------------------------------ vault

    private async Task<Guid> SeedVaultSession(Guid tenantId, TimeSpan expiresIn, bool withToken = true)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new VaultSession { TenantId = tenantId, ClientId = Guid.NewGuid(), CreatedAt = now.AddHours(-2), ExpiresAt = now + expiresIn };
        await using var db = pg.AdminContext();
        db.VaultSessions.Add(session);
        if (withToken)
            db.VaultTokens.Add(new VaultToken
            {
                TenantId = tenantId, SessionId = session.Id, Token = "[[PERSON_ABCD]]", EntityType = "PERSON", Ciphertext = [1, 2, 3],
                Nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12), Tag = new byte[16], DekVersion = 1,
            });
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task<(bool Session, int Tokens)> VaultState(Guid sessionId)
    {
        await using var db = pg.AdminContext();
        return (await db.VaultSessions.AnyAsync(s => s.Id == sessionId), await db.VaultTokens.CountAsync(t => t.SessionId == sessionId));
    }

    [Fact]
    public async Task An_expired_vault_session_is_removed_together_with_its_tokens()
    {
        var (t, _) = await SeedUser();
        var expired = await SeedVaultSession(t.Id, TimeSpan.FromMinutes(-5));

        var removed = await Store().PurgeExpiredVaultAsync();

        removed.Should().BeGreaterThanOrEqualTo(1);
        (await VaultState(expired)).Should().Be((false, 0));
    }

    [Fact]
    public async Task A_vault_session_that_has_not_expired_is_left_alone()
    {
        var (t, _) = await SeedUser();
        var live = await SeedVaultSession(t.Id, TimeSpan.FromMinutes(30));

        await Store().PurgeExpiredVaultAsync();

        (await VaultState(live)).Should().Be((true, 1));
    }

    [Fact]
    public async Task Vault_sessions_of_every_tenant_are_purged_without_a_tenant_set()
    {
        var (a, _) = await SeedUser();
        var (b, _) = await SeedUser();
        var first = await SeedVaultSession(a.Id, TimeSpan.FromMinutes(-1));
        var second = await SeedVaultSession(b.Id, TimeSpan.FromMinutes(-1));

        await Store().PurgeExpiredVaultAsync();

        (await VaultState(first)).Session.Should().BeFalse();
        (await VaultState(second)).Session.Should().BeFalse();
    }

    // --------------------------------------------------------------- sessions

    /// <summary>Starts a session and then moves its times, as if it had been created and used in the past.</summary>
    private async Task<Guid> SeedSession(Tenant t, User u, TimeSpan createdAgo, TimeSpan lastSeenAgo, TimeSpan expiresIn, TimeSpan? revokedAgo = null)
    {
        var now = DateTimeOffset.UtcNow;
        var session = Session.Start(t.Id, u.Id, RefreshTokens.Hash(RefreshTokens.Generate()), now).Value;
        await using var db = pg.AdminContext();
        db.Add(session);
        await db.SaveChangesAsync();

        var created = now - createdAgo;
        var lastSeen = now - lastSeenAgo;
        var expires = now + expiresIn;
        await db.Database.ExecuteSqlAsync($"UPDATE sessions SET created_at = {created}, last_seen_at = {lastSeen}, expires_at = {expires} WHERE id = {session.Id}");
        if (revokedAgo is { } ago)
        {
            var revoked = now - ago;
            await db.Database.ExecuteSqlAsync($"UPDATE sessions SET revoked_at = {revoked}, revoke_reason = 'test' WHERE id = {session.Id}");
        }
        return session.Id;
    }

    private async Task<bool> Exists(Guid sessionId)
    {
        await using var db = pg.AdminContext();
        return await db.Sessions.AnyAsync(s => s.Id == sessionId);
    }

    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    [Fact]
    public async Task A_session_past_its_hard_limit_is_removed_once_the_grace_period_is_over()
    {
        var (t, u) = await SeedUser();
        var longGone = await SeedSession(t, u, Week, TimeSpan.FromHours(1), TimeSpan.FromDays(-3));
        var justEnded = await SeedSession(t, u, Week, TimeSpan.FromHours(1), TimeSpan.FromHours(-1));

        var removed = await Store().ReapSessionsAsync(Session.IdleTimeout, Grace);

        removed.Should().BeGreaterThanOrEqualTo(1);
        (await Exists(longGone)).Should().BeFalse();
        (await Exists(justEnded)).Should().BeTrue();   // still inside the grace period, so a replayed token is still recognised
    }

    [Fact]
    public async Task A_revoked_session_is_removed_once_the_grace_period_is_over()
    {
        var (t, u) = await SeedUser();
        var longRevoked = await SeedSession(t, u, TimeSpan.FromDays(5), TimeSpan.FromDays(4), TimeSpan.FromDays(2), revokedAgo: TimeSpan.FromDays(3));
        var justRevoked = await SeedSession(t, u, TimeSpan.FromDays(5), TimeSpan.FromHours(2), TimeSpan.FromDays(2), revokedAgo: TimeSpan.FromHours(1));

        await Store().ReapSessionsAsync(Session.IdleTimeout, Grace);

        (await Exists(longRevoked)).Should().BeFalse();
        (await Exists(justRevoked)).Should().BeTrue();
    }

    [Fact]
    public async Task A_session_that_was_idle_for_too_long_is_removed()
    {
        var (t, u) = await SeedUser();
        // Last seen 4 days ago: the 8-hour idle timeout ended it about 3 days and 16 hours ago, though its hard limit is still ahead.
        var idle = await SeedSession(t, u, TimeSpan.FromDays(5), TimeSpan.FromDays(4), TimeSpan.FromDays(2));

        await Store().ReapSessionsAsync(Session.IdleTimeout, Grace);

        (await Exists(idle)).Should().BeFalse();
    }

    [Fact]
    public async Task A_session_in_use_is_never_removed()
    {
        var (t, u) = await SeedUser();
        var active = await SeedSession(t, u, TimeSpan.FromDays(1), TimeSpan.FromMinutes(5), TimeSpan.FromDays(6));

        await Store().ReapSessionsAsync(Session.IdleTimeout, Grace);

        (await Exists(active)).Should().BeTrue();
    }

    [Fact]
    public async Task A_session_that_ended_a_moment_ago_by_idleness_is_kept_through_the_grace_period()
    {
        var (t, u) = await SeedUser();
        var recentlyIdle = await SeedSession(t, u, TimeSpan.FromDays(1), TimeSpan.FromHours(9), TimeSpan.FromDays(6));   // idle-ended 1 hour ago

        await Store().ReapSessionsAsync(Session.IdleTimeout, Grace);

        (await Exists(recentlyIdle)).Should().BeTrue();
    }
}
