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

/// <summary>
/// "My sessions" from the outside: real HTTP, real database and row-level security, real tokens. Each token points at a real
/// session row, so "the session in use" means what it means in production.
/// </summary>
[Collection("postgres")]
public sealed class SessionsApiTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresFixture _pg;
    private readonly AuthApiFactory _baseFactory;
    private readonly HttpClient _client;

    public SessionsApiTests(PostgresFixture pg)
    {
        _pg = pg;
        _baseFactory = new AuthApiFactory(pg);
        _client = _baseFactory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _baseFactory.Dispose();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Guid> SeedTenant()
    {
        var tenant = Tenant.Create($"Ses {Guid.NewGuid():N}"[..16]).Value;
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

    private async Task<Session> SeedSession(User user, string? userAgent = "Mozilla/5.0 Chrome/130", string ip = "203.0.113.7")
    {
        var hash = RefreshTokens.Hash(RefreshTokens.Generate());
        var session = Session.Start(user.TenantId, user.Id, hash, DateTimeOffset.UtcNow, ip: ip, userAgent: userAgent).Value;
        await using var db = _pg.AdminContext();
        db.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    /// <summary>The token of a person using this session. <paramref name="mfaAge"/> is how long ago they last confirmed a second factor.</summary>
    private string TokenFor(User u, Session s, TimeSpan? mfaAge = null) =>
        _baseFactory.Services.GetRequiredService<ITokenService>().IssueAccessToken(
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

    private async Task<Session> Reload(Guid id)
    {
        await using var db = _pg.AdminContext();
        return db.Sessions.Single(s => s.Id == id);
    }

    // ---------------------------------------------------------------- list

    [Fact]
    public async Task Listing_shows_my_active_sessions_with_the_one_in_use_marked()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user, "Chrome on a laptop");
        var phone = await SeedSession(user, "Safari on a phone", "198.51.100.4");

        var response = await Send(HttpMethod.Get, "/api/v1/auth/sessions", TokenFor(user, mine));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = (await Json(response)).EnumerateArray().ToList();
        rows.Should().HaveCount(2);
        rows.Single(r => r.GetProperty("current").GetBoolean()).GetProperty("id").GetGuid().Should().Be(mine.Id);
        var other = rows.Single(r => !r.GetProperty("current").GetBoolean());
        other.GetProperty("id").GetGuid().Should().Be(phone.Id);
        other.GetProperty("ip").GetString().Should().Be("198.51.100.4");
        other.GetProperty("userAgent").GetString().Should().Be("Safari on a phone");
    }

    [Fact]
    public async Task Listing_leaves_out_ended_sessions_and_other_peoples_sessions()
    {
        var tenantId = await SeedTenant();
        var user = await SeedUser(tenantId);
        var stranger = await SeedUser(tenantId);
        var mine = await SeedSession(user);
        var ended = await SeedSession(user);
        await SeedSession(stranger);
        await using (var db = _pg.AdminContext())
        {
            var row = db.Sessions.Single(s => s.Id == ended.Id);
            row.Revoke("logout", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var rows = (await Json(await Send(HttpMethod.Get, "/api/v1/auth/sessions", TokenFor(user, mine)))).EnumerateArray().ToList();

        rows.Should().ContainSingle().Which.GetProperty("id").GetGuid().Should().Be(mine.Id);
    }

    [Fact]
    public async Task Every_role_may_list_their_sessions()
    {
        var tenantId = await SeedTenant();
        foreach (var role in new[] { UserRole.TenantAdmin, UserRole.SecurityOfficer, UserRole.Developer, UserRole.Auditor })
        {
            var user = await SeedUser(tenantId, role);
            var session = await SeedSession(user);

            (await Send(HttpMethod.Get, "/api/v1/auth/sessions", TokenFor(user, session))).StatusCode
                .Should().Be(HttpStatusCode.OK, $"{role} has sessions too");
        }
    }

    [Fact]
    public async Task Listing_needs_a_sign_in()
    {
        (await Send(HttpMethod.Get, "/api/v1/auth/sessions", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------- revoke one

    [Fact]
    public async Task I_can_end_another_session_of_mine_and_it_is_audited()
    {
        var tenantId = await SeedTenant();
        var user = await SeedUser(tenantId);
        var mine = await SeedSession(user);
        var phone = await SeedSession(user);

        var response = await Send(HttpMethod.Delete, $"/api/v1/auth/sessions/{phone.Id}", TokenFor(user, mine));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Reload(phone.Id)).RevokeReason.Should().Be("revoked_by_user");
        (await Reload(mine.Id)).RevokedAt.Should().BeNull();
        await using var db = _pg.AdminContext();
        db.AuditEvents.Where(e => e.TenantId == tenantId && e.Action == "auth.session_revoke").Should().ContainSingle()
            .Which.ActorId.Should().Be(user.Id);
    }

    [Fact]
    public async Task The_session_in_use_cannot_be_ended_this_way()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);

        var response = await Send(HttpMethod.Delete, $"/api/v1/auth/sessions/{mine.Id}", TokenFor(user, mine));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Code(await Json(response)).Should().Be("session.current");
        (await Reload(mine.Id)).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Someone_elses_session_is_not_found_even_in_my_own_organization()
    {
        var tenantId = await SeedTenant();
        var user = await SeedUser(tenantId);
        var stranger = await SeedUser(tenantId);
        var mine = await SeedSession(user);
        var theirs = await SeedSession(stranger);

        var response = await Send(HttpMethod.Delete, $"/api/v1/auth/sessions/{theirs.Id}", TokenFor(user, mine));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Reload(theirs.Id)).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_session_of_another_organization_is_not_found()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);
        var foreign = await SeedSession(await SeedUser(await SeedTenant()));

        var response = await Send(HttpMethod.Delete, $"/api/v1/auth/sessions/{foreign.Id}", TokenFor(user, mine));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Reload(foreign.Id)).RevokedAt.Should().BeNull();
    }

    // ---------------------------------------------------------------- sign out all others

    [Fact]
    public async Task Signing_out_all_other_sessions_keeps_the_one_in_use()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);
        var a = await SeedSession(user);
        var b = await SeedSession(user);

        var response = await Send(HttpMethod.Post, "/api/v1/auth/logout-all", TokenFor(user, mine), new { keepCurrent = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(response)).GetProperty("ended").GetInt32().Should().Be(2);
        (await Reload(mine.Id)).RevokedAt.Should().BeNull();
        (await Reload(a.Id)).RevokeReason.Should().Be("logout_all");
        (await Reload(b.Id)).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Keeping_the_current_session_is_the_default()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);
        await SeedSession(user);

        var response = await Send(HttpMethod.Post, "/api/v1/auth/logout-all", TokenFor(user, mine), new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Reload(mine.Id)).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Signing_out_everywhere_also_ends_the_session_in_use()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);
        await SeedSession(user);

        var response = await Send(HttpMethod.Post, "/api/v1/auth/logout-all", TokenFor(user, mine), new { keepCurrent = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(response)).GetProperty("ended").GetInt32().Should().Be(2);
        (await Reload(mine.Id)).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Signing_out_all_others_needs_a_recent_second_factor_check()
    {
        var user = await SeedUser(await SeedTenant());
        var mine = await SeedSession(user);
        var other = await SeedSession(user);

        var response = await Send(HttpMethod.Post, "/api/v1/auth/logout-all", TokenFor(user, mine, TimeSpan.FromMinutes(30)), new { keepCurrent = true });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Code(await Json(response)).Should().Be("auth.insufficient_user_authentication");
        (await Reload(other.Id)).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Signing_out_all_others_never_touches_another_persons_sessions()
    {
        var tenantId = await SeedTenant();
        var user = await SeedUser(tenantId);
        var stranger = await SeedUser(tenantId);
        var mine = await SeedSession(user);
        var theirs = await SeedSession(stranger);

        await Send(HttpMethod.Post, "/api/v1/auth/logout-all", TokenFor(user, mine), new { keepCurrent = false });

        (await Reload(theirs.Id)).RevokedAt.Should().BeNull();
    }
}
