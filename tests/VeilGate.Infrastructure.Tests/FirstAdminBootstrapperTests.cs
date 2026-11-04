using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;
using VeilGate.Infrastructure.Bootstrap;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>
/// "The first organisation" only makes sense on an EMPTY database, and the shared test database is never empty, so every test gets a
/// brand new database inside the same container, with the real migrations applied.
/// </summary>
[Collection("postgres")]
public class FirstAdminBootstrapperTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private async Task<string> EmptyDatabase()
    {
        var name = $"bootstrap_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(pg.AdminConnection))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(pg.AdminConnection) { Database = name, Pooling = false }.ConnectionString;
        await using var db = Context(connectionString);
        await db.Database.MigrateAsync();
        return connectionString;
    }

    private static VeilGateDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<VeilGateDbContext>().UseNpgsql(connectionString).Options);

    private static async Task<Result<BootstrapOutcome>> Run(string connectionString, string organisation = "Acme Health", string email = "Admin@Example.com", string name = "Alex Admin")
    {
        await using var db = Context(connectionString);
        return await new FirstAdminBootstrapper(db, new FixedClock()).RunAsync(organisation, email, name);
    }

    private static async Task<Result<BootstrapOutcome>> Reissue(string connectionString)
    {
        await using var db = Context(connectionString);
        return await new FirstAdminBootstrapper(db, new FixedClock()).ReissueAsync();
    }

    // -------------------------------------------------------------------------- the first run

    [Fact]
    public async Task It_creates_the_organisation_and_an_administrator_who_still_has_to_sign_up()
    {
        var cs = await EmptyDatabase();

        var result = await Run(cs);

        result.IsSuccess.Should().BeTrue();
        await using var db = Context(cs);
        var tenant = await db.Tenants.SingleAsync();
        tenant.Name.Should().Be("Acme Health");
        var admin = await db.Users.SingleAsync();
        admin.TenantId.Should().Be(tenant.Id);
        admin.Role.Should().Be(UserRole.TenantAdmin);
        admin.Status.Should().Be(UserStatus.Invited);          // no password yet: they choose one from the link
        admin.PasswordHash.Should().BeNull();
        admin.Email.Should().Be("admin@example.com");          // spelled the one canonical way
    }

    [Fact]
    public async Task The_administrator_gets_one_ordinary_invitation_that_works_once_for_48_hours()
    {
        var cs = await EmptyDatabase();

        var result = (await Run(cs)).Value;

        await using var db = Context(cs);
        var invitation = await db.Invitations.SingleAsync();
        invitation.UserId.Should().Be(result.UserId);
        invitation.Role.Should().Be(UserRole.TenantAdmin);
        invitation.IsUsable(Now).Should().BeTrue();
        invitation.ExpiresAt.Should().Be(Now.AddHours(48));
        result.ExpiresAt.Should().Be(Now.AddHours(48));
        invitation.IsUsable(Now.AddHours(48)).Should().BeFalse();   // and it stops working on time
        invitation.Accept(Now).IsSuccess.Should().BeTrue();
        invitation.IsUsable(Now).Should().BeFalse();                 // once used, never again
    }

    [Fact]
    public async Task Only_a_fingerprint_of_the_link_token_is_stored()
    {
        var cs = await EmptyDatabase();

        var result = (await Run(cs)).Value;

        await using var db = Context(cs);
        var invitation = await db.Invitations.SingleAsync();
        invitation.TokenHash.Should().Be(RefreshTokens.Hash(result.InvitationToken));
        invitation.TokenHash.Should().NotBe(result.InvitationToken);
        (await db.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from invitations where token_hash = {result.InvitationToken}").SingleAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_organisations_audit_log_starts_with_a_bootstrap_event_that_has_a_valid_hash()
    {
        var cs = await EmptyDatabase();

        var result = (await Run(cs)).Value;

        await using var db = Context(cs);
        var audit = await db.AuditEvents.SingleAsync();
        audit.TenantId.Should().Be(result.TenantId);
        audit.Seq.Should().Be(1);
        audit.PrevHash.Should().Be(AuditEvent.Genesis);
        audit.ActorType.Should().Be("system");
        audit.Action.Should().Be("tenant.bootstrap");
        audit.Result.Should().Be("success");
        AuditHasher.Compute(audit).Should().Be(audit.Hash);   // a later "check for tampering" will accept it
    }

    // ------------------------------------------------------------------ it works only once

    [Fact]
    public async Task A_second_run_is_refused_and_changes_nothing()
    {
        var cs = await EmptyDatabase();
        await Run(cs);

        var second = await Run(cs, organisation: "Another Org", email: "other@example.com");

        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be("bootstrap.already_done");
        await using var db = Context(cs);
        (await db.Tenants.CountAsync()).Should().Be(1);
        (await db.Users.CountAsync()).Should().Be(1);
        (await db.Invitations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task It_is_refused_when_an_organisation_already_exists_even_if_it_was_made_another_way()
    {
        var cs = await EmptyDatabase();
        await using (var db = Context(cs)) { db.Tenants.Add(Tenant.Create("Existing Org").Value); await db.SaveChangesAsync(); }

        var result = await Run(cs);

        result.Error.Code.Should().Be("bootstrap.already_done");
    }

    [Fact]
    public async Task Two_operators_at_the_same_moment_make_exactly_one_organisation()
    {
        var cs = await EmptyDatabase();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Run(cs, organisation: $"Race Org {i}", email: $"admin{i}@example.com")));

        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Where(r => r.IsFailure).Should().OnlyContain(r => r.Error.Code == "bootstrap.already_done");
        await using var db = Context(cs);
        (await db.Tenants.CountAsync()).Should().Be(1);
    }

    // ----------------------------------------------------------------------- bad input

    [Theory]
    [InlineData("ab", "admin@example.com", "Alex")]          // organisation name too short
    [InlineData("Acme Health", "not-an-email", "Alex")]
    [InlineData("Acme Health", "admin@example.com", "")]
    public async Task Bad_input_is_refused_and_nothing_is_created(string organisation, string email, string name)
    {
        var cs = await EmptyDatabase();

        var result = await Run(cs, organisation, email, name);

        result.IsFailure.Should().BeTrue();
        await using var db = Context(cs);
        (await db.Tenants.CountAsync()).Should().Be(0);
        (await db.Users.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------------- a lost link

    [Fact]
    public async Task A_lost_link_can_be_replaced_and_the_old_one_stops_working()
    {
        var cs = await EmptyDatabase();
        var first = (await Run(cs)).Value;

        var second = (await Reissue(cs)).Value;

        second.InvitationToken.Should().NotBe(first.InvitationToken);
        second.UserId.Should().Be(first.UserId);
        await using var db = Context(cs);
        var invitations = await db.Invitations.ToListAsync();
        invitations.Should().HaveCount(2);
        invitations.Single(i => i.TokenHash == RefreshTokens.Hash(first.InvitationToken)).IsUsable(Now.AddSeconds(1)).Should().BeFalse();
        invitations.Single(i => i.TokenHash == RefreshTokens.Hash(second.InvitationToken)).IsUsable(Now.AddSeconds(1)).Should().BeTrue();
        (await db.Users.CountAsync()).Should().Be(1);   // still one person: nobody was added
    }

    [Fact]
    public async Task A_new_link_is_refused_once_the_administrator_has_signed_up()
    {
        var cs = await EmptyDatabase();
        await Run(cs);
        await using (var db = Context(cs))
        {
            var admin = await db.Users.SingleAsync();
            admin.AcceptInvitation("$argon2id$not-a-real-hash", Now).IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync();
        }

        var result = await Reissue(cs);

        result.Error.Code.Should().Be("bootstrap.nothing_to_reissue");
    }

    [Fact]
    public async Task A_new_link_is_refused_when_nothing_was_bootstrapped()
    {
        var cs = await EmptyDatabase();

        (await Reissue(cs)).Error.Code.Should().Be("bootstrap.nothing_to_reissue");
    }

    [Fact]
    public async Task A_new_link_is_refused_when_there_is_more_than_one_organisation()
    {
        var cs = await EmptyDatabase();
        await Run(cs);
        await using (var db = Context(cs)) { db.Tenants.Add(Tenant.Create("Second Org").Value); await db.SaveChangesAsync(); }

        (await Reissue(cs)).Error.Code.Should().Be("bootstrap.nothing_to_reissue");
    }
}
