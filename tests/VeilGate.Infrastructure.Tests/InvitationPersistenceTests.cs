using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class InvitationPersistenceTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private VeilGateDbContext AppContext(Guid tenantId)
    {
        var tenant = new TenantContext();
        tenant.Set(tenantId);
        return new VeilGateDbContext(new DbContextOptionsBuilder<VeilGateDbContext>()
            .UseNpgsql(pg.AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenant))
            .Options);
    }

    // A context with NO tenant set, like the accept-invitation page before we know who it is.
    private VeilGateDbContext NoTenantContext() =>
        new(new DbContextOptionsBuilder<VeilGateDbContext>()
            .UseNpgsql(pg.AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(new TenantContext()))
            .Options);

    private async Task<(Tenant t, User admin, User invited)> SeedPeople()
    {
        var s = Guid.NewGuid().ToString("N")[..8];
        var t = Tenant.Create($"Acme {s}").Value;
        var admin = User.Invite(t.Id, $"admin-{s}@acme.test", "Admin", UserRole.TenantAdmin, Now).Value;
        var invited = User.Invite(t.Id, $"new-{s}@acme.test", "New Person", UserRole.Developer, Now).Value;

        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        db.AddRange(admin, invited);
        await db.SaveChangesAsync();
        return (t, admin, invited);
    }

    private async Task<NewInvitation> SaveInvitation(Tenant t, User admin, User invited)
    {
        var made = Invitation.Create(t.Id, invited.Id, invited.Email, UserRole.Developer, admin.Id, Now).Value;
        await using var db = AppContext(t.Id);
        db.Invitations.Add(made.Invitation);
        await db.SaveChangesAsync();
        return made;
    }

    [Fact]
    public async Task A_saved_invitation_is_found_by_the_hash_of_its_token()
    {
        var (t, admin, invited) = await SeedPeople();
        var made = await SaveInvitation(t, admin, invited);

        await using var db = AppContext(t.Id);
        var found = await db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == RefreshTokens.Hash(made.Token));

        found.Should().NotBeNull();
        found!.UserId.Should().Be(invited.Id);
        found.Role.Should().Be(UserRole.Developer);
        found.ExpiresAt.Should().BeCloseTo(Now.AddHours(48), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_invitation()
    {
        var (t, admin, invited) = await SeedPeople();
        var made = await SaveInvitation(t, admin, invited);
        var (other, _, _) = await SeedPeople();

        await using var db = AppContext(other.Id);

        (await db.Invitations.AnyAsync(i => i.TokenHash == RefreshTokens.Hash(made.Token))).Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_tenant_the_lookup_still_finds_the_owner_of_a_token()
    {
        var (t, admin, invited) = await SeedPeople();
        var made = await SaveInvitation(t, admin, invited);
        var hash = RefreshTokens.Hash(made.Token);

        await using var db = NoTenantContext();
        var rows = await db.Database
            .SqlQuery<Guid?>($"select auth_find_invitation_tenant({hash}) as \"Value\"")
            .ToListAsync();

        rows.Single().Should().Be(t.Id);
        (await db.Invitations.AnyAsync(i => i.TokenHash == hash)).Should().BeFalse();   // RLS still hides the row itself
    }

    [Fact]
    public async Task The_lookup_returns_nothing_for_an_unknown_token()
    {
        await using var db = NoTenantContext();
        var hash = RefreshTokens.Hash(RefreshTokens.Generate());

        var rows = await db.Database
            .SqlQuery<Guid?>($"select auth_find_invitation_tenant({hash}) as \"Value\"")
            .ToListAsync();

        rows.Single().Should().BeNull();
    }

        [Fact]
    public async Task The_repository_finds_by_token_hash_and_lists_a_users_invitations()
    {
        var (t, admin, invited) = await SeedPeople();
        var made = await SaveInvitation(t, admin, invited);
        var hash = RefreshTokens.Hash(made.Token);

        await using (var noTenant = NoTenantContext())
            (await new InvitationRepository(noTenant).FindTenantByTokenHashAsync(hash, default)).Should().Be(t.Id);

        await using var db = AppContext(t.Id);
        var repo = new InvitationRepository(db);
        (await repo.FindByTokenHashAsync(hash, default))!.Id.Should().Be(made.Invitation.Id);
        (await repo.ListForUserAsync(invited.Id, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_tenant_reads_only_its_own_name()
    {
        var (t, _, _) = await SeedPeople();

        await using var db = AppContext(t.Id);

        (await new UserRepository(db).GetTenantNameAsync(default)).Should().Be(t.Name);
    }
}