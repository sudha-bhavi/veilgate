using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

/// <summary>
/// "My account" security from the outside: change password and recovery codes. Real HTTP, real database and row-level
/// security, real tokens and the real Argon2 hasher, so a password really is checked and really is replaced.
/// </summary>
[Collection("postgres")]
public sealed class AccountSecurityApiTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a different long passphrase";

    private readonly PostgresFixture _pg;
    private readonly AuthApiFactory _factory;
    private readonly HttpClient _client;

    public AccountSecurityApiTests(PostgresFixture pg)
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

    // ---------------------------------------------------------------- helpers

    private async Task<Guid> SeedTenant()
    {
        var tenant = Tenant.Create($"Acct {Guid.NewGuid():N}"[..16]).Value;
        await using var db = _pg.AdminContext();
        db.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<User> SeedUser(Guid tenantId, UserRole role = UserRole.Developer)
    {
        var sfx = Guid.NewGuid().ToString("N")[..10];
        var user = User.Invite(tenantId, $"user-{sfx}@example.test", $"Person {sfx}", role, DateTimeOffset.UtcNow).Value;
        user.AcceptInvitation(new Argon2PasswordHasher().Hash(Password), DateTimeOffset.UtcNow);
        await using var db = _pg.AdminContext();
        db.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Session> SeedSession(User user)
    {
        var session = Session.Start(user.TenantId, user.Id, RefreshTokens.Hash(RefreshTokens.Generate()), DateTimeOffset.UtcNow).Value;
        await using var db = _pg.AdminContext();
        db.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    /// <summary>A confirmed authenticator and <paramref name="total"/> recovery codes, <paramref name="used"/> of them already spent. Returns their hashes.</summary>
    private async Task<List<string>> SeedMfa(User user, int total = 10, int used = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var factor = MfaFactor.EnrollTotp(user.TenantId, user.Id, "protected", now).Value;
        factor.Confirm(1, now);
        var hashes = new List<string>();
        await using var db = _pg.AdminContext();
        db.Add(factor);
        for (var i = 0; i < total; i++)
        {
            var hash = RecoveryCodes.Hash(RecoveryCodes.Generate()[0]);
            var code = RecoveryCode.Create(user.TenantId, user.Id, hash, now).Value;
            if (i < used) code.MarkUsed(now);
            hashes.Add(hash);
            db.Add(code);
        }
        await db.SaveChangesAsync();
        return hashes;
    }

    /// <summary>The token of a person using this session. <paramref name="mfaAge"/> is how long ago they last confirmed a second factor.</summary>
    private string TokenFor(User u, Session s, TimeSpan? mfaAge = null) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(
            new AccessTokenClaims(u.Id, u.TenantId, u.Role.ToString(), s.Id, ["pwd", "otp"],
                DateTimeOffset.UtcNow - (mfaAge ?? TimeSpan.Zero), u.TokenVersion));

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private Task<HttpResponseMessage> Change(string token, string current, string fresh) =>
        Send(HttpMethod.Post, "/api/v1/auth/password/change", token, new { currentPassword = current, newPassword = fresh });

    private async Task<HttpStatusCode> LoginStatus(string email, string password) =>
        (await Send(HttpMethod.Post, "/api/v1/auth/login", null, new { email, password })).StatusCode;

    private async Task<Session> ReloadSession(Guid id)
    {
        await using var db = _pg.AdminContext();
        return db.Sessions.Single(s => s.Id == id);
    }

    // ---------------------------------------------------------------- change password

    [Fact]
    public async Task Changing_the_password_replaces_it_and_signs_out_every_other_device()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);
        var phone = await SeedSession(user);

        var response = await Change(TokenFor(user, mine), Password, NewPassword);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LoginStatus(user.Email, NewPassword)).Should().Be(HttpStatusCode.OK);
        (await LoginStatus(user.Email, Password)).Should().Be(HttpStatusCode.Unauthorized);
        (await ReloadSession(mine.Id)).RevokedAt.Should().BeNull();
        (await ReloadSession(phone.Id)).RevokeReason.Should().Be("password_change");
    }

    [Fact]
    public async Task Tokens_issued_before_the_change_stop_working_so_other_devices_are_out_at_once()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);
        var oldToken = TokenFor(user, mine);

        await Change(oldToken, Password, NewPassword);

        (await Send(HttpMethod.Get, "/api/v1/auth/sessions", oldToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_change_is_audited_and_raises_a_security_event()
    {
        var tenantId = await SeedTenant();
        var user = await SeedUser(tenantId);
        var mine = await SeedSession(user);

        await Change(TokenFor(user, mine), Password, NewPassword);

        await using var db = _pg.AdminContext();
        db.AuditEvents.Where(e => e.TenantId == tenantId && e.Action == "auth.password_change" && e.Result == "success")
            .Should().ContainSingle().Which.ActorId.Should().Be(user.Id);
        db.SecurityEvents.Where(e => e.TenantId == tenantId && e.Type == "auth.password_changed")
            .Should().ContainSingle().Which.UserId.Should().Be(user.Id);
    }

    [Fact]
    public async Task A_wrong_current_password_is_refused_and_the_old_one_still_works()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);

        var response = await Change(TokenFor(user, mine), "not my password at all", NewPassword);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(response)).Should().Be("password.current_incorrect");
        (await LoginStatus(user.Email, Password)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Wrong_current_passwords_count_toward_the_lockout()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);
        var token = TokenFor(user, mine);

        for (var i = 0; i < User.MaxFailedAttempts; i++) await Change(token, "wrong wrong wrong", NewPassword);
        var locked = await Change(token, Password, NewPassword);

        locked.StatusCode.Should().Be(HttpStatusCode.Locked);
        (await LoginStatus(user.Email, Password)).Should().Be(HttpStatusCode.Locked);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("password1234")]
    public async Task A_weak_new_password_is_refused(string weak)
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);

        var response = await Change(TokenFor(user, mine), Password, weak);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(response)).Should().Be("password.weak");
    }

    [Fact]
    public async Task The_same_password_again_is_refused()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);

        var response = await Change(TokenFor(user, mine), Password, Password);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(response)).Should().Be("password.same_as_current");
    }

    [Fact]
    public async Task Changing_the_password_needs_a_sign_in()
    {
        (await Change(null!, Password, NewPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------- mfa status

    [Fact]
    public async Task The_status_shows_the_authenticator_and_the_recovery_codes_left()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);
        await SeedMfa(user, total: 10, used: 3);

        var response = await Send(HttpMethod.Get, "/api/v1/auth/mfa/factors", TokenFor(user, mine));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("authenticator").GetProperty("addedAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        body.GetProperty("recoveryCodesLeft").GetInt32().Should().Be(7);
        body.GetProperty("recoveryCodesTotal").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task Without_an_authenticator_the_status_is_empty()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);

        var body = await Json(await Send(HttpMethod.Get, "/api/v1/auth/mfa/factors", TokenFor(user, mine)));

        body.GetProperty("authenticator").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("recoveryCodesLeft").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task The_status_never_counts_another_persons_codes()
    {
        var tenantId = await SeedTenant();
        var user = await SeedUser(tenantId);
        var other = await SeedUser(tenantId);
        var mine = await SeedSession(user);
        await SeedMfa(user, total: 4);
        await SeedMfa(other, total: 10);

        var body = await Json(await Send(HttpMethod.Get, "/api/v1/auth/mfa/factors", TokenFor(user, mine)));

        body.GetProperty("recoveryCodesTotal").GetInt32().Should().Be(4);
    }

    // ---------------------------------------------------------------- regenerate recovery codes

    [Fact]
    public async Task Regenerating_returns_ten_new_codes_once_and_the_old_ones_stop_working()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);
        var oldHashes = await SeedMfa(user, total: 10, used: 2);

        var response = await Send(HttpMethod.Post, "/api/v1/auth/mfa/recovery-codes/regenerate", TokenFor(user, mine));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var codes = (await Json(response)).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        codes.Should().HaveCount(10).And.OnlyHaveUniqueItems();

        await using var db = _pg.AdminContext();
        var stored = db.RecoveryCodes.Where(c => c.UserId == user.Id).ToList();
        stored.Should().HaveCount(10);
        stored.Should().OnlyContain(c => !c.IsUsed);
        stored.Select(c => c.CodeHash).Should().BeEquivalentTo(codes.Select(RecoveryCodes.Hash));
        stored.Select(c => c.CodeHash).Should().NotIntersectWith(oldHashes);
    }

    [Fact]
    public async Task Regenerating_is_audited_and_raises_a_security_event_without_the_codes()
    {
        var tenantId = await SeedTenant();
        var user = await SeedUser(tenantId);
        var mine = await SeedSession(user);
        await SeedMfa(user);

        await Send(HttpMethod.Post, "/api/v1/auth/mfa/recovery-codes/regenerate", TokenFor(user, mine));

        await using var db = _pg.AdminContext();
        db.AuditEvents.Where(e => e.TenantId == tenantId && e.Action == "auth.recovery_codes_regenerate")
            .Should().ContainSingle().Which.Details.Should().BeEmpty();
        db.SecurityEvents.Where(e => e.TenantId == tenantId && e.Type == "mfa.recovery_codes_regenerated").Should().ContainSingle();
    }

    [Fact]
    public async Task Regenerating_needs_a_recent_second_factor_check_and_changes_nothing_without_it()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);
        var oldHashes = await SeedMfa(user);

        var response = await Send(HttpMethod.Post, "/api/v1/auth/mfa/recovery-codes/regenerate", TokenFor(user, mine, TimeSpan.FromMinutes(30)));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Code(await Json(response)).Should().Be("auth.insufficient_user_authentication");
        await using var db = _pg.AdminContext();
        db.RecoveryCodes.Where(c => c.UserId == user.Id).Select(c => c.CodeHash).Should().BeEquivalentTo(oldHashes);
    }

    [Fact]
    public async Task Regenerating_needs_an_authenticator_to_be_set_up()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);

        var response = await Send(HttpMethod.Post, "/api/v1/auth/mfa/recovery-codes/regenerate", TokenFor(user, mine));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Code(await Json(response)).Should().Be("mfa.not_enrolled");
    }

    [Fact]
    public async Task Regenerating_never_touches_another_persons_codes()
    {
        var tenantId = await SeedTenant();
        var user = await SeedUser(tenantId);
        var other = await SeedUser(tenantId);
        var mine = await SeedSession(user);
        await SeedMfa(user);
        var theirs = await SeedMfa(other);

        await Send(HttpMethod.Post, "/api/v1/auth/mfa/recovery-codes/regenerate", TokenFor(user, mine));

        await using var db = _pg.AdminContext();
        db.RecoveryCodes.Where(c => c.UserId == other.Id).Select(c => c.CodeHash).Should().BeEquivalentTo(theirs);
    }
}
