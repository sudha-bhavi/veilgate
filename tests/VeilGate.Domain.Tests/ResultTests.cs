using FluentAssertions;
using VeilGate.Domain;
using VeilGate.Domain.Common;
using Xunit;

namespace VeilGate.Domain.Tests;

public class ResultTests
{
    [Fact]
    public void Success_has_value_and_no_error()
    {
        Result<int> r = 42;
        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be(42);
        r.Error.Should().Be(Error.None);
    }

    [Fact]
    public void Failure_cannot_expose_value()
    {
        Result<int> r = Error.NotFound();
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("resource.not_found");
        var act = () => r.Value;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Failure_requires_an_error()
    {
        var act = () => Result.Failure(Error.None);
        act.Should().Throw<InvalidOperationException>();
    }
}

public class TenantTests
{
    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    public void Create_rejects_short_names(string name)
    {
        var r = Tenant.Create(name);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("validation.failed");
    }

    [Fact]
    public void Create_trims_and_sets_active()
    {
        var r = Tenant.Create("  Acme Health  ");
        r.IsSuccess.Should().BeTrue();
        r.Value.Name.Should().Be("Acme Health");
        r.Value.Status.Should().Be("Active");
        r.Value.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Suspend_twice_is_a_conflict()
    {
        var t = Tenant.Create("Acme Health").Value;
        t.Suspend().IsSuccess.Should().BeTrue();
        t.Suspend().Error.Kind.Should().Be(ErrorKind.Conflict);
    }
}
