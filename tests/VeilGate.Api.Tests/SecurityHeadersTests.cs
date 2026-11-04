using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using VeilGate.Api.Middleware;
using Xunit;

namespace VeilGate.Api.Tests;

public class SecurityHeadersTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client(params (string Key, string Value)[] settings) => factory.WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Development");
        foreach (var (key, value) in settings) b.UseSetting(key, value);
    }).CreateClient();

    private static string One(HttpResponseMessage response, string name) => response.Headers.GetValues(name).Single();

    [Theory]
    [InlineData("/api/v1/system/info")]
    [InlineData("/health/live")]
    [InlineData("/")]                       // a console page
    [InlineData("/does/not/exist")]         // and an unknown one: headers must not depend on the route existing
    public async Task Every_response_carries_the_baseline_headers(string path)
    {
        var response = await Client().GetAsync(path);

        One(response, "X-Content-Type-Options").Should().Be("nosniff");
        One(response, "X-Frame-Options").Should().Be("DENY");
        One(response, "Referrer-Policy").Should().Be("strict-origin-when-cross-origin");
        One(response, "Cross-Origin-Opener-Policy").Should().Be("same-origin");
        One(response, "Cross-Origin-Resource-Policy").Should().Be("same-origin");
        One(response, "Permissions-Policy").Should().Be(SecurityHeadersMiddleware.PermissionsPolicy);
    }

    [Fact]
    public async Task The_permissions_policy_switches_off_the_camera_microphone_and_location()
    {
        var response = await Client().GetAsync("/api/v1/system/info");

        One(response, "Permissions-Policy").Should().Contain("camera=()").And.Contain("microphone=()").And.Contain("geolocation=()");
    }

    [Fact]
    public async Task API_answers_are_never_cached_and_may_not_load_anything()
    {
        var response = await Client().GetAsync("/api/v1/system/info");

        One(response, "Cache-Control").Should().Be("no-store");
        One(response, "Content-Security-Policy").Should().Be(SecurityHeadersMiddleware.ApiPolicy);
    }

    [Fact]
    public async Task Strict_transport_security_is_off_by_default_because_local_development_is_plain_http()
    {
        var response = await Client().GetAsync("/api/v1/system/info");

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    [Fact]
    public async Task Strict_transport_security_is_sent_for_a_year_when_switched_on()
    {
        var response = await Client(("SecurityHeaders:Hsts", "true")).GetAsync("/api/v1/system/info");

        One(response, "Strict-Transport-Security").Should().Be("max-age=31536000; includeSubDomains");
    }

    [Fact]
    public async Task The_hsts_lifetime_is_a_setting()
    {
        var response = await Client(("SecurityHeaders:Hsts", "true"), ("SecurityHeaders:HstsMaxAgeDays", "30")).GetAsync("/api/v1/system/info");

        One(response, "Strict-Transport-Security").Should().Be("max-age=2592000; includeSubDomains");
    }

    [Fact]
    public async Task The_server_does_not_announce_what_it_runs_on()
    {
        var response = await Client().GetAsync("/api/v1/system/info");

        response.Headers.Contains("Server").Should().BeFalse();
        response.Headers.Contains("X-Powered-By").Should().BeFalse();
    }
}
