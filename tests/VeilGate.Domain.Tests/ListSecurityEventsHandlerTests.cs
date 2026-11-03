using FluentAssertions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Audit;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;
using Xunit;

namespace VeilGate.Domain.Tests;

public class ListSecurityEventsHandlerTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private readonly ISecurityEventReader _reader = Substitute.For<ISecurityEventReader>();

    public ListSecurityEventsHandlerTests() =>
        _reader.ListAsync(Arg.Any<SecurityEventFilter>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<SecurityEventView>)new List<SecurityEventView>(), 0));

    private ListSecurityEventsHandler Handler() => new(_reader);

    private static ListSecurityEventsQuery Query(
        bool whole, Guid? userId = null, string? severity = null, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        new(Me, whole, userId, severity, from, to);

    private SecurityEventFilter SentFilter() =>
        (SecurityEventFilter)_reader.ReceivedCalls().Single().GetArguments()[0]!;

    [Fact]
    public async Task Someone_who_sees_the_whole_organization_is_not_narrowed()
    {
        var r = await Handler().HandleAsync(Query(whole: true), 1, 25);

        r.IsSuccess.Should().BeTrue();
        SentFilter().UserId.Should().BeNull();
    }

    [Fact]
    public async Task Someone_who_sees_the_whole_organization_may_pick_a_person()
    {
        await Handler().HandleAsync(Query(whole: true, userId: Other), 1, 25);

        SentFilter().UserId.Should().Be(Other);
    }

    [Fact]
    public async Task Everyone_else_is_always_narrowed_to_their_own_events()
    {
        await Handler().HandleAsync(Query(whole: false), 1, 25);

        SentFilter().UserId.Should().Be(Me);
    }

    [Fact]
    public async Task Everyone_else_may_name_themselves_but_not_someone_else()
    {
        (await Handler().HandleAsync(Query(whole: false, userId: Me), 1, 25)).IsSuccess.Should().BeTrue();

        _reader.ClearReceivedCalls();
        var refused = await Handler().HandleAsync(Query(whole: false, userId: Other), 1, 25);

        refused.Error.Should().Be(ListSecurityEventsHandler.OnlyYourOwn);
        await _reader.DidNotReceiveWithAnyArgs().ListAsync(default!, default, default, default);
    }

    [Theory]
    [InlineData("high", Severity.High)]
    [InlineData("Critical", Severity.Critical)]
    public async Task A_severity_is_read_whatever_its_case(string given, Severity expected)
    {
        await Handler().HandleAsync(Query(whole: true, severity: given), 1, 25);

        SentFilter().Severity.Should().Be(expected);
    }

    [Theory]
    [InlineData("urgent")]
    [InlineData("42")]
    public async Task An_unknown_severity_is_a_validation_error(string given)
    {
        var r = await Handler().HandleAsync(Query(whole: true, severity: given), 1, 25);

        r.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task A_time_range_that_ends_before_it_starts_is_a_validation_error()
    {
        var now = DateTimeOffset.UtcNow;

        var r = await Handler().HandleAsync(Query(whole: true, from: now, to: now.AddDays(-1)), 1, 25);

        r.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task Paging_is_kept_inside_sensible_limits()
    {
        var r = await Handler().HandleAsync(Query(whole: true), page: 0, pageSize: 5000);

        r.Value.Page.Should().Be(1);
        r.Value.PageSize.Should().Be(ListSecurityEventsHandler.MaxPageSize);
    }
}
