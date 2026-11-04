using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using VeilGate.Api.Security;
using Xunit;

namespace VeilGate.Api.Tests;

public class ProductionSafetyCheckTests
{
    private const string PublicDevelopmentKek = "rXdjiITfqa95aHntRn/eo93iNWw0Kz8mFMrTkgPsPn0=";

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "VeilGate.Api";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>Settings of a sound deployment. Each test breaks exactly one of them.</summary>
    private static Dictionary<string, string?> Safe() => new()
    {
        ["ConnectionStrings:Postgres"] = "Host=postgres;Port=5432;Database=veilgate;Username=vg_app;Password=Xq9vT3mLz7pW2rKd8YbN",
        ["Security:VaultKek"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["Security:MfaEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["Cors:AllowedOrigins:0"] = "https://veilgate.example.com",
        ["Console:BaseUrl"] = "https://veilgate.example.com",
        ["Smtp:Host"] = "smtp.example.com",
        ["Smtp:UseStartTls"] = "true",
        ["SecurityHeaders:Hsts"] = "true",
        ["AllowedHosts"] = "veilgate.example.com",
        ["Llm:AllowPrivateNetworks"] = "false",
        ["Demo:Enabled"] = "false",
        ["Auth:AccessTokenMinutes"] = "10",
        ["Auth:LoginRateLimitPerMinute"] = "10",
    };

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IReadOnlyList<string> ProblemsWith(Action<Dictionary<string, string?>> change)
    {
        var values = Safe();
        change(values);
        return ProductionSafetyCheck.Problems(Config(values));
    }

    // ------------------------------------------------------------------ the safe case

    [Fact]
    public void A_sound_deployment_has_no_problems() => ProductionSafetyCheck.Problems(Config(Safe())).Should().BeEmpty();

    [Fact]
    public void A_sound_deployment_starts()
    {
        var start = () => ProductionSafetyCheck.EnforceOrThrow(Config(Safe()), new FakeEnvironment("Production"));

        start.Should().NotThrow();
    }

    // ------------------------------------------------------------- development is exempt

    [Fact]
    public void Development_may_use_every_unsafe_setting()
    {
        var unsafeSettings = new Dictionary<string, string?>
        {
            ["Demo:Enabled"] = "true", ["Cors:AllowedOrigins:0"] = "http://localhost:5173", ["AllowedHosts"] = "*",
            ["Security:VaultKek"] = PublicDevelopmentKek, ["Llm:AllowPrivateNetworks"] = "true",
        };

        var start = () => ProductionSafetyCheck.EnforceOrThrow(Config(unsafeSettings), new FakeEnvironment("Development"));

        start.Should().NotThrow();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Every_other_environment_is_checked(string environment)
    {
        var start = () => ProductionSafetyCheck.EnforceOrThrow(Config(new Dictionary<string, string?>()), new FakeEnvironment(environment));

        start.Should().Throw<InvalidOperationException>().WithMessage($"*'{environment}'*");
    }

    // ------------------------------------------------------------------------ the demo

    [Fact]
    public void The_demo_is_refused() => ProblemsWith(v => v["Demo:Enabled"] = "true").Should().ContainSingle().Which.Should().Contain("Demo:Enabled");

    [Fact]
    public void The_demo_is_allowed_when_it_is_explicitly_a_sandbox()
    {
        ProblemsWith(v => { v["Demo:Enabled"] = "true"; v["Safety:AllowDemo"] = "true"; }).Should().BeEmpty();
    }

    // -------------------------------------------------------------------------- database

    [Theory]
    [InlineData("Host=postgres;Database=veilgate;Username=postgres;Password=Xq9vT3mLz7pW2rKd8YbN")]
    [InlineData("Host=postgres;Database=veilgate;Username=vg_migrator;Password=Xq9vT3mLz7pW2rKd8YbN")]
    public void The_app_must_connect_as_the_least_privilege_role(string connection)
    {
        ProblemsWith(v => v["ConnectionStrings:Postgres"] = connection).Should().ContainSingle().Which.Should().Contain("vg_app");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("change-me-app-but-long-enough")]
    [InlineData("MyPassword-is-long-enough-1")]
    public void A_short_or_placeholder_database_password_is_refused(string password)
    {
        ProblemsWith(v => v["ConnectionStrings:Postgres"] = $"Host=postgres;Database=veilgate;Username=vg_app;Password={password}")
            .Should().ContainSingle().Which.Should().Contain("database password");
    }

    [Theory]
    [InlineData("")]
    [InlineData("this is not a connection string")]
    public void A_missing_or_broken_connection_string_is_refused(string connection)
    {
        ProblemsWith(v => v["ConnectionStrings:Postgres"] = connection).Should().ContainSingle().Which.Should().Contain("ConnectionStrings:Postgres");
    }

    // ----------------------------------------------------------------------------- keys

    [Theory]
    [InlineData("Security:VaultKek")]
    [InlineData("Security:MfaEncryptionKey")]
    public void A_missing_key_is_refused(string key)
    {
        ProblemsWith(v => v[key] = null).Should().ContainSingle().Which.Should().Contain(key).And.Contain("missing");
    }

    [Theory]
    [InlineData("Security:VaultKek")]
    [InlineData("Security:MfaEncryptionKey")]
    public void A_key_that_is_not_32_bytes_is_refused(string key)
    {
        ProblemsWith(v => v[key] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))).Should().ContainSingle().Which.Should().Contain("32 bytes");
    }

    [Fact]
    public void A_key_that_is_not_base64_is_refused()
    {
        ProblemsWith(v => v["Security:VaultKek"] = "not base64 at all!").Should().ContainSingle().Which.Should().Contain("base64");
    }

    [Fact]
    public void A_key_that_repeats_one_byte_is_refused()
    {
        ProblemsWith(v => v["Security:VaultKek"] = Convert.ToBase64String(new byte[32])).Should().ContainSingle().Which.Should().Contain("random");
    }

    [Fact]
    public void The_published_development_key_is_refused()
    {
        ProblemsWith(v => v["Security:VaultKek"] = PublicDevelopmentKek).Should().ContainSingle().Which.Should().Contain("published in the repository");
    }

    [Fact]
    public void The_two_keys_must_differ()
    {
        ProblemsWith(v => v["Security:MfaEncryptionKey"] = v["Security:VaultKek"]).Should().ContainSingle().Which.Should().Contain("same value");
    }

    // ------------------------------------------------------------------ addresses and mail

    [Theory]
    [InlineData("http://veilgate.example.com")]
    [InlineData("https://localhost:5173")]
    [InlineData("https://127.0.0.1")]
    [InlineData("*")]
    [InlineData("not an address")]
    public void A_cors_origin_must_be_a_public_https_address(string origin)
    {
        ProblemsWith(v => v["Cors:AllowedOrigins:0"] = origin).Should().ContainSingle().Which.Should().Contain("Cors:AllowedOrigins");
    }

    [Fact]
    public void No_cors_origin_at_all_is_refused()
    {
        ProblemsWith(v => v.Remove("Cors:AllowedOrigins:0")).Should().ContainSingle().Which.Should().Contain("empty");
    }

    [Theory]
    [InlineData("http://veilgate.example.com")]
    [InlineData("http://localhost:5173")]
    [InlineData("")]
    public void The_console_address_in_emails_must_be_a_public_https_address(string address)
    {
        ProblemsWith(v => v["Console:BaseUrl"] = address).Should().ContainSingle().Which.Should().Contain("Console:BaseUrl");
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("mailhog")]
    [InlineData("")]
    public void The_mail_server_must_not_be_a_local_catcher(string host)
    {
        ProblemsWith(v => v["Smtp:Host"] = host).Should().ContainSingle().Which.Should().Contain("Smtp:Host");
    }

    [Fact]
    public void Mail_must_use_starttls()
    {
        ProblemsWith(v => v["Smtp:UseStartTls"] = "false").Should().ContainSingle().Which.Should().Contain("UseStartTls");
    }

    // ---------------------------------------------------------------------------- the rest

    [Fact]
    public void Hsts_must_be_switched_on() => ProblemsWith(v => v["SecurityHeaders:Hsts"] = "false").Should().ContainSingle().Which.Should().Contain("Hsts");

    [Theory]
    [InlineData("*")]
    [InlineData("veilgate.example.com;*")]
    [InlineData("")]
    public void Allowed_hosts_must_name_the_real_hosts(string hosts)
    {
        ProblemsWith(v => v["AllowedHosts"] = hosts).Should().ContainSingle().Which.Should().Contain("AllowedHosts");
    }

    [Fact]
    public void The_relaxed_outbound_network_rules_are_refused()
    {
        ProblemsWith(v => v["Llm:AllowPrivateNetworks"] = "true").Should().ContainSingle().Which.Should().Contain("SSRF");
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void Trusting_every_address_as_a_proxy_is_refused(string network)
    {
        ProblemsWith(v => v["Proxy:TrustedNetworks:0"] = network).Should().ContainSingle().Which.Should().Contain("Proxy:TrustedNetworks");
    }

    [Fact]
    public void Trusting_only_the_proxys_own_network_is_fine()
    {
        ProblemsWith(v => v["Proxy:TrustedNetworks:0"] = "172.16.0.0/12").Should().BeEmpty();
    }

    [Fact]
    public void A_long_lived_access_token_is_refused()
    {
        ProblemsWith(v => v["Auth:AccessTokenMinutes"] = "60").Should().ContainSingle().Which.Should().Contain("AccessTokenMinutes");
    }

    [Fact]
    public void The_sign_in_limit_raised_for_a_pen_test_is_refused()
    {
        ProblemsWith(v => v["Auth:LoginRateLimitPerMinute"] = "1000").Should().ContainSingle().Which.Should().Contain("penetration test");
    }

    // ------------------------------------------------------------------ what the message says

    [Fact]
    public void Every_problem_is_listed_at_once_so_they_can_all_be_fixed_in_one_go()
    {
        var start = () => ProductionSafetyCheck.EnforceOrThrow(Config(new Dictionary<string, string?>()), new FakeEnvironment("Production"));

        var message = start.Should().Throw<InvalidOperationException>().Which.Message;

        message.Should().Contain("ConnectionStrings:Postgres").And.Contain("Security:VaultKek").And.Contain("Cors:AllowedOrigins")
            .And.Contain("Smtp:Host").And.Contain("SecurityHeaders:Hsts").And.Contain("AllowedHosts");
    }

    [Fact]
    public void The_message_never_contains_a_secret_value()
    {
        var values = Safe();
        values["ConnectionStrings:Postgres"] = "Host=postgres;Database=veilgate;Username=vg_app;Password=password-hunter2-hunter2";   // refused as a placeholder, and must not be echoed
        values["Security:VaultKek"] = PublicDevelopmentKek;
        values["Smtp:Host"] = "localhost";

        var start = () => ProductionSafetyCheck.EnforceOrThrow(Config(values), new FakeEnvironment("Production"));

        var message = start.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("database password").And.Contain("published in the repository");   // both were reported...
        message.Should().NotContain("hunter2").And.NotContain(PublicDevelopmentKek);                   // ...without showing either value
    }
}
