using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

/// <summary>
/// API clients and POST /oauth/token from the outside: real HTTP, real database and row-level security, real tokens.
/// The whole path is exercised: a person creates a client, the client trades its secret for a token, and the gateway accepts it.
/// </summary>
[Collection("postgres")]
public sealed class ApiClientsApiTests : IDisposable
{
    private const string PhoneText = "Call the patient on 513-555-0142 tomorrow.";
    private const string BlockPhoneYaml = """
        version: 1
        name: block-phone
        defaults: { action: REDACT, minConfidence: 0.5 }
        entities:
          PHONE: { action: BLOCK }
        """;

    private readonly PostgresFixture _pg;
    private readonly GatewayApiFactory _factory;
    private readonly HttpClient _client;
    private readonly List<IDisposable> _disposables = [];

    public ApiClientsApiTests(PostgresFixture pg)
    {
        _pg = pg;
        _factory = new GatewayApiFactory(pg);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        _client.Dispose();
        _factory.Dispose();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Guid> SeedTenant()
    {
        var tenant = Tenant.Create($"Cli {Guid.NewGuid():N}"[..16]).Value;
        await using var db = _pg.AdminContext();
        db.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<User> SeedUser(Guid tenantId, UserRole role)
    {
        var sfx = Guid.NewGuid().ToString("N")[..10];
        var user = User.Invite(tenantId, $"user-{sfx}@example.test", "Test User", role, DateTimeOffset.UtcNow).Value;
        user.AcceptInvitation(new Argon2PasswordHasher().Hash("correct horse battery staple"), DateTimeOffset.UtcNow);
        await using var db = _pg.AdminContext();
        db.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>A person's token. <paramref name="mfaAge"/> is how long ago they last confirmed a second factor.</summary>
    private string UserToken(User u, TimeSpan? mfaAge = null) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(
            new AccessTokenClaims(u.Id, u.TenantId, u.Role.ToString(), Guid.NewGuid(), ["pwd", "otp"],
                DateTimeOffset.UtcNow - (mfaAge ?? TimeSpan.Zero), u.TokenVersion));

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token, object? body = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client ?? _client).SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;
    private static string OAuthError(JsonElement body) => body.GetProperty("error").GetString()!;

    private async Task<(Guid id, string clientId, string secret)> CreateClient(string token, object? body = null, HttpStatusCode expected = HttpStatusCode.Created)
    {
        body ??= new { name = $"client-{Guid.NewGuid():N}"[..20], scopes = new[] { "gateway:invoke", "gateway:detect", "gateway:deidentify" } };
        var response = await Send(HttpMethod.Post, "/api/v1/api-clients", token, body);
        response.StatusCode.Should().Be(expected);
        if (expected != HttpStatusCode.Created) return default;
        var json = await Json(response);
        return (json.GetProperty("id").GetGuid(), json.GetProperty("clientId").GetString()!, json.GetProperty("clientSecret").GetString()!);
    }

    private Task<HttpResponseMessage> TokenRequest(
        Dictionary<string, string> form, HttpClient? client = null, string? basicUser = null, string? basicPassword = null, string? testIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = new FormUrlEncodedContent(form) };
        if (basicUser is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basicUser}:{basicPassword}")));
        if (testIp is not null) request.Headers.Add("X-Test-Ip", testIp);
        return (client ?? _client).SendAsync(request);
    }

    private static Dictionary<string, string> Credentials(string clientId, string secret, string? scope = null)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret };
        if (scope is not null) form["scope"] = scope;
        return form;
    }

    private async Task<string> GetToken(string clientId, string secret, string? scope = null, HttpClient? client = null, string? testIp = null)
    {
        var response = await TokenRequest(Credentials(clientId, secret, scope), client, testIp: testIp);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await Json(response)).GetProperty("access_token").GetString()!;
    }

    private async Task<HttpStatusCode> Detect(string token, HttpClient? client = null, string? testIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/gateway/detect") { Content = JsonContent.Create(new { text = "Call 513-555-0142" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (testIp is not null) request.Headers.Add("X-Test-Ip", testIp);
        return (await (client ?? _client).SendAsync(request)).StatusCode;
    }

    private async Task<HttpResponseMessage> Deidentify(string token) =>
        await Send(HttpMethod.Post, "/api/v1/gateway/deidentify", token, new { text = PhoneText });

    /// <summary>A client factory whose requests can pretend to come from any address (header X-Test-Ip), so the allowlist can be tested.</summary>
    private HttpClient ClientWithTestIps()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddTransient<IStartupFilter, TestIpStartupFilter>()));
        _disposables.Add(factory);
        return factory.CreateClient();
    }

    private sealed class TestIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nxt) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Ip", out var ip)) ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                await nxt();
            });
            next(app);
        };
    }

    private async Task<Guid> PublishBlockPhonePolicy(Guid tenantId)
    {
        var author = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var officer = UserToken(await SeedUser(tenantId, UserRole.SecurityOfficer));
        var created = await Send(HttpMethod.Post, "/api/v1/policies", author, new { name = $"Block {Guid.NewGuid():N}"[..20], template = "blank" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(created)).GetProperty("id").GetGuid();

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/policies/{id}/draft") { Content = JsonContent.Create(new { yaml = BlockPhoneYaml }) };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", author);
        put.Headers.TryAddWithoutValidation("If-Match", created.Headers.ETag!.Tag);
        (await _client.SendAsync(put)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Send(HttpMethod.Post, $"/api/v1/policies/{id}/submit", author, new { summary = "Block phone numbers" })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Send(HttpMethod.Post, $"/api/v1/policies/{id}/publish", officer)).StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    // ---------------------------------------------------------------- creating and reading clients

    [Fact]
    public async Task Creating_a_client_shows_its_secret_once_and_stores_only_a_hash()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));

        var response = await Send(HttpMethod.Post, "/api/v1/api-clients", admin, new
        {
            name = "ehr-bridge", scopes = new[] { "gateway:invoke", "gateway:deidentify" },
            ipAllowlist = new[] { "203.0.113.0/24" }, rateLimitPerMinute = 600
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await Json(response);
        var secret = body.GetProperty("clientSecret").GetString()!;
        secret.Should().StartWith("vgs_");
        body.GetProperty("clientId").GetString().Should().StartWith("vg_");
        body.GetProperty("status").GetString().Should().Be("Active");
        response.Headers.Location!.ToString().Should().EndWith(body.GetProperty("id").GetGuid().ToString());

        // Reading it back never shows a secret or a hash.
        var id = body.GetProperty("id").GetGuid();
        var read = await (await Send(HttpMethod.Get, $"/api/v1/api-clients/{id}", admin)).Content.ReadAsStringAsync();
        read.Should().NotContain(secret).And.NotContain("argon2").And.NotContain("clientSecret").And.NotContain("secretHash");

        // In the database there is a hash, never the secret.
        await using var db = _pg.AdminContext();
        var hashes = await db.ApiClientSecrets.AsNoTracking().Where(s => s.TenantId == tenantId).Select(s => s.SecretHash).ToListAsync();
        hashes.Should().ContainSingle().Which.Should().StartWith("$argon2id$").And.NotContain(secret);
    }

    [Fact]
    public async Task Creating_a_client_needs_a_recent_second_factor_check()
    {
        var tenantId = await SeedTenant();
        var adminUser = await SeedUser(tenantId, UserRole.TenantAdmin);
        var body = new { name = "late", scopes = new[] { "gateway:invoke" } };

        var stale = await Send(HttpMethod.Post, "/api/v1/api-clients", UserToken(adminUser, mfaAge: TimeSpan.FromMinutes(10)), body);

        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Code(await Json(stale)).Should().Be("auth.insufficient_user_authentication");
        (await Send(HttpMethod.Post, "/api/v1/api-clients", UserToken(adminUser), body)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Only_a_tenant_admin_or_developer_may_manage_clients()
    {
        var tenantId = await SeedTenant();
        var body = new { name = "nope", scopes = new[] { "gateway:invoke" } };

        foreach (var role in new[] { UserRole.SecurityOfficer, UserRole.Auditor })
        {
            var token = UserToken(await SeedUser(tenantId, role));
            (await Send(HttpMethod.Get, "/api/v1/api-clients", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Send(HttpMethod.Post, "/api/v1/api-clients", token, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        var clientToken = _factory.Services.GetRequiredService<ITokenService>().IssueClientToken(new ClientTokenClaims(Guid.NewGuid(), tenantId, ["gateway:invoke"]));
        (await Send(HttpMethod.Get, "/api/v1/api-clients", clientToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Get, "/api/v1/api-clients", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_body_cannot_set_the_tenant_owner_or_status_and_bad_fields_are_reported_one_by_one()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));

        var sneaky = await Send(HttpMethod.Post, "/api/v1/api-clients", admin,
            new { name = "x", scopes = new[] { "gateway:invoke" }, tenantId = Guid.NewGuid(), ownerUserId = Guid.NewGuid(), status = "Active" });
        sneaky.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(sneaky)).Should().Be("request.malformed");

        var bad = await Send(HttpMethod.Post, "/api/v1/api-clients", admin, new
        {
            name = "", scopes = new[] { "gateway:invoke", "gateway:root" }, ipAllowlist = new[] { "203.0.113.0/24", "nope" }, rateLimitPerMinute = 0
        });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var errors = (await Json(bad)).GetProperty("errors");
        errors.TryGetProperty("name", out _).Should().BeTrue();
        errors.TryGetProperty("scopes[1]", out _).Should().BeTrue();
        errors.TryGetProperty("ipAllowlist[1]", out _).Should().BeTrue();
        errors.TryGetProperty("rateLimitPerMinute", out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_client_name_is_unique_and_a_missing_default_policy_is_refused()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var named = new { name = "same", scopes = new[] { "gateway:invoke" } };

        await CreateClient(admin, named);
        (await Send(HttpMethod.Post, "/api/v1/api-clients", admin, named)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var unknownPolicy = await Send(HttpMethod.Post, "/api/v1/api-clients", admin,
            new { name = "other", scopes = new[] { "gateway:invoke" }, defaultPolicyId = Guid.NewGuid() });
        unknownPolicy.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(unknownPolicy)).Should().Be("validation.failed");
    }

    [Fact]
    public async Task A_developer_sees_and_manages_only_their_own_clients()
    {
        var tenantId = await SeedTenant();
        var dev1 = UserToken(await SeedUser(tenantId, UserRole.Developer));
        var dev2 = UserToken(await SeedUser(tenantId, UserRole.Developer));
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var (id, _, _) = await CreateClient(dev1);
        await CreateClient(dev2);

        (await Json(await Send(HttpMethod.Get, "/api/v1/api-clients", dev1))).GetProperty("total").GetInt32().Should().Be(1);
        (await Json(await Send(HttpMethod.Get, "/api/v1/api-clients", admin))).GetProperty("total").GetInt32().Should().Be(2);

        (await Send(HttpMethod.Get, $"/api/v1/api-clients/{id}", dev2)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Patch, $"/api/v1/api-clients/{id}", dev2, new { rateLimitPerMinute = 5 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Delete, $"/api/v1/api-clients/{id}", dev2)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await Send(HttpMethod.Patch, $"/api/v1/api-clients/{id}", dev1, new { rateLimitPerMinute = 5 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Patch, $"/api/v1/api-clients/{id}", admin, new { rateLimitPerMinute = 7 })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_change_a_client()
    {
        var tenantA = await SeedTenant();
        var tenantB = await SeedTenant();
        var adminA = UserToken(await SeedUser(tenantA, UserRole.TenantAdmin));
        var adminB = UserToken(await SeedUser(tenantB, UserRole.TenantAdmin));
        var (id, _, _) = await CreateClient(adminA);

        (await Send(HttpMethod.Get, $"/api/v1/api-clients/{id}", adminB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Patch, $"/api/v1/api-clients/{id}", adminB, new { rateLimitPerMinute = 5 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"/api/v1/api-clients/{id}/rotate-secret", adminB, new { graceHours = 1 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Delete, $"/api/v1/api-clients/{id}", adminB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(await Send(HttpMethod.Get, "/api/v1/api-clients", adminB))).GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Update_changes_what_is_given_and_an_empty_update_is_refused()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var (id, _, _) = await CreateClient(admin);

        var updated = await Json(await Send(HttpMethod.Patch, $"/api/v1/api-clients/{id}", admin,
            new { scopes = new[] { "gateway:detect" }, ipAllowlist = new[] { "203.0.113.9" }, rateLimitPerMinute = 42 }));
        updated.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).Should().Equal("gateway:detect");
        updated.GetProperty("ipAllowlist").EnumerateArray().Select(s => s.GetString()).Should().Equal("203.0.113.9/32");
        updated.GetProperty("rateLimitPerMinute").GetInt32().Should().Be(42);

        (await Send(HttpMethod.Patch, $"/api/v1/api-clients/{id}", admin, new { })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ---------------------------------------------------------------- /oauth/token

    [Fact]
    public async Task A_client_trades_its_secret_for_a_token_the_gateway_accepts()
    {
        var tenantId = await SeedTenant();
        var (_, clientId, secret) = await CreateClient(UserToken(await SeedUser(tenantId, UserRole.TenantAdmin)));

        var response = await TokenRequest(Credentials(clientId, secret));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Pragma.ToString().Should().Contain("no-cache");
        var body = await Json(response);
        body.GetProperty("token_type").GetString().Should().Be("Bearer");
        body.GetProperty("expires_in").GetInt32().Should().Be(900);
        body.GetProperty("scope").GetString().Should().Be("gateway:invoke gateway:detect gateway:deidentify");

        (await Detect(body.GetProperty("access_token").GetString()!)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_token_can_be_asked_for_with_http_basic_too_but_not_with_both_methods()
    {
        var tenantId = await SeedTenant();
        var (_, clientId, secret) = await CreateClient(UserToken(await SeedUser(tenantId, UserRole.TenantAdmin)));
        var grant = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };

        (await TokenRequest(grant, basicUser: clientId, basicPassword: secret)).StatusCode.Should().Be(HttpStatusCode.OK);

        var both = await TokenRequest(Credentials(clientId, secret), basicUser: clientId, basicPassword: secret);
        both.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        OAuthError(await Json(both)).Should().Be("invalid_request");

        var wrong = await TokenRequest(grant, basicUser: clientId, basicPassword: "vgs_wrong");
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        wrong.Headers.WwwAuthenticate.ToString().Should().StartWith("Basic");
    }

    [Fact]
    public async Task Wrong_secrets_unknown_clients_and_other_grants_get_the_standard_oauth_errors()
    {
        var tenantId = await SeedTenant();
        var (_, clientId, secret) = await CreateClient(UserToken(await SeedUser(tenantId, UserRole.TenantAdmin)));

        var wrong = await TokenRequest(Credentials(clientId, "vgs_wrong"));
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        OAuthError(await Json(wrong)).Should().Be("invalid_client");

        var unknown = await TokenRequest(Credentials("vg_doesnotexist", secret));
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        OAuthError(await Json(unknown)).Should().Be("invalid_client");

        var grant = Credentials(clientId, secret);
        grant["grant_type"] = "password";
        var other = await TokenRequest(grant);
        other.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        OAuthError(await Json(other)).Should().Be("unsupported_grant_type");

        var missing = await TokenRequest(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var json = await _client.PostAsJsonAsync("/oauth/token", new { grant_type = "client_credentials" });
        json.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task A_wrong_secret_leaves_a_security_event_and_an_unknown_client_does_not()
    {
        var tenantId = await SeedTenant();
        var (id, clientId, _) = await CreateClient(UserToken(await SeedUser(tenantId, UserRole.TenantAdmin)));

        await TokenRequest(Credentials(clientId, "vgs_wrong"));
        await TokenRequest(Credentials("vg_doesnotexist", "vgs_wrong"));

        await using var db = _pg.AdminContext();
        var events = await db.SecurityEvents.AsNoTracking().Where(e => e.TenantId == tenantId).ToListAsync();
        events.Should().ContainSingle();
        events[0].Type.Should().Be("oauth.invalid_secret");
        events[0].ClientId.Should().Be(id);
    }

    [Fact]
    public async Task A_token_carries_only_the_scopes_that_were_asked_for()
    {
        var tenantId = await SeedTenant();
        var (_, clientId, secret) = await CreateClient(UserToken(await SeedUser(tenantId, UserRole.TenantAdmin)));

        var token = await GetToken(clientId, secret, scope: "gateway:detect");

        (await Detect(token)).Should().Be(HttpStatusCode.OK);
        (await Deidentify(token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);   // the client may deidentify, but this token was not given that

        var tooMuch = await TokenRequest(Credentials(clientId, secret, "gateway:detect gateway:reidentify"));
        tooMuch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        OAuthError(await Json(tooMuch)).Should().Be("invalid_scope");
    }

    // ---------------------------------------------------------------- rotation and revocation

    [Fact]
    public async Task Rotating_gives_a_new_secret_and_keeps_the_old_one_working_for_the_grace_period()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var (id, clientId, oldSecret) = await CreateClient(admin);

        var rotated = await Send(HttpMethod.Post, $"/api/v1/api-clients/{id}/rotate-secret", admin, new { graceHours = 24 });

        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(rotated);
        var newSecret = body.GetProperty("clientSecret").GetString()!;
        newSecret.Should().StartWith("vgs_").And.NotBe(oldSecret);

        (await TokenRequest(Credentials(clientId, oldSecret))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TokenRequest(Credentials(clientId, newSecret))).StatusCode.Should().Be(HttpStatusCode.OK);

        // A second rotation while the first grace period runs is refused, and a bad grace period is a validation error.
        (await Send(HttpMethod.Post, $"/api/v1/api-clients/{id}/rotate-secret", admin, new { graceHours = 1 })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Send(HttpMethod.Post, $"/api/v1/api-clients/{id}/rotate-secret", admin, new { graceHours = 5 })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Rotating_needs_a_recent_second_factor_check()
    {
        var tenantId = await SeedTenant();
        var adminUser = await SeedUser(tenantId, UserRole.TenantAdmin);
        var (id, _, _) = await CreateClient(UserToken(adminUser));

        var stale = await Send(HttpMethod.Post, $"/api/v1/api-clients/{id}/rotate-secret", UserToken(adminUser, mfaAge: TimeSpan.FromHours(1)), new { graceHours = 1 });

        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoking_a_client_stops_its_tokens_at_once_and_stops_new_ones()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var (id, clientId, secret) = await CreateClient(admin);
        var token = await GetToken(clientId, secret);
        (await Detect(token)).Should().Be(HttpStatusCode.OK);

        (await Send(HttpMethod.Delete, $"/api/v1/api-clients/{id}", admin)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Detect(token)).Should().Be(HttpStatusCode.Unauthorized);   // the token is still unexpired and correctly signed, yet refused
        var again = await TokenRequest(Credentials(clientId, secret));
        again.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        OAuthError(await Json(again)).Should().Be("invalid_client");

        (await Send(HttpMethod.Delete, $"/api/v1/api-clients/{id}", admin)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Send(HttpMethod.Patch, $"/api/v1/api-clients/{id}", admin, new { rateLimitPerMinute = 9 })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var listed = await Json(await Send(HttpMethod.Get, "/api/v1/api-clients?status=Revoked", admin));
        listed.GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Creating_rotating_and_revoking_are_audited_without_the_secret()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var (id, clientId, secret) = await CreateClient(admin);
        await Send(HttpMethod.Post, $"/api/v1/api-clients/{id}/rotate-secret", admin, new { graceHours = 1 });
        await Send(HttpMethod.Delete, $"/api/v1/api-clients/{id}", admin);

        await using var db = _pg.AdminContext();
        var events = await db.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.ResourceType == "api_client").OrderBy(e => e.Seq).ToListAsync();

        events.Select(e => e.Action).Should().Equal("apiclient.create", "apiclient.rotate", "apiclient.revoke");
        events.Should().OnlyContain(e => e.Details["clientId"] == clientId && e.ResourceId == id);
        events.SelectMany(e => e.Details.Values).Should().NotContain(v => v.Contains(secret));
    }

    // ---------------------------------------------------------------- what a client's settings do at the gateway

    [Fact]
    public async Task A_clients_own_rate_limit_applies_to_its_tokens_and_no_other_clients()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var (_, slowId, slowSecret) = await CreateClient(admin, new { name = "slow", scopes = new[] { "gateway:detect" }, rateLimitPerMinute = 2 });
        var (_, busyId, busySecret) = await CreateClient(admin, new { name = "busy", scopes = new[] { "gateway:detect" }, rateLimitPerMinute = 1000 });
        var slow = await GetToken(slowId, slowSecret);
        var busy = await GetToken(busyId, busySecret);

        (await Detect(slow)).Should().Be(HttpStatusCode.OK);
        (await Detect(slow)).Should().Be(HttpStatusCode.OK);
        (await Detect(slow)).Should().Be(HttpStatusCode.TooManyRequests);
        (await Detect(busy)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_clients_default_policy_applies_when_the_request_names_none()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var policyId = await PublishBlockPhonePolicy(tenantId);
        var (_, withId, withSecret) = await CreateClient(admin, new { name = "strict", scopes = new[] { "gateway:deidentify" }, defaultPolicyId = policyId });
        var (_, plainId, plainSecret) = await CreateClient(admin, new { name = "plain", scopes = new[] { "gateway:deidentify" } });

        var strict = await Deidentify(await GetToken(withId, withSecret));
        var plain = await Deidentify(await GetToken(plainId, plainSecret));

        strict.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Code(await Json(strict)).Should().Be("policy.blocked");
        plain.StatusCode.Should().Be(HttpStatusCode.OK);   // the built-in template masks phone numbers rather than blocking them
    }

    [Fact]
    public async Task A_default_policy_that_is_archived_later_is_an_error_not_a_quiet_fallback()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var policyId = await PublishBlockPhonePolicy(tenantId);
        var (_, clientId, secret) = await CreateClient(admin, new { name = "strict", scopes = new[] { "gateway:deidentify" }, defaultPolicyId = policyId });
        var token = await GetToken(clientId, secret);
        (await Send(HttpMethod.Post, $"/api/v1/policies/{policyId}/archive", admin)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await Deidentify(token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Code(await Json(response)).Should().Be("policy.default_unavailable");
    }

    [Fact]
    public async Task A_client_limited_to_a_network_can_get_and_use_tokens_only_from_there()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var (_, clientId, secret) = await CreateClient(admin,
            new { name = "office-only", scopes = new[] { "gateway:detect" }, ipAllowlist = new[] { "203.0.113.0/24" } });
        var client = ClientWithTestIps();

        var outside = await TokenRequest(Credentials(clientId, secret), client, testIp: "198.51.100.7");
        outside.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        OAuthError(await Json(outside)).Should().Be("invalid_client");

        var token = await GetToken(clientId, secret, client: client, testIp: "203.0.113.9");
        (await Detect(token, client, testIp: "203.0.113.50")).Should().Be(HttpStatusCode.OK);
        (await Detect(token, client, testIp: "198.51.100.7")).Should().Be(HttpStatusCode.Unauthorized);   // a token carried to another network is useless

        await using var db = _pg.AdminContext();
        (await db.SecurityEvents.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Type == "oauth.ip_denied")).Should().BeTrue();
    }

    [Fact]
    public async Task The_oauth_endpoint_is_also_available_under_the_versioned_path()
    {
        var tenantId = await SeedTenant();
        var (_, clientId, secret) = await CreateClient(UserToken(await SeedUser(tenantId, UserRole.TenantAdmin)));

        var response = await _client.PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(Credentials(clientId, secret)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
