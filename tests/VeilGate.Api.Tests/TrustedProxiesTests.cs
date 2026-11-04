using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VeilGate.Api.Security;
using Xunit;

namespace VeilGate.Api.Tests;

public class TrustedProxiesTests
{
    /// <summary>
    /// A tiny host with the same two calls Program.cs makes. The request is made to look as if it came from <paramref name="remote"/>,
    /// and the answer shows what the rest of the pipeline would see: "caller address|scheme".
    /// </summary>
    private static async Task<string> WhatTheApiSees(Dictionary<string, string?> settings, string remote, params (string Name, string Value)[] headers)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services => services.AddTrustedProxies(configuration));
            web.Configure(app =>
            {
                app.Use((context, next) => { context.Connection.RemoteIpAddress = IPAddress.Parse(remote); return next(context); });
                app.UseForwardedHeaders();
                app.Run(context => context.Response.WriteAsync($"{context.Connection.RemoteIpAddress}|{context.Request.Scheme}"));
            });
        }).StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return await (await host.GetTestClient().SendAsync(request)).Content.ReadAsStringAsync();
    }

    private static Dictionary<string, string?> Trusting(string network) => new() { ["Proxy:TrustedNetworks:0"] = network };

    // ------------------------------------------------------------------- when it is believed

    [Fact]
    public async Task A_request_from_the_trusted_proxy_shows_the_real_caller_and_scheme()
    {
        var seen = await WhatTheApiSees(Trusting("172.16.0.0/12"), "172.18.0.5", ("X-Forwarded-For", "203.0.113.9"), ("X-Forwarded-Proto", "https"));

        seen.Should().Be("203.0.113.9|https");
    }

    [Fact]
    public async Task A_single_trusted_address_works_too()
    {
        var seen = await WhatTheApiSees(new() { ["Proxy:TrustedAddresses:0"] = "10.0.0.2" }, "10.0.0.2", ("X-Forwarded-For", "203.0.113.9"));

        seen.Should().Be("203.0.113.9|http");
    }

    [Fact]
    public async Task An_ipv6_network_can_be_trusted()
    {
        var seen = await WhatTheApiSees(Trusting("fd00::/8"), "fd00::5", ("X-Forwarded-For", "203.0.113.9"));

        seen.Should().Be("203.0.113.9|http");
    }

    // ---------------------------------------------------------------- when it is not believed

    [Fact]
    public async Task Headers_from_anyone_else_are_ignored_so_a_caller_cannot_choose_their_own_address()
    {
        var seen = await WhatTheApiSees(Trusting("172.16.0.0/12"), "198.51.100.7", ("X-Forwarded-For", "6.6.6.6"), ("X-Forwarded-Proto", "https"));

        seen.Should().Be("198.51.100.7|http");
    }

    [Fact]
    public async Task Addresses_a_client_put_at_the_front_of_the_header_are_never_used()
    {
        // A client behind the proxy sends "6.6.6.6"; the proxy appends the real address it saw. Only the last entry is ours to believe.
        var seen = await WhatTheApiSees(Trusting("172.16.0.0/12"), "172.18.0.5", ("X-Forwarded-For", "6.6.6.6, 203.0.113.9"));

        seen.Should().Be("203.0.113.9|http");
    }

    [Fact]
    public async Task With_no_proxy_configured_the_headers_are_ignored_even_from_a_private_address()
    {
        var seen = await WhatTheApiSees(new(), "172.18.0.5", ("X-Forwarded-For", "203.0.113.9"), ("X-Forwarded-Proto", "https"));

        seen.Should().Be("172.18.0.5|http");
    }

    [Fact]
    public async Task The_loopback_default_is_not_trusted_unless_it_is_configured()
    {
        var seen = await WhatTheApiSees(Trusting("172.16.0.0/12"), "127.0.0.1", ("X-Forwarded-For", "203.0.113.9"));

        seen.Should().Be("127.0.0.1|http");
    }

    // ------------------------------------------------------------------------- configuration

    [Fact]
    public void It_says_whether_any_proxy_is_configured()
    {
        var none = new ConfigurationBuilder().Build();
        var some = new ConfigurationBuilder().AddInMemoryCollection(Trusting("10.0.0.0/8")).Build();

        new ServiceCollection().AddTrustedProxies(none).Should().BeFalse();
        new ServiceCollection().AddTrustedProxies(some).Should().BeTrue();
    }

    [Theory]
    [InlineData("172.16.0.0")]          // no prefix
    [InlineData("not-an-address/8")]
    [InlineData("172.16.0.0/40")]       // too long for IPv4
    [InlineData("172.16.0.0/-1")]
    [InlineData("/8")]
    public void A_network_that_is_not_one_stops_the_app_from_starting(string entry)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Trusting(entry)).Build();

        var add = () => new ServiceCollection().AddTrustedProxies(configuration);

        add.Should().Throw<InvalidOperationException>().WithMessage("*TrustedNetworks*");
    }

    [Fact]
    public void An_address_that_is_not_one_stops_the_app_from_starting()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Proxy:TrustedAddresses:0"] = "nope" }).Build();

        var add = () => new ServiceCollection().AddTrustedProxies(configuration);

        add.Should().Throw<InvalidOperationException>().WithMessage("*TrustedAddresses*");
    }
}
