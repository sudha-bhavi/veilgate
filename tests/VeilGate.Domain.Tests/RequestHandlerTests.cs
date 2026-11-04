using FluentAssertions;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Requests;
using VeilGate.Domain.Common;
using Xunit;

namespace VeilGate.Domain.Tests;

public class RequestHandlerTests
{
    private static readonly GatewayRequestFilter NoFilter = new(null, null, null, null, null, null);

    private sealed class FakeReader(GatewayRequestView? found = null) : IGatewayRequestReader
    {
        public int? LastPage, LastPageSize;

        public Task<(IReadOnlyList<GatewayRequestView> Items, int Total)> ListAsync(GatewayRequestFilter filter, int page, int pageSize, CancellationToken ct = default)
        {
            (LastPage, LastPageSize) = (page, pageSize);
            return Task.FromResult<(IReadOnlyList<GatewayRequestView>, int)>(([], 0));
        }

        public Task<GatewayRequestView?> FindAsync(Guid id, CancellationToken ct = default) => Task.FromResult(found);
        public Task<IReadOnlyList<string>> ListActionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(["gateway.chat"]);
    }

    private static GatewayRequestView View() => new(Guid.NewGuid(), "req_1", DateTimeOffset.UtcNow, "client", Guid.NewGuid(), "ehr-bridge",
        "gateway.chat", "success", null, "gpt-4o-mini", "mock", "7", 2, new Dictionary<string, int> { ["PHONE"] = 2 }, 120);

    [Fact]
    public async Task A_status_outside_the_known_set_is_refused()
    {
        var result = await new ListGatewayRequestsHandler(new FakeReader()).HandleAsync(NoFilter with { Status = "maybe" }, 1, 50);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task A_time_range_that_ends_before_it_starts_is_refused()
    {
        var now = DateTimeOffset.UtcNow;

        var result = await new ListGatewayRequestsHandler(new FakeReader()).HandleAsync(NoFilter with { From = now, To = now.AddDays(-1) }, 1, 50);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task An_over_long_filter_value_is_refused()
    {
        var result = await new ListGatewayRequestsHandler(new FakeReader()).HandleAsync(NoFilter with { RequestId = new string('x', 41) }, 1, 50);

        result.IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-3, 500, 1, 100)]
    [InlineData(4, 25, 4, 25)]
    public async Task The_page_and_page_size_are_kept_in_range(int page, int pageSize, int expectedPage, int expectedSize)
    {
        var reader = new FakeReader();

        var result = await new ListGatewayRequestsHandler(reader).HandleAsync(NoFilter, page, pageSize);

        result.IsSuccess.Should().BeTrue();
        (reader.LastPage, reader.LastPageSize).Should().Be((expectedPage, expectedSize));
        (result.Value.Page, result.Value.PageSize).Should().Be((expectedPage, expectedSize));
    }

    [Fact]
    public async Task A_known_request_is_returned()
    {
        var view = View();

        var result = await new GetGatewayRequestHandler(new FakeReader(view)).HandleAsync(view.Id);

        result.Value.Should().Be(view);
    }

    [Fact]
    public async Task An_unknown_request_is_not_found()
    {
        var result = await new GetGatewayRequestHandler(new FakeReader()).HandleAsync(Guid.NewGuid());

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
        result.Error.Code.Should().Be("request.not_found");
    }
}
