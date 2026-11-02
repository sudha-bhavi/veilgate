using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
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
/// Users and invitations from the outside: real HTTP, real database and row-level security, real tokens. Email goes to a fake
/// sender that remembers what it was given, so a test can read the link out of the email, exactly as a person would.
/// </summary>
[Collection("postgres")]
public sealed class UsersApiTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresFixture _pg;
    private readonly AuthApiFactory _baseFactory;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly FakeEmail _email = new();

    public UsersApiTests(PostgresFixture pg)
    {
        _pg = pg;
        _baseFactory = new AuthApiFactory(pg);
        _factory = _baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Console:BaseUrl", "http://console.test");
            b.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(_email));
        });
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private sealed class FakeEmail : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public bool Fail { get; set; }

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("smtp down");
            lock (Sent) Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Guid> SeedTenant()
    {
        var tenant = Tenant.Create($"Usr {Guid.NewGuid():N}"[..16]).Value;
        await using var db = _pg.AdminContext();
        db.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<User> SeedUser(Guid tenantId, UserRole role)
    {
        var sfx = Guid.NewGuid().ToString("N")[..10];
        var user = User.Invite(tenantId, $"user-{sfx}@example.test", $"Person {sfx}", role, DateTimeOffset.UtcNow).Value;
        user.AcceptInvitation(new Argon2PasswordHasher().Hash(Password), DateTimeOffset.UtcNow);
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

    /// <summary>The token out of the newest email sent to this address, the way a person would click the link.</summary>
    private string TokenFromEmail(string to)
    {
        string body;
        lock (_email.Sent) body = _email.Sent.Last(m => m.To == to).TextBody;
        return Regex.Match(body, "token=([A-Za-z0-9_-]+)").Groups[1].Value;
    }

    private async Task<string> Invite(string adminToken, string email, string role = "Developer")
    {
        var response = await Send(HttpMethod.Post, "/api/v1/users", adminToken, new { email, role });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return TokenFromEmail(email);
    }

    private static string NewEmail() => $"new-{Guid.NewGuid():N}@example.test";

    private Task<HttpResponseMessage> Login(string email, string password = Password) =>
        Send(HttpMethod.Post, "/api/v1/auth/login", null, new { email, password });

    /// <summary>Any call a Developer may make. A 401 means the person's token no longer works.</summary>
    private async Task<HttpStatusCode> TokenStatus(string token) =>
        (await Send(HttpMethod.Get, "/api/v1/api-clients", token)).StatusCode;

    // ---------------------------------------------------------------- invite, view, accept

    [Fact]
    public async Task An_invited_person_can_accept_and_then_sign_in()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var email = NewEmail();

        var invited = await Send(HttpMethod.Post, "/api/v1/users", admin, new { email, role = "Developer" });

        invited.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await Json(invited);
        body.GetProperty("status").GetString().Should().Be("Invited");
        body.GetProperty("emailSent").GetBoolean().Should().BeTrue();
        invited.Headers.Location!.ToString().Should().EndWith(body.GetProperty("id").GetGuid().ToString());
        var token = TokenFromEmail(email);

        // The database holds only a hash of the link's token.
        await using (var db = _pg.AdminContext())
        {
            var stored = await db.Invitations.AsNoTracking().SingleAsync(i => i.TenantId == tenantId);
            stored.TokenHash.Should().Be(RefreshTokens.Hash(token)).And.NotContain(token);
        }

        // Nobody is signed in on the accept page.
        var view = await Send(HttpMethod.Get, $"/api/v1/auth/invitations?token={token}", null);
        view.StatusCode.Should().Be(HttpStatusCode.OK);
        var shown = await Json(view);
        shown.GetProperty("email").GetString().Should().Be(email);
        shown.GetProperty("role").GetString().Should().Be("Developer");

        var accepted = await Send(HttpMethod.Post, "/api/v1/users/accept-invite", null, new { token, fullName = "Jordan Reyes", password = Password });
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        accepted.Headers.CacheControl!.NoStore.Should().BeTrue();
        var next = await Json(accepted);
        next.GetProperty("mfaToken").GetString().Should().NotBeNullOrWhiteSpace();
        next.GetProperty("mfaEnrollmentRequired").GetBoolean().Should().BeTrue();

        await using (var db = _pg.AdminContext())
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);
            user.Status.Should().Be(UserStatus.Active);
            user.DisplayName.Should().Be("Jordan Reyes");
            user.PasswordHash.Should().StartWith("$argon2id$");
        }

        // The link works once.
        var again = await Send(HttpMethod.Post, "/api/v1/users/accept-invite", null, new { token, fullName = "Jordan Reyes", password = Password });
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Code(await Json(again)).Should().Be("invitation.invalid");

        // And the new person can sign in with their password, then goes on to set up two-step sign-in.
        var login = await Login(email);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(login)).GetProperty("mfaEnrollmentRequired").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Inviting_is_audited_without_the_email_address()
    {
        var tenantId = await SeedTenant();
        var adminUser = await SeedUser(tenantId, UserRole.TenantAdmin);

        await Invite(UserToken(adminUser), NewEmail(), "Auditor");

        await using var db = _pg.AdminContext();
        var events = await db.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.Action == "user.invite").ToListAsync();
        var audited = events.Should().ContainSingle().Subject;
        audited.ActorId.Should().Be(adminUser.Id);
        audited.Details["role"].Should().Be("Auditor");
        audited.Details.Values.Should().NotContain(v => v.Contains('@'));
    }

    [Fact]
    public async Task Only_a_tenant_admin_may_manage_people()
    {
        var tenantId = await SeedTenant();
        var body = new { email = NewEmail(), role = "Developer" };

        foreach (var role in new[] { UserRole.SecurityOfficer, UserRole.Developer, UserRole.Auditor })
        {
            var token = UserToken(await SeedUser(tenantId, role));
            (await Send(HttpMethod.Get, "/api/v1/users", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Send(HttpMethod.Post, "/api/v1/users", token, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        var clientToken = _factory.Services.GetRequiredService<ITokenService>().IssueClientToken(new ClientTokenClaims(Guid.NewGuid(), tenantId, ["gateway:invoke"]));
        (await Send(HttpMethod.Get, "/api/v1/users", clientToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Get, "/api/v1/users", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_bad_invitation_is_refused_with_a_reason()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));

        var operatorRole = await Send(HttpMethod.Post, "/api/v1/users", admin, new { email = NewEmail(), role = "PlatformAdmin" });
        operatorRole.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(operatorRole)).GetProperty("errors").TryGetProperty("role", out _).Should().BeTrue();

        var sneaky = await Send(HttpMethod.Post, "/api/v1/users", admin, new { email = NewEmail(), role = "Developer", tenantId = Guid.NewGuid(), status = "Active" });
        sneaky.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(sneaky)).Should().Be("request.malformed");

        var notAnEmail = await Send(HttpMethod.Post, "/api/v1/users", admin, new { email = "not-an-email", role = "Developer" });
        notAnEmail.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_email_that_already_has_an_account_anywhere_is_refused()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var sameTenant = await SeedUser(tenantId, UserRole.Developer);
        var otherTenant = await SeedUser(await SeedTenant(), UserRole.Developer);

        foreach (var taken in new[] { sameTenant.Email, otherTenant.Email.ToUpperInvariant() })
        {
            var response = await Send(HttpMethod.Post, "/api/v1/users", admin, new { email = taken, role = "Developer" });
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            Code(await Json(response)).Should().Be("user.email_taken");
        }
    }

    [Fact]
    public async Task If_the_email_cannot_be_sent_the_person_is_still_invited_and_the_admin_is_told()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        _email.Fail = true;
        var email = NewEmail();

        var response = await Send(HttpMethod.Post, "/api/v1/users", admin, new { email, role = "Developer" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(response)).GetProperty("emailSent").GetBoolean().Should().BeFalse();
        await using var db = _pg.AdminContext();
        (await db.Users.AsNoTracking().SingleAsync(u => u.Email == email)).Status.Should().Be(UserStatus.Invited);
    }

    [Fact]
    public async Task Accepting_needs_a_strong_password_and_a_name_and_keeps_the_link_usable_after_a_refusal()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var email = NewEmail();
        var token = await Invite(admin, email);

        var weak = await Send(HttpMethod.Post, "/api/v1/users/accept-invite", null, new { token, fullName = "Jordan Reyes", password = "short" });
        weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(weak)).Should().Be("password.weak");

        var empty = await Send(HttpMethod.Post, "/api/v1/users/accept-invite", null, new { token = "", fullName = "", password = "" });
        empty.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var errors = (await Json(empty)).GetProperty("errors");
        errors.TryGetProperty("token", out _).Should().BeTrue();
        errors.TryGetProperty("fullName", out _).Should().BeTrue();
        errors.TryGetProperty("password", out _).Should().BeTrue();

        (await Send(HttpMethod.Post, "/api/v1/users/accept-invite", null, new { token, fullName = "Jordan Reyes", password = Password }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unknown_or_missing_link_looks_the_same()
    {
        foreach (var url in new[] { "/api/v1/auth/invitations?token=no-such-token", "/api/v1/auth/invitations" })
        {
            var response = await Send(HttpMethod.Get, url, null);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            Code(await Json(response)).Should().Be("invitation.invalid");
        }
    }

    // ---------------------------------------------------------------- resend

    [Fact]
    public async Task Resending_switches_the_old_link_off_and_sends_a_new_one()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var email = NewEmail();
        var oldToken = await Invite(admin, email);
        Guid personId;
        await using (var db = _pg.AdminContext()) personId = (await db.Users.AsNoTracking().SingleAsync(u => u.Email == email)).Id;

        var resend = await Send(HttpMethod.Post, $"/api/v1/users/{personId}/invite/resend", admin);

        resend.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(resend)).GetProperty("emailSent").GetBoolean().Should().BeTrue();
        var newToken = TokenFromEmail(email);
        newToken.Should().NotBe(oldToken);
        (await Send(HttpMethod.Get, $"/api/v1/auth/invitations?token={oldToken}", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, $"/api/v1/auth/invitations?token={newToken}", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Resending_to_someone_who_already_accepted_is_a_conflict()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var active = await SeedUser(tenantId, UserRole.Developer);

        var response = await Send(HttpMethod.Post, $"/api/v1/users/{active.Id}/invite/resend", admin);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Code(await Json(response)).Should().Be("user.not_invited");
    }

    // ---------------------------------------------------------------- list

    [Fact]
    public async Task Listing_shows_only_this_tenants_people_and_never_a_password_hash()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var developer = await SeedUser(tenantId, UserRole.Developer);
        await SeedUser(tenantId, UserRole.Auditor);
        var outsider = await SeedUser(await SeedTenant(), UserRole.Developer);

        var all = await Send(HttpMethod.Get, "/api/v1/users", admin);
        all.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await all.Content.ReadAsStringAsync();
        text.Should().NotContain("argon2").And.NotContain("passwordHash").And.NotContain(outsider.Email);
        using (var doc = JsonDocument.Parse(text)) doc.RootElement.GetProperty("total").GetInt32().Should().Be(3);

        var onlyDevelopers = await Json(await Send(HttpMethod.Get, "/api/v1/users?role=Developer", admin));
        onlyDevelopers.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(developer.Id);

        var search = await Json(await Send(HttpMethod.Get, $"/api/v1/users?q={developer.Email[..12]}", admin));
        search.GetProperty("total").GetInt32().Should().Be(1);

        (await Send(HttpMethod.Get, "/api/v1/users?status=Gone", admin)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------- change role

    [Fact]
    public async Task Changing_a_role_needs_a_recent_check_and_kills_the_old_tokens()
    {
        var tenantId = await SeedTenant();
        var adminUser = await SeedUser(tenantId, UserRole.TenantAdmin);
        var target = await SeedUser(tenantId, UserRole.Developer);
        var targetToken = UserToken(target);
        (await TokenStatus(targetToken)).Should().Be(HttpStatusCode.OK);

        var stale = await Send(HttpMethod.Patch, $"/api/v1/users/{target.Id}", UserToken(adminUser, TimeSpan.FromMinutes(10)), new { role = "Auditor" });
        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Code(await Json(stale)).Should().Be("auth.insufficient_user_authentication");

        var changed = await Send(HttpMethod.Patch, $"/api/v1/users/{target.Id}", UserToken(adminUser), new { role = "Auditor" });
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(changed)).GetProperty("role").GetString().Should().Be("Auditor");

        // The old token still says "Developer", so it must stop working.
        (await TokenStatus(targetToken)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task You_cannot_change_your_own_role_or_hand_out_the_operator_role()
    {
        var tenantId = await SeedTenant();
        var adminUser = await SeedUser(tenantId, UserRole.TenantAdmin);
        var target = await SeedUser(tenantId, UserRole.Developer);
        var admin = UserToken(adminUser);

        var self = await Send(HttpMethod.Patch, $"/api/v1/users/{adminUser.Id}", admin, new { role = "Developer" });
        self.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Code(await Json(self)).Should().Be("user.self_change");

        (await Send(HttpMethod.Patch, $"/api/v1/users/{target.Id}", admin, new { role = "PlatformAdmin" })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ---------------------------------------------------------------- disable and enable

    [Fact]
    public async Task Disabling_stops_the_person_at_once_and_enabling_lets_them_sign_in_again()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var target = await SeedUser(tenantId, UserRole.Developer);
        var targetToken = UserToken(target);

        (await Send(HttpMethod.Post, $"/api/v1/users/{target.Id}/disable", admin)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await TokenStatus(targetToken)).Should().Be(HttpStatusCode.Unauthorized);
        var refused = await Login(target.Email);
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Code(await Json(refused)).Should().Be("auth.invalid_credentials");   // no hint that the account exists

        (await Send(HttpMethod.Post, $"/api/v1/users/{target.Id}/enable", admin)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Login(target.Email)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task You_cannot_disable_yourself()
    {
        var tenantId = await SeedTenant();
        var adminUser = await SeedUser(tenantId, UserRole.TenantAdmin);

        var response = await Send(HttpMethod.Post, $"/api/v1/users/{adminUser.Id}/disable", UserToken(adminUser));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Code(await Json(response)).Should().Be("user.self_change");
    }

    // ---------------------------------------------------------------- reset two-step sign-in

    [Fact]
    public async Task Resetting_two_step_sign_in_needs_a_recent_check_and_sends_the_person_back_to_set_it_up()
    {
        var tenantId = await SeedTenant();
        var adminUser = await SeedUser(tenantId, UserRole.TenantAdmin);
        var target = await SeedUser(tenantId, UserRole.Developer);
        var now = DateTimeOffset.UtcNow;
        await using (var db = _pg.AdminContext())
        {
            var factor = MfaFactor.EnrollTotp(tenantId, target.Id, "v1:x", now).Value;
            factor.Confirm(1, now);
            db.Add(factor);
            db.Add(RecoveryCode.Create(tenantId, target.Id, RecoveryCodes.Hash(RecoveryCodes.Generate()[0]), now).Value);
            await db.SaveChangesAsync();
        }
        var targetToken = UserToken(target);

        // Before the reset the person is asked for a code.
        (await Json(await Login(target.Email))).GetProperty("mfaRequired").GetBoolean().Should().BeTrue();

        var stale = await Send(HttpMethod.Post, $"/api/v1/users/{target.Id}/mfa/reset", UserToken(adminUser, TimeSpan.FromMinutes(10)));
        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Code(await Json(stale)).Should().Be("auth.insufficient_user_authentication");

        (await Send(HttpMethod.Post, $"/api/v1/users/{target.Id}/mfa/reset", UserToken(adminUser))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var db = _pg.AdminContext())
        {
            (await db.MfaFactors.AsNoTracking().AnyAsync(f => f.UserId == target.Id)).Should().BeFalse();
            (await db.RecoveryCodes.AsNoTracking().AnyAsync(c => c.UserId == target.Id)).Should().BeFalse();
            (await db.AuditEvents.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Action == "user.mfa_reset" && e.ResourceId == target.Id)).Should().BeTrue();
            (await db.SecurityEvents.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Type == "mfa.reset" && e.UserId == target.Id)).Should().BeTrue();
        }

        (await TokenStatus(targetToken)).Should().Be(HttpStatusCode.Unauthorized);
        var next = await Json(await Login(target.Email));
        next.GetProperty("mfaRequired").GetBoolean().Should().BeFalse();
        next.GetProperty("mfaEnrollmentRequired").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task You_cannot_reset_your_own_two_step_sign_in()
    {
        var tenantId = await SeedTenant();
        var adminUser = await SeedUser(tenantId, UserRole.TenantAdmin);

        var response = await Send(HttpMethod.Post, $"/api/v1/users/{adminUser.Id}/mfa/reset", UserToken(adminUser));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Code(await Json(response)).Should().Be("user.self_change");
    }

    // ---------------------------------------------------------------- other tenants

    [Fact]
    public async Task A_person_of_another_tenant_is_simply_not_found()
    {
        var admin = UserToken(await SeedUser(await SeedTenant(), UserRole.TenantAdmin));
        var outsider = await SeedUser(await SeedTenant(), UserRole.Developer);

        (await Send(HttpMethod.Patch, $"/api/v1/users/{outsider.Id}", admin, new { role = "Auditor" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"/api/v1/users/{outsider.Id}/disable", admin)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"/api/v1/users/{outsider.Id}/enable", admin)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"/api/v1/users/{outsider.Id}/invite/resend", admin)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"/api/v1/users/{outsider.Id}/mfa/reset", admin)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
