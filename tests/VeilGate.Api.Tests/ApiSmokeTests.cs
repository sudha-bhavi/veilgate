using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace VeilGate.Api.Tests;

public class ApiSmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.WithWebHostBuilder(b => b.UseEnvironment("Development")).CreateClient();

    [Fact]
    public async Task Live_health_returns_200()
    {
        var res = await _client.GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task System_info_returns_name()
    {
        var res = await _client.GetAsync("/api/v1/system/info");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("VeilGate");
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var res = await _client.GetAsync("/api/v1/system/info");
        res.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
        res.Headers.GetValues("X-Frame-Options").Should().ContainSingle("DENY");
        res.Headers.Contains("Server").Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_route_is_404_not_500()
    {
        var res = await _client.GetAsync("/api/v1/does-not-exist");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
