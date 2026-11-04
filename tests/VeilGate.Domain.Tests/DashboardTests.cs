using FluentAssertions;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Dashboard;
using VeilGate.Domain.Common;
using VeilGate.Domain.Metrics;
using Xunit;

namespace VeilGate.Domain.Tests;

public class DashboardRangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 42, 17, TimeSpan.Zero);

    [Theory]
    [InlineData("24h", 24)]
    [InlineData("7d", 7)]
    [InlineData("30d", 30)]
    public void The_three_ranges_are_known(string key, int buckets) => DashboardRange.Parse(key)!.Buckets.Should().Be(buckets);

    [Theory]
    [InlineData("1h")]
    [InlineData("")]
    [InlineData("90d")]
    public void Anything_else_is_not_a_range(string key) => DashboardRange.Parse(key).Should().BeNull();

    [Fact]
    public void The_last_24_hours_start_23_hours_before_the_current_hour_and_end_with_it()
    {
        var range = DashboardRange.Parse("24h")!;

        range.Start(Now).Should().Be(new DateTimeOffset(2026, 11, 2, 11, 0, 0, TimeSpan.Zero));
        range.End(Now).Should().Be(new DateTimeOffset(2026, 11, 3, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Daily_ranges_start_at_the_beginning_of_a_utc_day()
    {
        DashboardRange.Parse("7d")!.Start(Now).Should().Be(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero));
        DashboardRange.Parse("30d")!.Start(Now).Should().Be(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
    }
}

public class MetricsSeriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 42, 0, TimeSpan.Zero);
    private static readonly DashboardRange Day = DashboardRange.Parse("24h")!;
    private static readonly DashboardRange Week = DashboardRange.Parse("7d")!;

    private static HourlyPoint Hour(int day, int hour, long requests, long entities = 0, Dictionary<string, int>? latency = null) =>
        new(new DateTimeOffset(2026, 11, day, hour, 0, 0, TimeSpan.Zero), requests, entities, latency ?? new Dictionary<string, int>());

    [Fact]
    public void Every_hour_of_the_range_has_a_point_even_when_nothing_happened()
    {
        var points = MetricsSeries.Build([Hour(3, 9, 4)], Day, Now, MetricsSeries.Requests);

        points.Should().HaveCount(24);
        points.Last().T.Should().Be(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        points.Single(p => p.T.Hour == 9 && p.T.Day == 3).Value.Should().Be(4);
        points.Count(p => p.Value == 0).Should().Be(23);
    }

    [Fact]
    public void Hours_before_the_range_are_left_out()
    {
        var points = MetricsSeries.Build([Hour(2, 10, 99)], Day, Now, MetricsSeries.Requests);   // 25 hours ago

        points.Sum(p => p.Value ?? 0).Should().Be(0);
    }

    [Fact]
    public void Daily_points_add_up_the_hours_of_their_day()
    {
        var points = MetricsSeries.Build([Hour(3, 1, 2), Hour(3, 9, 3), Hour(2, 23, 5)], Week, Now, MetricsSeries.Requests);

        points.Should().HaveCount(7);
        points[^1].Value.Should().Be(5);   // 3 November
        points[^2].Value.Should().Be(5);   // 2 November
    }

    [Fact]
    public void Protected_items_are_summed_the_same_way()
    {
        var points = MetricsSeries.Build([Hour(3, 8, 1, entities: 7), Hour(3, 9, 1, entities: 3)], Week, Now, MetricsSeries.Entities);

        points[^1].Value.Should().Be(10);
    }

    [Fact]
    public void Latency_is_the_p95_band_of_the_calls_in_the_point_and_empty_when_there_were_none()
    {
        var latency = new Dictionary<string, int> { ["50"] = 90, ["250"] = 10 };

        var points = MetricsSeries.Build([Hour(3, 9, 100, latency: latency)], Day, Now, MetricsSeries.Latency);

        points.Single(p => p.T.Hour == 9 && p.T.Day == 3).Value.Should().Be(250);
        points.Single(p => p.T.Hour == 8 && p.T.Day == 3).Value.Should().BeNull();
    }

    [Fact]
    public void An_unknown_metric_is_refused()
    {
        var act = () => MetricsSeries.Build([], Day, Now, "money");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public class DashboardHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 42, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FakeReader : IDashboardReader
    {
        public (DateTimeOffset From, DateTimeOffset To)? LastSpan;
        public double? LeakRate;
        public DashboardTotals Totals = new(0, 0, 0, 0, new Dictionary<string, int>(), new Dictionary<string, int>());

        public Task<DashboardTotals> TotalsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            LastSpan = (from, to);
            return Task.FromResult(Totals);
        }

        public Task<IReadOnlyList<ClientTotal>> TopClientsAsync(DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ClientTotal>>([new ClientTotal(Guid.NewGuid(), "ehr-bridge", 12)]);

        public Task<IReadOnlyList<HourlyPoint>> HourlyAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HourlyPoint>>([new HourlyPoint(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), 4, 6, new Dictionary<string, int>())]);

        public Task<double?> LatestLeakRateAsync(CancellationToken ct = default) => Task.FromResult(LeakRate);
    }

    [Fact]
    public async Task The_summary_reports_the_totals_and_works_the_percentiles_out_from_the_bands()
    {
        var reader = new FakeReader
        {
            LeakRate = 0.02,
            Totals = new DashboardTotals(120, 5, 2, 300, new Dictionary<string, int> { ["PHONE"] = 300 },
                new Dictionary<string, int> { ["50"] = 90, ["250"] = 9, ["1000"] = 1 }),
        };

        var result = await new GetDashboardSummaryHandler(reader, new FixedClock()).HandleAsync("24h");

        var s = result.Value;
        (s.Requests, s.BlockedRequests, s.FailedRequests, s.EntitiesProtected).Should().Be((120, 5, 2, 300));
        (s.P50LatencyMs, s.P95LatencyMs).Should().Be((50, 250));
        s.LatestLeakRate.Should().Be(0.02);
        s.EntitiesByType["PHONE"].Should().Be(300);
        s.TopClients.Should().ContainSingle().Which.Name.Should().Be("ehr-bridge");
    }

    [Fact]
    public async Task With_no_calls_there_are_no_latencies()
    {
        var result = await new GetDashboardSummaryHandler(new FakeReader(), new FixedClock()).HandleAsync("24h");

        result.Value.P50LatencyMs.Should().BeNull();
        result.Value.P95LatencyMs.Should().BeNull();
        result.Value.LatestLeakRate.Should().BeNull();
    }

    [Fact]
    public async Task The_range_defaults_to_24_hours_and_the_reader_is_asked_for_exactly_that_span()
    {
        var reader = new FakeReader();

        var result = await new GetDashboardSummaryHandler(reader, new FixedClock()).HandleAsync(null);

        result.Value.Range.Should().Be("24h");
        reader.LastSpan.Should().Be((new DateTimeOffset(2026, 11, 2, 11, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 3, 11, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task An_unknown_range_is_refused()
    {
        var result = await new GetDashboardSummaryHandler(new FakeReader(), new FixedClock()).HandleAsync("1y");

        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task The_series_has_one_point_per_hour_with_the_reader_data_filled_in()
    {
        var result = await new GetDashboardSeriesHandler(new FakeReader(), new FixedClock()).HandleAsync("24h", "requests");

        result.Value.Points.Should().HaveCount(24);
        result.Value.Points.Single(p => p.Value == 4).T.Hour.Should().Be(9);
    }

    [Theory]
    [InlineData("24h", null)]
    [InlineData("24h", "money")]
    [InlineData("2y", "requests")]
    public async Task The_series_refuses_an_unknown_metric_or_range(string range, string? metric)
    {
        var result = await new GetDashboardSeriesHandler(new FakeReader(), new FixedClock()).HandleAsync(range, metric);

        result.IsFailure.Should().BeTrue();
    }
}
