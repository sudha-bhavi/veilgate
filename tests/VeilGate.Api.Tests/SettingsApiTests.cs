using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

/// <summary>The read-only Settings endpoint: real HTTP, real database and row-level security.</summary>
[Collection("postgres")]
public sealed class SettingsApiTests : IDisposable
{
    private readonly PostgresFixture _pg;
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;

    public SettingsApiTests(PostgresFixture pg)
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

    private async Task<(Tenant Tenant, string Token)> SeedPerson(UserRole role)
    {
        var sfx = Guid.NewGuid().ToString("N")[..10];
        var tenant = Tenant.Create($"Settings {sfx}").Value;
        var user = User.Invite(tenant.Id, $"user-{sfx}@example.test", $"Person {sfx}", role, DateTimeOffset.UtcNow).Value;
        user.AcceptInvitation("hash", DateTimeOffset.UtcNow);
        await using (var db = _pg.AdminContext())
        {
            db.Add(tenant);
            await db.SaveChangesAsync();
            db.Add(user);
            await db.SaveChangesAsync();
        }
        var token = _factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(
            new AccessTokenClaims(user.Id, user.TenantId, role.ToString(), Guid.NewGuid(), ["pwd", "otp"], DateTimeOffset.UtcNow, user.TokenVersion));
        return (tenant, token);
    }

    private Task<HttpResponseMessage> Get(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task A_tenant_admin_sees_their_own_organisation_and_the_rules()
    {
        var (tenant, token) = await SeedPerson(UserRole.TenantAdmin);

        var response = await Get(token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = doc.RootElement;
        body.GetProperty("organisation").GetProperty("name").GetString().Should().Be(tenant.Name);
        body.GetProperty("organisation").GetProperty("status").GetString().Should().Be("Active");
        var rules = body.GetProperty("rules");
        rules.GetProperty("lockoutAttempts").GetInt32().Should().Be(User.MaxFailedAttempts);
        rules.GetProperty("lockoutMinutes").GetInt32().Should().Be(15);
        rules.GetProperty("sessionIdleHours").GetInt32().Should().Be(8);
        rules.GetProperty("sessionMaxDays").GetInt32().Should().Be(7);
        rules.GetProperty("mfaRequired").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Each_admin_sees_only_their_own_organisation()
    {
        var (first, firstToken) = await SeedPerson(UserRole.TenantAdmin);
        var (second, secondToken) = await SeedPerson(UserRole.TenantAdmin);

        var a = JsonDocument.Parse(await (await Get(firstToken)).Content.ReadAsStringAsync()).RootElement;
        var b = JsonDocument.Parse(await (await Get(secondToken)).Content.ReadAsStringAsync()).RootElement;

        a.GetProperty("organisation").GetProperty("name").GetString().Should().Be(first.Name);
        b.GetProperty("organisation").GetProperty("name").GetString().Should().Be(second.Name);
    }

    [Theory]
    [InlineData(UserRole.Developer)]
    [InlineData(UserRole.SecurityOfficer)]
    [InlineData(UserRole.Auditor)]
    public async Task Other_roles_are_refused(UserRole role)
    {
        var (_, token) = await SeedPerson(role);

        (await Get(token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Nobody_signed_out_can_ask()
    {
        (await Get(null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
