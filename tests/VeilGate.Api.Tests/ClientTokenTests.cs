using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using VeilGate.Api.Security;
using VeilGate.Application.Abstractions;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

[Collection("postgres")]
public sealed class ClientTokenTests : IDisposable
{
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;

    public ClientTokenTests(PostgresFixture pg)
    {
        _factory = new AuthApiFactory(pg);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private string ClientToken(Guid clientId, Guid tenantId, params string[] scopes) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueClientToken(new ClientTokenClaims(clientId, tenantId, scopes));

    [Fact]
    public void A_client_token_carries_the_client_the_tenant_and_the_scopes_and_expires_in_15_minutes()
    {
        var clientId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var jwt = new JsonWebToken(ClientToken(clientId, tenantId, "gateway:invoke", "gateway:detect"));

        jwt.Typ.Should().Be("at+jwt");
        jwt.GetClaim("client_id").Value.Should().Be(clientId.ToString());
        jwt.GetClaim("tid").Value.Should().Be(tenantId.ToString());
        jwt.GetClaim("scope").Value.Should().Be("gateway:invoke gateway:detect");
        (jwt.ValidTo - jwt.ValidFrom).Should().Be(TimeSpan.FromMinutes(15));
        jwt.Claims.Should().NotContain(c => c.Type == "role" || c.Type == "sid", "a client is not a person");
    }

    [Fact]
    public async Task A_client_token_cannot_open_a_console_endpoint()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ClientToken(Guid.NewGuid(), Guid.NewGuid(), "gateway:invoke"));

        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("gateway:invoke gateway:detect", "gateway:detect", true)]
    [InlineData("gateway:invoke", "gateway:detect", false)]
    [InlineData("gateway:invoke", "gateway:invo", false)]   // a prefix is not a match
    [InlineData("", "gateway:invoke", false)]
    public void Scope_matching_is_exact(string scopes, string required, bool expected)
    {
        var identity = new System.Security.Claims.ClaimsIdentity([new("scope", scopes)], "test");

        AuthPolicies.HasScope(new System.Security.Claims.ClaimsPrincipal(identity), required).Should().Be(expected);
    }
}
