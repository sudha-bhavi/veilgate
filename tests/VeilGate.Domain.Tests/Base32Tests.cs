using System.Text;
using FluentAssertions;
using VeilGate.Domain;
using Xunit;

namespace VeilGate.Domain.Tests;

public class Base32Tests
{
    // Official RFC 4648 test vectors (without padding).
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Encode_matches_the_RFC_vectors(string plain, string expected)
    {
        Base32.Encode(Encoding.ASCII.GetBytes(plain)).Should().Be(expected);
    }

    [Theory]
    [InlineData("MY", "f")]
    [InlineData("mzxw6ytboi", "foobar")]          // lowercase is fine
    [InlineData("MZXW6YTB OI", "foobar")]         // spaces are fine
    [InlineData("MZXW6YQ=", "foob")]              // padding is fine
    public void Decode_is_forgiving_about_formatting(string text, string expected)
    {
        Encoding.ASCII.GetString(Base32.Decode(text)).Should().Be(expected);
    }

    [Fact]
    public void Decode_rejects_characters_outside_the_alphabet()
    {
        var act = () => Base32.Decode("MZXW1");   // '1' is not Base32
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Random_bytes_survive_a_round_trip()
    {
        var bytes = new byte[20];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);

        Base32.Decode(Base32.Encode(bytes)).Should().Equal(bytes);
    }
}