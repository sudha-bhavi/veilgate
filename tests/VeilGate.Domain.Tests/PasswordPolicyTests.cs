using FluentAssertions;
using VeilGate.Domain;
using Xunit;

namespace VeilGate.Domain.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public void Too_short_is_rejected(string pw) => PasswordPolicy.Validate(pw).IsFailure.Should().BeTrue();

    [Fact]
    public void Too_long_is_rejected() => PasswordPolicy.Validate(new string('a', 1) + new string('b', PasswordPolicy.MaxLength)).IsFailure.Should().BeTrue();

    [Theory]
    [InlineData("password1234")]
    [InlineData("PASSWORD1234")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    public void Common_or_repeated_passwords_are_rejected(string pw) => PasswordPolicy.Validate(pw).IsFailure.Should().BeTrue();

    [Fact]
    public void Password_built_from_the_email_name_is_rejected()
        => PasswordPolicy.Validate("jordanreyes-2026!", "jordanreyes@acme.test").IsFailure.Should().BeTrue();

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("twelve chars!")]
    public void Long_phrases_are_accepted(string pw) => PasswordPolicy.Validate(pw, "jordan@acme.test").IsSuccess.Should().BeTrue();

    [Fact]
    public void Failure_carries_the_stable_code() => PasswordPolicy.Validate("x").Error.Code.Should().Be("password.weak");
}
