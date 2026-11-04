using FluentAssertions;
using VeilGate.Domain.Messaging;
using VeilGate.Domain.Metrics;
using Xunit;

namespace VeilGate.Domain.Tests;

public class MetricsRollupTests
{
    private static readonly Guid Tenant = Guid.Parse("0192a000-0000-7000-8000-00000000000a");
    private static readonly Guid Client = Guid.Parse("0192a000-0000-7000-8000-00000000000c");
    private static readonly DateTimeOffset At = new(2026, 11, 3, 10, 42, 17, TimeSpan.Zero);

    private static GatewayRequestEvent Event(
        string result = "success", Dictionary<string, int>? entities = null, long? latencyMs = 80, DateTimeOffset? at = null, string action = "gateway.chat") =>
        GatewayRequestEvent.Create("req_1", at ?? At, Tenant, Client, "client", action, result, entities, latencyMs).Value;

    // ------------------------------------------------------------------ hours

    [Fact]
    public void A_time_belongs_to_the_utc_hour_it_falls_in()
    {
        MetricsRollup.HourOf(At).Should().Be(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void The_hour_is_worked_out_in_utc_whatever_offset_the_time_has()
    {
        var local = new DateTimeOffset(2026, 11, 3, 15, 30, 0, TimeSpan.FromHours(5.5));   // 10:00 UTC

        MetricsRollup.HourOf(local).Should().Be(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        MetricsRollup.HourOf(local).Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void The_last_second_of_an_hour_stays_in_that_hour_and_the_next_second_starts_a_new_one()
    {
        MetricsRollup.HourOf(new DateTimeOffset(2026, 11, 3, 10, 59, 59, TimeSpan.Zero)).Hour.Should().Be(10);
        MetricsRollup.HourOf(new DateTimeOffset(2026, 11, 3, 11, 0, 0, TimeSpan.Zero)).Hour.Should().Be(11);
    }

    // ----------------------------------------------------------------- deltas

    [Fact]
    public void A_successful_call_adds_no_blocked_and_no_failed()
    {
        var d = MetricsRollup.DeltaFor(Event());
        (d.Blocked, d.Failed).Should().Be((0, 0));
    }

    [Fact]
    public void A_denied_call_counts_as_blocked()
    {
        MetricsRollup.DeltaFor(Event(result: "denied")).Blocked.Should().Be(1);
    }

    [Fact]
    public void A_failed_call_counts_as_failed_and_not_as_blocked()
    {
        var d = MetricsRollup.DeltaFor(Event(result: "failure"));
        (d.Blocked, d.Failed).Should().Be((0, 1));
    }

    [Fact]
    public void The_entities_are_summed_and_kept_by_type()
    {
        var d = MetricsRollup.DeltaFor(Event(entities: new Dictionary<string, int> { ["PHONE"] = 2, ["SSN"] = 1 }));

        d.Entities.Should().Be(3);
        d.EntitiesByType.Should().BeEquivalentTo(new Dictionary<string, int> { ["PHONE"] = 2, ["SSN"] = 1 });
    }

    [Fact]
    public void A_missing_latency_adds_no_band()
    {
        MetricsRollup.DeltaFor(Event(latencyMs: null)).LatencyBand.Should().BeNull();
    }

    // ------------------------------------------------------------------ merge

    [Fact]
    public void The_first_event_of_an_hour_starts_from_zero()
    {
        var hour = MetricsRollup.Merge(null, Tenant, MetricsRollup.DeltaFor(Event(entities: new Dictionary<string, int> { ["PHONE"] = 2 })));

        hour.TenantId.Should().Be(Tenant);
        hour.ClientId.Should().Be(Client);
        hour.Hour.Should().Be(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        hour.Requests.Should().Be(1);
        hour.Entities.Should().Be(2);
        hour.LatencyBuckets.Should().BeEquivalentTo(new Dictionary<string, int> { ["100"] = 1 });
    }

    [Fact]
    public void Events_in_the_same_hour_add_up()
    {
        var one = MetricsRollup.Merge(null, Tenant, MetricsRollup.DeltaFor(Event(entities: new Dictionary<string, int> { ["PHONE"] = 2 }, latencyMs: 80)));
        var two = MetricsRollup.Merge(one, Tenant, MetricsRollup.DeltaFor(Event(result: "denied", entities: new Dictionary<string, int> { ["PHONE"] = 1, ["SSN"] = 1 }, latencyMs: 90)));
        var three = MetricsRollup.Merge(two, Tenant, MetricsRollup.DeltaFor(Event(result: "failure", latencyMs: 400)));

        three.Requests.Should().Be(3);
        three.Blocked.Should().Be(1);
        three.Failed.Should().Be(1);
        three.Entities.Should().Be(4);
        three.EntitiesByType.Should().BeEquivalentTo(new Dictionary<string, int> { ["PHONE"] = 3, ["SSN"] = 1 });
        three.LatencyBuckets.Should().BeEquivalentTo(new Dictionary<string, int> { ["100"] = 2, ["500"] = 1 });
    }

    [Fact]
    public void Merging_does_not_change_the_counters_it_started_from()
    {
        var one = MetricsRollup.Merge(null, Tenant, MetricsRollup.DeltaFor(Event(entities: new Dictionary<string, int> { ["PHONE"] = 2 })));

        MetricsRollup.Merge(one, Tenant, MetricsRollup.DeltaFor(Event(entities: new Dictionary<string, int> { ["PHONE"] = 5 })));

        one.Requests.Should().Be(1);
        one.EntitiesByType["PHONE"].Should().Be(2);
    }

    [Fact]
    public void Adding_two_maps_adds_the_counts_key_by_key()
    {
        var sum = MetricsRollup.Add(new Dictionary<string, int> { ["A"] = 1, ["B"] = 2 }, new Dictionary<string, int> { ["B"] = 3, ["C"] = 4 });

        sum.Should().BeEquivalentTo(new Dictionary<string, int> { ["A"] = 1, ["B"] = 5, ["C"] = 4 });
    }

    // ---------------------------------------------------------------- request

    [Fact]
    public void The_request_row_is_keyed_by_the_message_and_holds_counts_not_text()
    {
        var messageId = Guid.NewGuid();
        var e = Event(result: "denied", entities: new Dictionary<string, int> { ["PHONE"] = 2, ["SSN"] = 1 });

        var row = GatewayRequestRecord.From(messageId, e);

        row.Id.Should().Be(messageId);
        row.TenantId.Should().Be(Tenant);
        row.Status.Should().Be("denied");
        row.EntityCount.Should().Be(3);
        row.Entities.Should().BeEquivalentTo(new Dictionary<string, int> { ["PHONE"] = 2, ["SSN"] = 1 });
        row.CreatedAt.Should().Be(At);
        row.RequestId.Should().Be("req_1");
    }
}

public class LatencyBandsTests
{
    [Theory]
    [InlineData(0, "25")]
    [InlineData(25, "25")]
    [InlineData(26, "50")]
    [InlineData(100, "100")]
    [InlineData(101, "250")]
    [InlineData(10_000, "10000")]
    [InlineData(10_001, "inf")]
    public void A_call_falls_in_the_band_whose_limit_it_does_not_exceed(long ms, string band)
    {
        LatencyBands.KeyFor(ms).Should().Be(band);
    }

    [Fact]
    public void With_no_calls_there_is_no_percentile()
    {
        LatencyBands.Percentile(new Dictionary<string, int>(), 0.95).Should().BeNull();
    }

    [Fact]
    public void The_percentile_is_the_limit_of_the_band_that_holds_it()
    {
        // 90 calls up to 50 ms, 9 up to 250 ms, 1 up to 1000 ms.
        var buckets = new Dictionary<string, int> { ["50"] = 90, ["250"] = 9, ["1000"] = 1 };

        LatencyBands.Percentile(buckets, 0.50).Should().Be(50);
        LatencyBands.Percentile(buckets, 0.90).Should().Be(50);
        LatencyBands.Percentile(buckets, 0.95).Should().Be(250);
        LatencyBands.Percentile(buckets, 1.0).Should().Be(1000);
    }

    [Fact]
    public void A_percentile_in_the_overflow_band_reports_the_last_limit()
    {
        LatencyBands.Percentile(new Dictionary<string, int> { ["inf"] = 4 }, 0.95).Should().Be(10_000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    [InlineData(-1)]
    public void A_percentile_outside_zero_to_one_is_refused(double p)
    {
        var act = () => LatencyBands.Percentile(new Dictionary<string, int> { ["25"] = 1 }, p);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
