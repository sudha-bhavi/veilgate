using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using VeilGate.Api.Middleware;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

/// <summary>
/// The API also serves the built console (wwwroot). These check that each side gets its own security headers and that the
/// console's fallback never answers for the API. A temporary wwwroot stands in for the real build.
/// </summary>
[Collection("postgres")]
public sealed class ConsoleHostingTests : IDisposable
{
    private const string IndexHtml = "<!doctype html><title>console</title>";

    private readonly string _webRoot;
    private readonly GatewayApiFactory _factory;
    private readonly WebApplicationFactory<Program> _withConsole;
    private readonly HttpClient _client;

    public ConsoleHostingTests(PostgresFixture pg)
    {
        _webRoot = Directory.CreateTempSubdirectory("veilgate-wwwroot-").FullName;
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), IndexHtml);
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "assets", "index-abc123.js"), "console.log('app');");

        _factory = new GatewayApiFactory(pg);
        _withConsole = _factory.WithWebHostBuilder(b => b.UseSetting(WebHostDefaults.WebRootKey, _webRoot));
        _client = _withConsole.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _withConsole.Dispose();
        _factory.Dispose();
        Directory.Delete(_webRoot, recursive: true);
    }

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var values) || r.Content.Headers.TryGetValues(name, out values) ? string.Join(",", values) : "";

    [Fact]
    public async Task A_console_page_gets_index_html_with_the_console_policy_and_is_checked_on_every_load()
    {
        var response = await _client.GetAsync("/policies/01a188f2-f4f9-7428-b915-11c7f7a5846c");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(IndexHtml);
        Header(response, "Content-Security-Policy").Should().Be(SecurityHeadersMiddleware.ConsolePolicy);
        Header(response, "Cache-Control").Should().Be("no-cache");
        Header(response, "X-Frame-Options").Should().Be("DENY");
    }

    [Fact]
    public void The_console_policy_allows_only_our_own_files_and_no_inline_scripts()
    {
        var policy = SecurityHeadersMiddleware.ConsolePolicy;

        policy.Should().Contain("script-src 'self'").And.Contain("worker-src 'self'").And.Contain("frame-ancestors 'none'");
        policy.Should().NotContain("unsafe-eval").And.NotContain("blob:").And.NotContain("*");
        policy.Split("; ").Single(d => d.StartsWith("script-src")).Should().NotContain("unsafe-inline");
    }

    [Fact]
    public async Task A_built_file_is_served_with_a_long_cache()
    {
        var response = await _client.GetAsync("/assets/index-abc123.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Header(response, "Cache-Control").Should().Be("public, max-age=31536000, immutable");
    }

    [Fact]
    public async Task An_unknown_api_url_stays_a_plain_404_with_the_api_policy()
    {
        foreach (var path in new[] { "/api/v1/does-not-exist", "/oauth/nothing", "/api" })
        {
            var response = await _client.GetAsync(path);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("<title>console</title>", path);
            Header(response, "Content-Security-Policy").Should().Be(SecurityHeadersMiddleware.ApiPolicy, path);
            Header(response, "Cache-Control").Should().Be("no-store", path);
        }
    }

    [Fact]
    public async Task A_missing_file_is_a_404_not_the_console_page()
    {
        var response = await _client.GetAsync("/assets/gone-123.js");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_api_answer_keeps_the_strict_policy()
    {
        var response = await _client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Header(response, "Content-Security-Policy").Should().Be(SecurityHeadersMiddleware.ApiPolicy);
        Header(response, "Cache-Control").Should().Be("no-store");
    }
}
