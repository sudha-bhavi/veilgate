using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

[Collection("postgres")]
public sealed class AuthApiTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly PostgresFixture _pg;
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;

    public AuthApiTests(PostgresFixture pg)
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

    // ---------- helpers ----------

    private async Task<(Tenant Tenant, User User)> SeedAsync(bool active = true, UserRole role = UserRole.Developer)
    {
        var sfx = Guid.NewGuid().ToString("N")[..10];
        var tenant = Tenant.Create($"Tenant {sfx}").Value;
        var user = User.Invite(tenant.Id, $"user-{sfx}@example.test", "Test User", role, DateTimeOffset.UtcNow).Value;
        if (active) user.AcceptInvitation(new Argon2PasswordHasher().Hash(Password), DateTimeOffset.UtcNow);

        await using var db = _pg.AdminContext();
        db.Add(tenant);
        await db.SaveChangesAsync();
        db.Add(user);
        await db.SaveChangesAsync();
        return (tenant, user);
    }

    private string TokenFor(User u, Guid? tenantId = null, int? version = null, DateTimeOffset? now = null)
    {
        var tokens = _factory.Services.GetRequiredService<ITokenService>();
        return tokens.IssueAccessToken(
            new AccessTokenClaims(u.Id, tenantId ?? u.TenantId, u.Role.ToString(), Guid.NewGuid(), ["pwd", "otp"], DateTimeOffset.UtcNow, version ?? u.TokenVersion),
            now);
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password, HttpClient? client = null)
        => (client ?? _client).PostAsJsonAsync("/api/v1/auth/login", new { email, password });

    private async Task<HttpResponseMessage> MeAsync(string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(req);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    // ---------- login ----------

    [Fact]
    public async Task Correct_password_returns_an_mfa_step_and_never_an_access_token()
    {
        var (_, user) = await SeedAsync();
        var r = await LoginAsync(user.Email, Password);

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("mfaEnrollmentRequired").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("mfaToken").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.TryGetProperty("accessToken", out _).Should().BeFalse("password alone must never be enough");
    }

    [Fact]
    public async Task Email_is_not_case_sensitive()
    {
        var (_, user) = await SeedAsync();
        (await LoginAsync(user.Email.ToUpperInvariant(), Password)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_look_identical()
    {
        var (_, user) = await SeedAsync();
        var wrong = await LoginAsync(user.Email, "not the password at all");
        var unknown = await LoginAsync($"nobody-{Guid.NewGuid():N}@example.test", "not the password at all");

        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CodeOf(wrong)).Should().Be("auth.invalid_credentials");
        (await CodeOf(unknown)).Should().Be("auth.invalid_credentials");
    }

    [Fact]
    public async Task Invited_user_who_has_not_set_a_password_cannot_sign_in()
    {
        var (_, user) = await SeedAsync(active: false);
        (await LoginAsync(user.Email, Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task User_of_a_suspended_tenant_cannot_sign_in()
    {
        var (tenant, user) = await SeedAsync();
        await using (var db = _pg.AdminContext())
        {
            var t = db.Tenants.Single(x => x.Id == tenant.Id);
            t.Suspend();
            await db.SaveChangesAsync();
        }
        (await LoginAsync(user.Email, Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        var (_, user) = await SeedAsync();
        for (var i = 0; i < User.MaxFailedAttempts; i++)
            (await LoginAsync(user.Email, "wrong password number " + i)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var locked = await LoginAsync(user.Email, Password);
        locked.StatusCode.Should().Be(HttpStatusCode.Locked);
        (await CodeOf(locked)).Should().Be("auth.account_locked");
    }

    [Fact]
    public async Task Unknown_json_fields_are_rejected()
    {
        var r = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "a@example.test", password = "x", isAdmin = true });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_fields_are_rejected()
    {
        var r = await _client.PostAsJsonAsync("/api/v1/auth/login", new { password = "x" });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_is_rate_limited_per_client()
    {
        using var limited = _factory.WithWebHostBuilder(b => b.UseSetting("Auth:LoginRateLimitPerMinute", "2"));
        using var client = limited.CreateClient();

        (await LoginAsync("x@example.test", "pw", client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync("x@example.test", "pw", client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var third = await LoginAsync("x@example.test", "pw", client);

        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        third.Headers.Contains("Retry-After").Should().BeTrue();
    }

    // ---------- tokens ----------

    [Fact]
    public async Task Request_without_a_token_is_rejected()
        => (await MeAsync(null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Valid_token_reaches_the_endpoint_and_carries_the_right_tenant()
    {
        var (tenant, user) = await SeedAsync(role: UserRole.TenantAdmin);
        var r = await MeAsync(TokenFor(user));

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("tenantId").GetString().Should().Be(tenant.Id.ToString());
        doc.RootElement.GetProperty("userId").GetString().Should().Be(user.Id.ToString());
        doc.RootElement.GetProperty("role").GetString().Should().Be("TenantAdmin");
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var (_, user) = await SeedAsync();
        var old = TokenFor(user, now: DateTimeOffset.UtcNow.AddHours(-1));
        (await MeAsync(old)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_with_an_edited_tenant_is_rejected_because_the_signature_no_longer_matches()
    {
        var (_, user) = await SeedAsync();
        var parts = TokenFor(user).Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1]));
        var forged = payload.Replace(user.TenantId.ToString(), Guid.NewGuid().ToString());
        forged.Should().NotBe(payload);

        var bad = $"{parts[0]}.{Base64UrlEncoder.Encode(forged)}.{parts[2]}";
        (await MeAsync(bad)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_naming_a_different_tenant_than_the_users_own_is_rejected()
    {
        var (_, userA) = await SeedAsync();
        var (tenantB, _) = await SeedAsync();
        // Correctly signed, but claims user A belongs to tenant B. Row-level security hides A from tenant B, so the request fails.
        (await MeAsync(TokenFor(userA, tenantId: tenantB.Id))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Old_token_version_is_rejected()
    {
        var (_, user) = await SeedAsync();
        (await MeAsync(TokenFor(user, version: user.TokenVersion + 1))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Disabling_a_user_kills_the_token_they_already_hold()
    {
        var (_, user) = await SeedAsync();
        var token = TokenFor(user);
        (await MeAsync(token)).StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = _pg.AdminContext())
        {
            var u = db.Users.Single(x => x.Id == user.Id);
            u.Disable(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        (await MeAsync(token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mfa_step_token_cannot_be_used_as_an_access_token()
    {
        var (_, user) = await SeedAsync();
        var login = await LoginAsync(user.Email, Password);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var mfaToken = doc.RootElement.GetProperty("mfaToken").GetString();

        (await MeAsync(mfaToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_signed_with_a_shared_secret_instead_of_the_private_key_is_rejected()
    {
        var (_, user) = await SeedAsync();
        var secret = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = AuthApiFactory.Kid };
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://veilgate.local",
            Audience = "veilgate-api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            TokenType = "at+jwt",
            SigningCredentials = new SigningCredentials(secret, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object> { ["sub"] = user.Id.ToString(), ["tid"] = user.TenantId.ToString(), ["role"] = "TenantAdmin", ["ver"] = 1 }
        });
        (await MeAsync(forged)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unsigned_token_with_alg_none_is_rejected()
    {
        var (_, user) = await SeedAsync();
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var header = Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"at+jwt\"}");
        var payload = Base64UrlEncoder.Encode(
            $"{{\"sub\":\"{user.Id}\",\"tid\":\"{user.TenantId}\",\"role\":\"TenantAdmin\",\"ver\":1,\"iss\":\"https://veilgate.local\",\"aud\":\"veilgate-api\",\"exp\":{exp}}}");
        (await MeAsync($"{header}.{payload}.")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------- JWKS ----------

    [Fact]
    public async Task Jwks_publishes_the_public_key_and_nothing_private()
    {
        var r = await _client.GetAsync("/.well-known/jwks.json");
        r.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var key = doc.RootElement.GetProperty("keys")[0];
        key.GetProperty("kty").GetString().Should().Be("EC");
        key.GetProperty("crv").GetString().Should().Be("P-256");
        key.GetProperty("alg").GetString().Should().Be("ES256");
        key.GetProperty("kid").GetString().Should().Be(AuthApiFactory.Kid);
        key.TryGetProperty("d", out _).Should().BeFalse("d is the private key");
        body.Should().NotContain("PRIVATE");
    }
}
