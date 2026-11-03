using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeilGate.Domain;
using VeilGate.Domain.Messaging;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class OutboxFunctionTests(PostgresFixture pg)
{
    // The publisher has no tenant, so these run as the app role with no tenant set.
    private VeilGateDbContext PublisherContext() =>
        new(new DbContextOptionsBuilder<VeilGateDbContext>().UseNpgsql(pg.AppConnection).Options);

    private async Task<Guid> SeedTenant()
    {
        var t = Tenant.Create($"OutboxFn {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private async Task<Guid> SeedMessage(Guid tenantId, DateTimeOffset? dueAt = null)
    {
        var m = OutboxMessage.Create(tenantId, dueAt ?? DateTimeOffset.UtcNow.AddSeconds(-5), OutboxTopics.GatewayRequests, """{"requestId":"req_1"}""").Value;
        await using var db = pg.AdminContext();
        db.OutboxMessages.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    }

    private async Task<OutboxMessage> Load(Guid id)
    {
        await using var db = pg.AdminContext();
        return await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<List<Guid>> Claim(int limit = 100, int leaseSeconds = 60)
    {
        await using var db = PublisherContext();
        return await db.Database.SqlQuery<Guid>($"select msg_id as \"Value\" from outbox_claim_due({limit}, {leaseSeconds})").ToListAsync();
    }

    private async Task Run(FormattableString sql)
    {
        await using var db = PublisherContext();
        await db.Database.ExecuteSqlAsync(sql);
    }

    [Fact]
    public async Task Claiming_counts_the_attempt_and_hides_the_message_while_it_is_leased()
    {
        var id = await SeedMessage(await SeedTenant());

        (await Claim()).Should().Contain(id);
        (await Load(id)).Attempts.Should().Be(1);

        (await Claim()).Should().NotContain(id);   // leased: another publisher does not get it too
    }

    [Fact]
    public async Task A_message_that_is_not_due_yet_is_not_claimed()
    {
        var id = await SeedMessage(await SeedTenant(), DateTimeOffset.UtcNow.AddHours(1));

        (await Claim()).Should().NotContain(id);
        (await Load(id)).Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Messages_of_every_tenant_are_claimed_without_a_tenant_set()
    {
        var a = await SeedMessage(await SeedTenant());
        var b = await SeedMessage(await SeedTenant());

        (await Claim()).Should().Contain([a, b]);
    }

    [Fact]
    public async Task Claiming_never_returns_more_than_the_limit()
    {
        var tenant = await SeedTenant();
        await SeedMessage(tenant);
        await SeedMessage(tenant);
        await SeedMessage(tenant);

        (await Claim(limit: 2)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_message_is_claimed_again_when_its_lease_runs_out()
    {
        var id = await SeedMessage(await SeedTenant());

        (await Claim(leaseSeconds: 0)).Should().Contain(id);   // a publisher that claimed it and then crashed
        (await Claim(leaseSeconds: 0)).Should().Contain(id);

        (await Load(id)).Attempts.Should().Be(2);
    }

    [Fact]
    public async Task A_sent_message_is_marked_and_never_claimed_again()
    {
        var id = await SeedMessage(await SeedTenant());
        await Claim(leaseSeconds: 0);

        await Run($"select outbox_mark_sent({id})");

        var m = await Load(id);
        m.SentAt.Should().NotBeNull();
        m.LastErrorCode.Should().BeNull();
        (await Claim(leaseSeconds: 0)).Should().NotContain(id);
    }

    [Fact]
    public async Task A_failed_message_records_the_error_and_waits_before_the_next_try()
    {
        var id = await SeedMessage(await SeedTenant());
        await Claim();   // attempts = 1, so the wait is 2 seconds

        await Run($"select outbox_mark_failed({id}, 'transport.Local_MsgTimedOut', 300)");

        var m = await Load(id);
        m.LastErrorCode.Should().Be("transport.Local_MsgTimedOut");
        m.SentAt.Should().BeNull();
        m.NextAttemptAt.Should().BeAfter(DateTimeOffset.UtcNow).And.BeBefore(DateTimeOffset.UtcNow.AddSeconds(10));
    }

    [Fact]
    public async Task The_wait_between_tries_is_capped()
    {
        var id = await SeedMessage(await SeedTenant());
        for (var i = 0; i < 4; i++) await Claim(leaseSeconds: 0);   // attempts = 4, so 2^4 = 16 seconds without a cap

        await Run($"select outbox_mark_failed({id}, 'transport.x', 3)");

        (await Load(id)).NextAttemptAt.Should().BeBefore(DateTimeOffset.UtcNow.AddSeconds(6));
    }

    [Fact]
    public async Task A_dead_message_is_finished_but_keeps_its_error()
    {
        var id = await SeedMessage(await SeedTenant());
        await Claim(leaseSeconds: 0);

        await Run($"select outbox_mark_dead({id}, 'transport.Local_MsgTimedOut')");

        var m = await Load(id);
        m.SentAt.Should().NotBeNull();
        m.LastErrorCode.Should().Be("transport.Local_MsgTimedOut");
        (await Claim(leaseSeconds: 0)).Should().NotContain(id);
    }

    [Fact]
    public async Task The_app_role_still_cannot_update_rows_directly()
    {
        await SeedMessage(await SeedTenant());

        await using var db = PublisherContext();
        var update = () => db.Database.ExecuteSqlRawAsync("update outbox_messages set attempts = 0");
        await update.Should().ThrowAsync<PostgresException>().Where(e => e.SqlState == "42501");
    }
}
