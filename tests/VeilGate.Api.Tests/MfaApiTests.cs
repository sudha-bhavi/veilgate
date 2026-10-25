using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

[Collection("postgres")]
public sealed class MfaApiTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string Start = "/api/v1/auth/mfa/totp/start";
    private const string Confirm = "/api/v1/auth/mfa/totp/confirm";

    private readonly PostgresFixture _pg;
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;

    public MfaApiTests(PostgresFixture pg)
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

    private async Task<User> SeedUserAsync()
    {
        var sfx = Guid.NewGuid().ToString("N")[..10];
        var tenant = Tenant.Create($"Tenant {sfx}").Value;
        var user = User.Invite(tenant.Id, $"user-{sfx}@example.test", "Test User", UserRole.Developer, DateTimeOffset.UtcNow).Value;
        user.AcceptInvitation(new Argon2PasswordHasher().Hash(Password), DateTimeOffset.UtcNow);

        await using var db = _pg.AdminContext();
        db.Add(tenant);
        await db.SaveChangesAsync();
        db.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    // Signs in with the password, exactly like a real client, and returns the MFA-step token.
    private async Task<string> MfaTokenAsync(User user)
    {
        var r = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Password });
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("mfaToken").GetString()!;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string? token, object? body = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        return await _client.SendAsync(req);
    }

    // Calls /start and returns the typed key the user would see.
    private async Task<string> StartAsync(string token)
    {
        var r = await PostAsync(Start, token);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("manualEntryKey").GetString()!;
    }

    private static string CodeFor(string manualKey, long secondsFromNow = 0) =>
        Totp.Compute(Base32.Decode(manualKey), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + secondsFromNow);

    private static async Task<string?> CodeOf(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    // ---------- tests ----------

    [Fact]
    public async Task Full_setup_flow_works_and_stores_the_secret_encrypted()
    {
        var user = await SeedUserAsync();
        var token = await MfaTokenAsync(user);

        var key = await StartAsync(token);
        var confirm = await PostAsync(Confirm, token, new { code = CodeFor(key) });

        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _pg.AdminContext();
        var factor = await db.MfaFactors.SingleAsync(f => f.UserId == user.Id);
        factor.IsConfirmed.Should().BeTrue();
        factor.ProtectedSecret.Should().StartWith("v1:").And.NotContain(key);
    }

    [Fact]
    public async Task Start_without_a_token_is_rejected()
        => (await PostAsync(Start, token: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task A_full_access_token_cannot_be_used_for_mfa_setup()
    {
        var user = await SeedUserAsync();
        var tokens = _factory.Services.GetRequiredService<ITokenService>();
        var access = tokens.IssueAccessToken(new AccessTokenClaims(
            user.Id, user.TenantId, user.Role.ToString(), Guid.NewGuid(), ["pwd", "otp"], DateTimeOffset.UtcNow, user.TokenVersion));

        (await PostAsync(Start, access)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_mfa_step_token_cannot_be_used_as_an_access_token()
    {
        var user = await SeedUserAsync();
        var token = await MfaTokenAsync(user);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await _client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_code_is_rejected_and_the_factor_stays_unconfirmed()
    {
        var user = await SeedUserAsync();
        var token = await MfaTokenAsync(user);
        var key = await StartAsync(token);

        var r = await PostAsync(Confirm, token, new { code = CodeFor(key, secondsFromNow: -7200) }); // two hours old

        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CodeOf(r)).Should().Be("mfa.code_invalid");

        await using var db = _pg.AdminContext();
        (await db.MfaFactors.SingleAsync(f => f.UserId == user.Id)).IsConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_before_start_is_not_found()
    {
        var user = await SeedUserAsync();
        var token = await MfaTokenAsync(user);

        var r = await PostAsync(Confirm, token, new { code = "123456" });

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("mfa.not_enrolled");
    }

    [Fact]
    public async Task Starting_again_after_confirming_is_a_conflict()
    {
        var user = await SeedUserAsync();
        var token = await MfaTokenAsync(user);
        var key = await StartAsync(token);
        (await PostAsync(Confirm, token, new { code = CodeFor(key) })).StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await PostAsync(Start, token);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(again)).Should().Be("mfa.already_enrolled");
    }

    [Fact]
    public async Task An_empty_code_is_a_validation_error()
    {
        var user = await SeedUserAsync();
        var token = await MfaTokenAsync(user);
        await StartAsync(token);

        var r = await PostAsync(Confirm, token, new { code = "" });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(r)).Should().Be("validation.failed");
    }
}