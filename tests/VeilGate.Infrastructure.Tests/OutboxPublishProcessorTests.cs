using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Common;
using VeilGate.Domain.Messaging;
using VeilGate.Infrastructure.Messaging;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class OutboxPublishProcessorTests(PostgresFixture pg)
{
    private sealed class Rig(OutboxPublishProcessor processor, InMemoryEventTransport transport)
    {
        public OutboxPublishProcessor Processor { get; } = processor;
        public InMemoryEventTransport Transport { get; } = transport;
    }

    private Rig Build(int maxAttempts = 5)
    {
        var services = new ServiceCollection();
        services.AddDbContext<VeilGateDbContext>(o => o.UseNpgsql(pg.AppConnection));   // the publisher has no tenant
        services.AddScoped<IOutboxStore, PostgresOutboxStore>();
        var provider = services.BuildServiceProvider();

        var transport = new InMemoryEventTransport();
        var options = Options.Create(new OutboxPublisherOptions { BatchSize = 500, MaxAttempts = maxAttempts, LeaseSeconds = 0 });
        return new Rig(new OutboxPublishProcessor(provider.GetRequiredService<IServiceScopeFactory>(), transport, options,
            NullLogger<OutboxPublishProcessor>.Instance), transport);
    }

    private async Task<OutboxMessage> SeedMessage()
    {
        var t = Tenant.Create($"Publisher {Guid.NewGuid():N}"[..20]).Value;
        var m = OutboxMessage.Create(t.Id, DateTimeOffset.UtcNow.AddSeconds(-5), OutboxTopics.GatewayRequests, """{"requestId":"req_1"}""").Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        db.OutboxMessages.Add(m);
        await db.SaveChangesAsync();
        return m;
    }

    private async Task<OutboxMessage> Load(Guid id)
    {
        await using var db = pg.AdminContext();
        return await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static IEnumerable<TransportMessage> Of(Rig rig, OutboxMessage m) =>
        rig.Transport.Published.Where(p => p.Headers["messageId"] == m.Id.ToString());

    [Fact]
    public async Task A_due_message_is_published_with_its_key_payload_and_headers_and_marked_sent()
    {
        var rig = Build();
        var m = await SeedMessage();

        await rig.Processor.PublishBatchAsync(CancellationToken.None);

        var sent = Of(rig, m).Should().ContainSingle().Subject;
        sent.Topic.Should().Be(OutboxTopics.GatewayRequests);
        sent.Key.Should().Be(m.TenantId.ToString());
        sent.Payload.Should().Contain("req_1");
        sent.Headers["tenantId"].Should().Be(m.TenantId.ToString());
        (await Load(m.Id)).SentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_sent_message_is_not_published_again()
    {
        var rig = Build();
        var m = await SeedMessage();

        await rig.Processor.PublishBatchAsync(CancellationToken.None);
        await rig.Processor.PublishBatchAsync(CancellationToken.None);

        Of(rig, m).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_failed_send_keeps_the_message_with_its_error_and_does_not_dead_letter_it_early()
    {
        var rig = Build(maxAttempts: 5);
        var m = await SeedMessage();
        rig.Transport.FailWith = new Error("transport.Local_MsgTimedOut", "down");

        await rig.Processor.PublishBatchAsync(CancellationToken.None);

        var loaded = await Load(m.Id);
        loaded.SentAt.Should().BeNull();
        loaded.LastErrorCode.Should().Be("transport.Local_MsgTimedOut");
        loaded.Attempts.Should().Be(1);
        loaded.NextAttemptAt.Should().BeAfter(DateTimeOffset.UtcNow);   // it waits before the next try
        rig.Transport.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task After_the_last_attempt_the_message_goes_to_the_dead_letter_topic_with_the_reason()
    {
        var rig = Build(maxAttempts: 1);
        var m = await SeedMessage();

        // The main topic is down, but the dead-letter topic accepts messages.
        rig.Transport.FailWith = new Error("transport.Local_MsgTimedOut", "down");
        var failingMain = new SelectiveTransport(rig.Transport);
        var processor = Rebuild(failingMain);

        await processor.PublishBatchAsync(CancellationToken.None);

        var dead = failingMain.Dead.Where(p => p.Headers["messageId"] == m.Id.ToString()).Should().ContainSingle().Subject;
        dead.Topic.Should().Be(OutboxTopics.GatewayRequests + ".dlq");
        dead.Headers["x-error"].Should().Be("transport.Local_MsgTimedOut");
        dead.Headers["x-attempts"].Should().Be("1");

        var loaded = await Load(m.Id);
        loaded.SentAt.Should().NotBeNull();                                  // finished: never retried
        loaded.LastErrorCode.Should().Be("transport.Local_MsgTimedOut");     // and the reason stays on the row
    }

    [Fact]
    public async Task If_even_the_dead_letter_topic_fails_the_message_is_kept_for_another_try()
    {
        var rig = Build(maxAttempts: 1);
        var m = await SeedMessage();
        rig.Transport.FailWith = new Error("transport.Local_MsgTimedOut", "down");   // everything fails

        await rig.Processor.PublishBatchAsync(CancellationToken.None);

        var loaded = await Load(m.Id);
        loaded.SentAt.Should().BeNull();
        loaded.LastErrorCode.Should().NotBeNull();
    }

    [Fact]
    public async Task A_transport_that_throws_is_treated_as_a_failure_not_a_crash()
    {
        var m = await SeedMessage();
        var processor = Rebuild(new ThrowingTransport());

        var act = () => processor.PublishBatchAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        var loaded = await Load(m.Id);
        loaded.SentAt.Should().BeNull();
        loaded.LastErrorCode.Should().Be("transport.error");
    }

    private OutboxPublishProcessor Rebuild(IEventTransport transport)
    {
        var services = new ServiceCollection();
        services.AddDbContext<VeilGateDbContext>(o => o.UseNpgsql(pg.AppConnection));
        services.AddScoped<IOutboxStore, PostgresOutboxStore>();
        var options = Options.Create(new OutboxPublisherOptions { BatchSize = 500, MaxAttempts = 1, LeaseSeconds = 0 });
        return new OutboxPublishProcessor(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), transport, options,
            NullLogger<OutboxPublishProcessor>.Instance);
    }

    /// <summary>Fails for every topic except one ending in ".dlq", which it records.</summary>
    private sealed class SelectiveTransport(InMemoryEventTransport failing) : IEventTransport
    {
        public List<TransportMessage> Dead { get; } = [];

        public Task<Result> PublishAsync(TransportMessage message, CancellationToken ct = default)
        {
            if (!message.Topic.EndsWith(OutboxPublishProcessor.DeadLetterSuffix)) return failing.PublishAsync(message, ct);
            lock (Dead) Dead.Add(message);
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class ThrowingTransport : IEventTransport
    {
        public Task<Result> PublishAsync(TransportMessage message, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }
}
