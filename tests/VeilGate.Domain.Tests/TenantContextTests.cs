using FluentAssertions;
using VeilGate.Infrastructure.Persistence;
using Xunit;

namespace VeilGate.Domain.Tests;

public class TenantContextTests
{
    [Fact]
    public void Reading_before_set_throws()
    {
        var ctx = new TenantContext();
        ctx.IsSet.Should().BeFalse();
        var act = () => ctx.TenantId;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Can_only_be_set_once()
    {
        var ctx = new TenantContext();
        var a = Guid.NewGuid();
        ctx.Set(a);
        ctx.TenantId.Should().Be(a);
        var act = () => ctx.Set(Guid.NewGuid());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Empty_guid_is_rejected()
    {
        var act = () => new TenantContext().Set(Guid.Empty);
        act.Should().Throw<ArgumentException>();
    }
}
