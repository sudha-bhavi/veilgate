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
using Microsoft.AspNetCore.Mvc.Testing;

namespace VeilGate.Api.Tests;

[Collection("postgres")]
public sealed class MfaApiTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string Start = "/api/v1/auth/mfa/totp/start";
    private const string Confirm = "/api/v1/auth/mfa/totp/confirm";
        private const string Verify = "/api/v1/auth/mfa/verify";
            private const string Refresh = "/api/v1/auth/refresh";
    private const string Logout = "/api/v1/auth/logout";

    private readonly PostgresFixture _pg;
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;

    public MfaApiTests(PostgresFixture pg)
    {
        _pg = pg;
        _factory = new AuthApiFactory(pg);
                // We handle cookies by hand, so the tests see exactly what the server sends.
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
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
        // ---------- sign in with the MFA code ----------

    // Sets up MFA completely, then signs in again from scratch. Returns the key and a fresh temporary token.
    private async Task<(User user, string key, string freshToken)> EnrolledUserAsync()
    {
        var user = await SeedUserAsync();
        var token = await MfaTokenAsync(user);
        var key = await StartAsync(token);
        (await PostAsync(Confirm, token, new { code = CodeFor(key) })).StatusCode.Should().Be(HttpStatusCode.OK);
        return (user, key, await MfaTokenAsync(user));   // a second, separate sign-in
    }

    [Fact]
    public async Task Full_sign_in_returns_an_access_token_that_works()
    {
        var (user, key, token) = await EnrolledUserAsync();

        // Login now says "enter your code" instead of "set up MFA".
        var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Password });
        using (var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("mfaRequired").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("mfaEnrollmentRequired").GetBoolean().Should().BeFalse();
        }

        // The confirm step already used the current window, so use the next one (still inside the allowed drift).
        var verify = await PostAsync(Verify, token, new { code = CodeFor(key, secondsFromNow: 30) });
        verify.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        var access = body.RootElement.GetProperty("accessToken").GetString()!;
        body.RootElement.GetProperty("tokenType").GetString().Should().Be("Bearer");

        var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var meResponse = await _client.SendAsync(me);

        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var meBody = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync());
        meBody.RootElement.GetProperty("tenantId").GetString().Should().Be(user.TenantId.ToString());
        meBody.RootElement.GetProperty("userId").GetString().Should().Be(user.Id.ToString());
    }

    [Fact]
    public async Task The_code_used_to_confirm_cannot_be_reused_to_sign_in()
    {
        var (_, key, token) = await EnrolledUserAsync();

        var r = await PostAsync(Verify, token, new { code = CodeFor(key) });   // same window as the confirm step

        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CodeOf(r)).Should().Be("mfa.code_invalid");
    }

    [Fact]
    public async Task A_wrong_code_does_not_sign_in()
    {
        var (_, key, token) = await EnrolledUserAsync();

        var r = await PostAsync(Verify, token, new { code = CodeFor(key, secondsFromNow: -7200) });

        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_access_token_cannot_be_used_at_the_verify_step()
    {
        var (user, key, _) = await EnrolledUserAsync();
        var tokens = _factory.Services.GetRequiredService<ITokenService>();
        var access = tokens.IssueAccessToken(new AccessTokenClaims(
            user.Id, user.TenantId, user.Role.ToString(), Guid.NewGuid(), ["pwd", "otp"], DateTimeOffset.UtcNow, user.TokenVersion));

        var r = await PostAsync(Verify, access, new { code = CodeFor(key, secondsFromNow: 30) });

        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Verify_before_mfa_is_set_up_is_refused()
    {
        var user = await SeedUserAsync();
        var token = await MfaTokenAsync(user);

        var r = await PostAsync(Verify, token, new { code = "123456" });

        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeOf(r)).Should().Be("mfa.not_enrolled");
    }
        // Runs setup to the end and returns the readable recovery codes from the confirm response.
    private async Task<(User user, List<string> codes)> EnrolledWithRecoveryCodesAsync()
    {
        var user = await SeedUserAsync();
        var token = await MfaTokenAsync(user);
        var key = await StartAsync(token);
        var confirm = await PostAsync(Confirm, token, new { code = CodeFor(key) });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await confirm.Content.ReadAsStringAsync());
        var codes = doc.RootElement.GetProperty("recoveryCodes").EnumerateArray().Select(e => e.GetString()!).ToList();
        return (user, codes);
    }

    [Fact]
    public async Task Confirm_returns_ten_recovery_codes_and_stores_only_their_hashes()
    {
        var (user, codes) = await EnrolledWithRecoveryCodesAsync();

        codes.Should().HaveCount(10);
        codes.Should().OnlyContain(c => System.Text.RegularExpressions.Regex.IsMatch(c, "^[A-Z2-9]{5}-[A-Z2-9]{5}$"));

        await using var db = _pg.AdminContext();
        var stored = await db.RecoveryCodes.Where(c => c.UserId == user.Id).Select(c => c.CodeHash).ToListAsync();
        stored.Should().HaveCount(10);
        stored.Should().NotIntersectWith(codes);   // the database holds hashes, never the readable codes
    }

    [Fact]
    public async Task A_recovery_code_signs_in_exactly_once()
    {
        var (user, codes) = await EnrolledWithRecoveryCodesAsync();

        var first = await PostAsync(Verify, await MfaTokenAsync(user), new { code = codes[0] });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await PostAsync(Verify, await MfaTokenAsync(user), new { code = codes[0] });
        again.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CodeOf(again)).Should().Be("mfa.code_invalid");

        // A different code from the same set still works.
        var other = await PostAsync(Verify, await MfaTokenAsync(user), new { code = codes[1] });
        other.StatusCode.Should().Be(HttpStatusCode.OK);
    }
        // ---------- sessions: refresh and logout ----------

    // The refresh token arrives only as a cookie. Pull its value out of the Set-Cookie header.
    private static string? RefreshCookieOf(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0])
                    .Where(p => p.StartsWith("vg_refresh=") && p.Length > "vg_refresh=".Length)
                    .Select(p => p["vg_refresh=".Length..])
                    .FirstOrDefault()
            : null;

    private async Task<HttpResponseMessage> PostWithCookieAsync(string path, string? refreshToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        if (refreshToken is not null) req.Headers.Add("Cookie", $"vg_refresh={refreshToken}");
        return await _client.SendAsync(req);
    }

    // A user who has fully signed in (password + MFA), and the refresh cookie they got.
    private async Task<(User user, string refreshToken, HttpResponseMessage verify)> SignedInAsync()
    {
        var (user, key, token) = await EnrolledUserAsync();
        var verify = await PostAsync(Verify, token, new { code = CodeFor(key, secondsFromNow: 30) });
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        return (user, RefreshCookieOf(verify)!, verify);
    }

    [Fact]
    public async Task Sign_in_sets_a_secure_http_only_refresh_cookie_and_keeps_it_out_of_the_body()
    {
        var (_, refreshToken, verify) = await SignedInAsync();

        refreshToken.Should().NotBeNullOrEmpty();
        var header = verify.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("vg_refresh=")).ToLowerInvariant();
        header.Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=strict").And.Contain("path=/api/v1/auth");
        (await verify.Content.ReadAsStringAsync()).Should().NotContain(refreshToken);
    }

    [Fact]
    public async Task Refresh_gives_a_working_access_token_and_a_new_cookie()
    {
        var (_, first, _) = await SignedInAsync();

        var r = await PostWithCookieAsync(Refresh, first);

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        RefreshCookieOf(r).Should().NotBeNullOrEmpty().And.NotBe(first);
        using var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("accessToken").GetString());
        (await _client.SendAsync(me)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Replaying_an_old_refresh_token_ends_the_whole_session()
    {
        var (_, first, _) = await SignedInAsync();
        var second = RefreshCookieOf(await PostWithCookieAsync(Refresh, first))!;

        var replay = await PostWithCookieAsync(Refresh, first);   // a thief uses the stolen old token

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CodeOf(replay)).Should().Be("auth.refresh_reused");
        (await PostWithCookieAsync(Refresh, second)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);   // the real user's token is dead too
    }

    [Fact]
    public async Task After_logout_the_refresh_token_no_longer_works()
    {
        var (_, token, _) = await SignedInAsync();

        (await PostWithCookieAsync(Logout, token)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await PostWithCookieAsync(Refresh, token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_without_a_cookie_is_rejected_and_logout_without_one_is_fine()
    {
        (await PostWithCookieAsync(Refresh, null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostWithCookieAsync(Logout, null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}