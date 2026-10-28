using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

[Collection("postgres")]
public sealed class StepUpApiTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string StepUp = "/api/v1/auth/step-up";

    private readonly PostgresFixture _pg;
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;

    public StepUpApiTests(PostgresFixture pg)
    {
        _pg = pg;
        _factory = new AuthApiFactory(pg);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> PostAsync(string path, string? token, object? body = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        return await _client.SendAsync(req);
    }

    private static string CodeFor(string manualKey, long secondsFromNow = 0) =>
        Totp.Compute(Base32.Decode(manualKey), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + secondsFromNow);

    private async Task<string> LoginMfaTokenAsync(User user)
    {
        var r = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Password });
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("mfaToken").GetString()!;
    }

    // Seeds a user and sets MFA up through the real endpoints. Returns the user and the typed-in key.
    private async Task<(User user, string key)> EnrolledUserAsync()
    {
        var sfx = Guid.NewGuid().ToString("N")[..10];
        var tenant = Tenant.Create($"Tenant {sfx}").Value;
        var user = User.Invite(tenant.Id, $"user-{sfx}@example.test", "Test User", UserRole.SecurityOfficer, DateTimeOffset.UtcNow).Value;
        user.AcceptInvitation(new Argon2PasswordHasher().Hash(Password), DateTimeOffset.UtcNow);

        await using (var db = _pg.AdminContext())
        {
            db.Add(tenant);
            await db.SaveChangesAsync();
            db.Add(user);
            await db.SaveChangesAsync();
        }

        var mfaToken = await LoginMfaTokenAsync(user);
        var start = await PostAsync("/api/v1/auth/mfa/totp/start", mfaToken);
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var key = doc.RootElement.GetProperty("manualEntryKey").GetString()!;

        (await PostAsync("/api/v1/auth/mfa/totp/confirm", mfaToken, new { code = CodeFor(key) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return (user, key);
    }

    // An access token whose last MFA check was an hour ago: the moment publishing asks to confirm again.
    private string StaleAccessToken(User user, Guid sessionId) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(new AccessTokenClaims(
            user.Id, user.TenantId, user.Role.ToString(), sessionId, ["pwd", "otp"],
            DateTimeOffset.UtcNow.AddHours(-1), user.TokenVersion));

    private static JsonElement ClaimsOf(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.Clone();
    }

    private static async Task<string?> CodeOf(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    // ---------- tests ----------

    [Fact]
    public async Task A_correct_code_returns_a_fresh_token_for_the_same_session()
    {
        var (user, key) = await EnrolledUserAsync();
        var sessionId = Guid.NewGuid();

        // The enrolment step used the current window, so use the next one (still inside the allowed drift).
        var r = await PostAsync(StepUp, StaleAccessToken(user, sessionId), new { code = CodeFor(key, secondsFromNow: 30) });

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var claims = ClaimsOf(body.RootElement.GetProperty("accessToken").GetString()!);
        claims.GetProperty("sid").GetString().Should().Be(sessionId.ToString());
        claims.GetProperty("auth_time").GetInt64().Should().BeCloseTo(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 60);
        claims.GetProperty("amr").EnumerateArray().Select(a => a.GetString()).Should().Contain("otp");
    }

    [Fact]
    public async Task A_wrong_code_is_rejected()
    {
        var (user, key) = await EnrolledUserAsync();

        var r = await PostAsync(StepUp, StaleAccessToken(user, Guid.NewGuid()), new { code = CodeFor(key, secondsFromNow: -7200) });

        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CodeOf(r)).Should().Be("mfa.code_invalid");
    }

    [Fact]
    public async Task The_code_used_to_enrol_cannot_be_replayed()
    {
        var (user, key) = await EnrolledUserAsync();

        var r = await PostAsync(StepUp, StaleAccessToken(user, Guid.NewGuid()), new { code = CodeFor(key) });

        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Without_a_token_it_is_rejected()
        => (await PostAsync(StepUp, token: null, new { code = "123456" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task The_temporary_sign_in_token_is_not_enough()
    {
        var (user, key) = await EnrolledUserAsync();
        var mfaToken = await LoginMfaTokenAsync(user);

        var r = await PostAsync(StepUp, mfaToken, new { code = CodeFor(key, secondsFromNow: 30) });

        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}