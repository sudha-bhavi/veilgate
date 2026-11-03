using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

/// <summary>
/// "Try the demo" from the outside: real HTTP, real database, real tokens. Each test gets its own demo email, so tests
/// never see each other's demo user.
/// </summary>
[Collection("postgres")]
public sealed class DemoApiTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresFixture _pg;
    private readonly GatewayApiFactory _baseFactory;
    private readonly string _demoEmail = $"demo-{Guid.NewGuid():N}@example.test";
    private readonly List<IDisposable> _disposables = [];

    public DemoApiTests(PostgresFixture pg)
    {
        _pg = pg;
        _baseFactory = new GatewayApiFactory(pg);
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        _baseFactory.Dispose();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A client of the real API with the demo switched on or off, and this test's demo email configured.</summary>
    private (WebApplicationFactory<Program> factory, HttpClient client) Api(bool demoEnabled)
    {
        var factory = _baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Demo:Enabled", demoEnabled ? "true" : "false");
            b.UseSetting("Demo:UserEmail", _demoEmail);
        });
        var client = factory.CreateClient();
        _disposables.Add(client);
        _disposables.Add(factory);
        return (factory, client);
    }

    private async Task<Guid> SeedTenant()
    {
        var tenant = Tenant.Create($"Demo {Guid.NewGuid():N}"[..16]).Value;
        await using var db = _pg.AdminContext();
        db.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<User> SeedUser(Guid tenantId, UserRole role, string? email = null)
    {
        var sfx = Guid.NewGuid().ToString("N")[..10];
        var user = User.Invite(tenantId, email ?? $"user-{sfx}@example.test", "Test User", role, DateTimeOffset.UtcNow).Value;
        user.AcceptInvitation(new Argon2PasswordHasher().Hash(Password), DateTimeOffset.UtcNow);
        await using var db = _pg.AdminContext();
        db.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private Task<User> SeedDemoUser(Guid tenantId, UserRole role = UserRole.SecurityOfficer) => SeedUser(tenantId, role, _demoEmail);

    /// <summary>An ordinary, fully signed-in person: password and a code, confirmed just now.</summary>
    private static string NormalToken(WebApplicationFactory<Program> factory, User u) =>
        factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(
            new AccessTokenClaims(u.Id, u.TenantId, u.Role.ToString(), Guid.NewGuid(), ["pwd", "otp"], DateTimeOffset.UtcNow, u.TokenVersion));

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string? token = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    /// <summary>Clicks "Try the demo" and returns the access token and the refresh cookie ("name=value").</summary>
    private static async Task<(string token, string cookie)> StartDemo(HttpClient client)
    {
        var response = await Send(client, HttpMethod.Post, "/api/v1/auth/demo");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await Json(response)).GetProperty("accessToken").GetString()!;
        var cookie = response.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("vg_refresh=")).Split(';')[0];
        return (token, cookie);
    }

    // ---------------------------------------------------------------- off by default

    [Fact]
    public async Task The_demo_is_off_by_default_even_when_a_demo_user_exists()
    {
        await SeedDemoUser(await SeedTenant());
        var client = _baseFactory.CreateClient();
        _disposables.Add(client);

        var response = await Send(client, HttpMethod.Post, "/api/v1/auth/demo");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Code(await Json(response)).Should().Be("demo.unavailable");
        (await Json(await Send(client, HttpMethod.Get, "/api/v1/system/info"))).GetProperty("demoEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task When_it_is_on_the_system_info_says_so_for_the_sign_in_page()
    {
        var (_, client) = Api(demoEnabled: true);

        var info = await Json(await Send(client, HttpMethod.Get, "/api/v1/system/info"));

        info.GetProperty("demoEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Without_a_demo_user_there_is_nothing_to_sign_in_as()
    {
        var (_, client) = Api(demoEnabled: true);

        var response = await Send(client, HttpMethod.Post, "/api/v1/auth/demo");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Code(await Json(response)).Should().Be("demo.unavailable");
    }

    [Fact]
    public async Task A_demo_user_who_is_a_tenant_admin_is_refused()
    {
        await SeedDemoUser(await SeedTenant(), UserRole.TenantAdmin);
        var (_, client) = Api(demoEnabled: true);

        var response = await Send(client, HttpMethod.Post, "/api/v1/auth/demo");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- signing in

    [Fact]
    public async Task A_visitor_is_signed_in_as_the_demo_user_and_can_look_around()
    {
        await SeedDemoUser(await SeedTenant());
        var (_, client) = Api(demoEnabled: true);

        var response = await Send(client, HttpMethod.Post, "/api/v1/auth/demo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("Set-Cookie").Should().Contain(h => h.StartsWith("vg_refresh="));
        var token = (await Json(response)).GetProperty("accessToken").GetString()!;
        (await Send(client, HttpMethod.Get, "/api/v1/policies", token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_sign_in_is_recorded_in_the_audit_log_with_the_method_demo()
    {
        var tenantId = await SeedTenant();
        var demoUser = await SeedDemoUser(tenantId);
        var (_, client) = Api(demoEnabled: true);

        await StartDemo(client);

        await using var db = _pg.AdminContext();
        var events = db.AuditEvents.Where(e => e.TenantId == tenantId && e.Action == "auth.demo_login").ToList();
        var audited = events.Should().ContainSingle().Subject;
        audited.ActorId.Should().Be(demoUser.Id);
        audited.Details["method"].Should().Be("demo");
    }

    // ---------------------------------------------------------------- read-only

    [Fact]
    public async Task A_demo_visitor_may_try_the_playground_and_sign_out()
    {
        await SeedDemoUser(await SeedTenant());
        var (_, client) = Api(demoEnabled: true);
        var (token, _) = await StartDemo(client);

        var playground = await Send(client, HttpMethod.Post, "/api/v1/playground/run", token, new { mode = "detect", text = "Call 513-555-0142 tomorrow." });
        var logout = await Send(client, HttpMethod.Post, "/api/v1/auth/logout", token);

        playground.StatusCode.Should().Be(HttpStatusCode.OK);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_demo_visitor_may_try_a_policy_on_sample_text_but_the_guard_does_not_block_it()
    {
        await SeedDemoUser(await SeedTenant());
        var (_, client) = Api(demoEnabled: true);
        var (token, _) = await StartDemo(client);

        // The policy does not exist, so the answer is "not found". What matters is that the guard let the request through.
        var response = await Send(client, HttpMethod.Post, $"/api/v1/policies/{Guid.NewGuid()}/test", token, new { text = "sample" });

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Every_other_change_is_refused_for_a_demo_visitor()
    {
        await SeedDemoUser(await SeedTenant());
        var (_, client) = Api(demoEnabled: true);
        var (token, _) = await StartDemo(client);

        var attempts = new (HttpMethod method, string path, object? body)[]
        {
            (HttpMethod.Post, "/api/v1/policies", new { name = "Sneaky", template = "blank" }),
            (HttpMethod.Post, "/api/v1/users", new { email = "x@example.test", role = "Developer" }),
            (HttpMethod.Patch, $"/api/v1/users/{Guid.NewGuid()}", new { role = "Auditor" }),
            (HttpMethod.Post, "/api/v1/api-clients", new { name = "x", scopes = new[] { "gateway:invoke" } }),
            (HttpMethod.Delete, $"/api/v1/api-clients/{Guid.NewGuid()}", null),
            (HttpMethod.Post, "/api/v1/auth/step-up", new { code = "123456" }),
        };

        foreach (var (method, path, body) in attempts)
        {
            var response = await Send(client, method, path, token, body);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {path}");
            Code(await Json(response)).Should().Be("demo.read_only", $"{method} {path}");
        }
    }

    [Fact]
    public async Task A_refreshed_demo_session_is_still_a_demo_session()
    {
        await SeedDemoUser(await SeedTenant());
        var (_, client) = Api(demoEnabled: true);
        var (_, cookie) = await StartDemo(client);

        var refresh = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        refresh.Headers.Add("Cookie", cookie);
        var refreshed = await client.SendAsync(refresh);
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        var newToken = (await Json(refreshed)).GetProperty("accessToken").GetString()!;

        var attempt = await Send(client, HttpMethod.Post, "/api/v1/policies", newToken, new { name = "Sneaky", template = "blank" });

        attempt.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Code(await Json(attempt)).Should().Be("demo.read_only");
    }

    [Fact]
    public async Task The_guard_leaves_ordinary_sign_ins_alone()
    {
        var tenantId = await SeedTenant();
        var officer = await SeedUser(tenantId, UserRole.SecurityOfficer);
        var (factory, client) = Api(demoEnabled: true);

        var response = await Send(client, HttpMethod.Post, "/api/v1/policies", NormalToken(factory, officer),
            new { name = $"Normal {Guid.NewGuid():N}"[..20], template = "blank" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
