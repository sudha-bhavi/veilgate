using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

/// <summary>
/// Penetration test finding VG-PT-006: a NUL character (\u0000) in a search filter made the API answer 500, because PostgreSQL cannot
/// store it. Bad input must be a 400, never a server error.
/// </summary>
[Collection("postgres")]
public sealed class InputHandlingApiTests : IDisposable
{
    private readonly PostgresFixture _pg;
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;

    public InputHandlingApiTests(PostgresFixture pg)
    {
        _pg = pg;
        _factory = new AuthApiFactory(pg);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<string> AdminToken(UserRole role = UserRole.TenantAdmin)
    {
        var sfx = Guid.NewGuid().ToString("N")[..10];
        var tenant = Tenant.Create($"Input {sfx}").Value;
        var user = User.Invite(tenant.Id, $"user-{sfx}@example.test", $"Person {sfx}", role, DateTimeOffset.UtcNow).Value;
        user.AcceptInvitation("hash", DateTimeOffset.UtcNow);
        await using (var db = _pg.AdminContext())
        {
            db.Add(tenant);
            await db.SaveChangesAsync();
            db.Add(user);
            await db.SaveChangesAsync();
        }
        return _factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(
            new AccessTokenClaims(user.Id, user.TenantId, role.ToString(), Guid.NewGuid(), ["pwd", "otp"], DateTimeOffset.UtcNow, user.TokenVersion));
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    [Theory]
    [InlineData("/api/v1/users?q=%00")]
    [InlineData("/api/v1/policies?q=%00")]
    [InlineData("/api/v1/audit-events?action=%00")]
    [InlineData("/api/v1/users?q=a%00b")]
    public async Task A_nul_character_in_a_filter_is_a_400_not_a_500(string path)
    {
        var token = await AdminToken();

        var response = await Send(HttpMethod.Get, path, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("request.invalid_characters");
    }

    [Fact]
    public async Task A_nul_character_in_a_json_body_is_a_400_not_a_500()
    {
        var token = await AdminToken(UserRole.SecurityOfficer);
        var body = new StringContent("{\"name\":\"bad\\u0000name\",\"template\":\"blank\"}", Encoding.UTF8, "application/json");

        var response = await Send(HttpMethod.Post, "/api/v1/policies", token, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ordinary_odd_text_is_still_fine()
    {
        var token = await AdminToken();

        var response = await Send(HttpMethod.Get, "/api/v1/users?q=" + Uri.EscapeDataString("' OR '1'='1 <b>é漢"), token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
