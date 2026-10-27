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

/// <summary>The policy workflow from the outside: real HTTP, real database and row-level security, real tokens.</summary>
[Collection("postgres")]
public sealed class PoliciesApiTests : IDisposable
{
    private const string BlockPhoneYaml = """
        version: 1
        name: block-phone
        defaults: { action: REDACT, minConfidence: 0.5 }
        entities:
          PHONE: { action: BLOCK }
        """;

    private const string PhoneText = "Call the patient on 513-555-0142 tomorrow.";

    private readonly PostgresFixture _pg;
    private readonly GatewayApiFactory _factory;
    private readonly HttpClient _client;

    public PoliciesApiTests(PostgresFixture pg)
    {
        _pg = pg;
        _factory = new GatewayApiFactory(pg);
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
        var tenant = Tenant.Create($"Pol {Guid.NewGuid():N}"[..16]).Value;
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

    private string ClientToken(Guid tenantId, params string[] scopes) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueClientToken(new ClientTokenClaims(Guid.NewGuid(), tenantId, scopes));

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token, object? body = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Get(string path, string? token) => Send(HttpMethod.Get, path, token);
    private Task<HttpResponseMessage> Post(string path, string? token, object? body = null) => Send(HttpMethod.Post, path, token, body);

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private async Task<(Guid id, string etag)> CreatePolicy(string token, string template = "blank")
    {
        var response = await Post("/api/v1/policies", token, new { name = $"Policy {Guid.NewGuid():N}"[..20], template });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await Json(response);
        return (body.GetProperty("id").GetGuid(), response.Headers.ETag!.Tag);
    }

    private async Task<string> SaveDraft(string token, Guid id, string etag, string yaml)
    {
        var response = await Send(HttpMethod.Put, $"/api/v1/policies/{id}/draft", token, new { yaml }, etag);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return response.Headers.ETag!.Tag;
    }

    private async Task Submit(string token, Guid id) =>
        (await Post($"/api/v1/policies/{id}/submit", token, new { summary = "Block phone numbers" })).StatusCode.Should().Be(HttpStatusCode.Accepted);

    /// <summary>Drives a policy to "published" with the given text: author writes and submits, a different officer publishes.</summary>
    private async Task<int> PublishText(Guid id, string etag, string yaml, string authorToken, string officerToken)
    {
        var newEtag = await SaveDraft(authorToken, id, etag, yaml);
        _ = newEtag;
        await Submit(authorToken, id);
        var published = await Post($"/api/v1/policies/{id}/publish", officerToken);
        published.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await Json(published)).GetProperty("versionNumber").GetInt32();
    }

    private async Task<List<string>> AuditResults(Guid tenantId, string action)
    {
        await using var db = _pg.AdminContext();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Action == action)
            .OrderBy(e => e.Seq).Select(e => e.Result).ToListAsync();
    }

    // ---------------------------------------------------------------- create and read

    [Fact]
    public async Task Creating_a_policy_returns_201_with_a_location_an_etag_and_a_draft_from_the_template()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));

        var response = await Post("/api/v1/policies", admin, new { name = "Default", template = "hipaa-safe-harbor", description = "first" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.ETag.Should().NotBeNull();
        var id = (await Json(response)).GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().Should().EndWith($"/api/v1/policies/{id}");

        var detail = await Json(await Get($"/api/v1/policies/{id}", admin));
        detail.GetProperty("status").GetString().Should().Be("Draft");
        detail.GetProperty("draft").GetProperty("yaml").GetString().Should().Contain("hipaa-safe-harbor-v1");
        detail.GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Only_a_tenant_admin_or_security_officer_may_create_a_policy()
    {
        var tenantId = await SeedTenant();
        var body = new { name = "Nope", template = "blank" };

        (await Post("/api/v1/policies", UserToken(await SeedUser(tenantId, UserRole.Developer)), body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Post("/api/v1/policies", UserToken(await SeedUser(tenantId, UserRole.Auditor)), body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Post("/api/v1/policies", ClientToken(tenantId, "gateway:invoke"), body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Post("/api/v1/policies", null, body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_auditor_can_read_policies_but_not_change_them()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var auditor = UserToken(await SeedUser(tenantId, UserRole.Auditor));
        var (id, _) = await CreatePolicy(admin);

        (await Get($"/api/v1/policies/{id}", auditor)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Get("/api/v1/policies", auditor)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Post($"/api/v1/policies/{id}/archive", auditor)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_request_body_cannot_set_the_tenant_or_any_other_unknown_field()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));

        var response = await Post("/api/v1/policies", admin, new { name = "Sneaky", template = "blank", tenantId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(response)).Should().Be("request.malformed");
    }

    [Fact]
    public async Task Create_checks_the_fields_and_the_name_must_be_unique()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));

        var bad = await Post("/api/v1/policies", admin, new { name = "", template = "nope" });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Code(await Json(bad)).Should().Be("validation.failed");

        (await Post("/api/v1/policies", admin, new { name = "Same", template = "blank" })).StatusCode.Should().Be(HttpStatusCode.Created);
        var dup = await Post("/api/v1/policies", admin, new { name = "Same", template = "blank" });
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---------------------------------------------------------------- drafts and If-Match

    [Fact]
    public async Task Saving_a_draft_needs_the_current_etag()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var (id, etag) = await CreatePolicy(admin);
        var path = $"/api/v1/policies/{id}/draft";

        var noHeader = await Send(HttpMethod.Put, path, admin, new { yaml = "version: 1\nname: x\n" });
        noHeader.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        Code(await Json(noHeader)).Should().Be("precondition.failed");

        (await Send(HttpMethod.Put, path, admin, new { yaml = "version: 1\nname: x\n" }, "\"stale\"")).StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);

        var saved = await Send(HttpMethod.Put, path, admin, new { yaml = BlockPhoneYaml }, etag);
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        saved.Headers.ETag!.Tag.Should().NotBe(etag);

        // The old etag is now stale: a second editor who loaded the page earlier cannot overwrite the first one's work.
        (await Send(HttpMethod.Put, path, admin, new { yaml = "version: 1\nname: y\n" }, etag)).StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task Validate_reports_problems_and_submit_refuses_text_that_does_not_validate()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var (id, etag) = await CreatePolicy(admin);
        await SaveDraft(admin, id, etag, "version: 1\nname: x\nbogus: 1\n");

        var report = await Json(await Post($"/api/v1/policies/{id}/validate", admin));
        report.GetProperty("valid").GetBoolean().Should().BeFalse();
        report.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
        report.GetProperty("errors")[0].TryGetProperty("line", out _).Should().BeTrue();

        var submit = await Post($"/api/v1/policies/{id}/submit", admin, new { summary = "Try to submit bad text" });
        submit.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Code(await Json(submit)).Should().Be("policy.invalid_definition");
    }

    // ---------------------------------------------------------------- the workflow

    [Fact]
    public async Task A_published_policy_is_what_the_gateway_enforces()
    {
        var tenantId = await SeedTenant();
        var author = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var officer = UserToken(await SeedUser(tenantId, UserRole.SecurityOfficer));
        var (id, etag) = await CreatePolicy(author);
        var gateway = ClientToken(tenantId, "gateway:deidentify");

        // Not published yet: the gateway does not know this policy.
        var before = await Post("/api/v1/gateway/deidentify", gateway, new { text = PhoneText, policyId = id });
        before.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var version = await PublishText(id, etag, BlockPhoneYaml, author, officer);
        version.Should().Be(1);

        var after = await Post("/api/v1/gateway/deidentify", gateway, new { text = PhoneText, policyId = id });
        after.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Code(await Json(after)).Should().Be("policy.blocked");

        // Without a policy id the built-in template still applies (phone numbers are masked, not blocked).
        (await Post("/api/v1/gateway/deidentify", gateway, new { text = PhoneText })).StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await Json(await Get($"/api/v1/policies/{id}", author));
        detail.GetProperty("status").GetString().Should().Be("Published");
        detail.GetProperty("current").GetProperty("versionNo").GetInt32().Should().Be(1);

        (await AuditResults(tenantId, "policy.create")).Should().Equal("success");
        (await AuditResults(tenantId, "policy.submit")).Should().Equal("success");
        (await AuditResults(tenantId, "policy.publish")).Should().Equal("success");
    }

    [Fact]
    public async Task Nobody_can_publish_what_they_submitted_themselves()
    {
        var tenantId = await SeedTenant();
        var officerUser = await SeedUser(tenantId, UserRole.SecurityOfficer);
        var officer = UserToken(officerUser);
        var (id, etag) = await CreatePolicy(officer);
        await SaveDraft(officer, id, etag, BlockPhoneYaml);
        await Submit(officer, id);

        var publish = await Post($"/api/v1/policies/{id}/publish", officer);

        publish.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Code(await Json(publish)).Should().Be("policy.self_approval");
        (await AuditResults(tenantId, "policy.publish")).Should().Equal("denied");
        (await Json(await Get($"/api/v1/policies/{id}", officer))).GetProperty("status").GetString().Should().Be("PendingApproval");
    }

    [Fact]
    public async Task Publishing_needs_a_recent_second_factor_check()
    {
        var tenantId = await SeedTenant();
        var author = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var officerUser = await SeedUser(tenantId, UserRole.SecurityOfficer);
        var (id, etag) = await CreatePolicy(author);
        await SaveDraft(author, id, etag, BlockPhoneYaml);
        await Submit(author, id);

        var stale = await Post($"/api/v1/policies/{id}/publish", UserToken(officerUser, mfaAge: TimeSpan.FromMinutes(10)));

        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Code(await Json(stale)).Should().Be("auth.insufficient_user_authentication");
        stale.Headers.WwwAuthenticate.ToString().Should().Contain("insufficient_user_authentication");

        (await Post($"/api/v1/policies/{id}/publish", UserToken(officerUser))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Only_a_security_officer_can_publish_or_reject()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var (id, etag) = await CreatePolicy(admin);
        await SaveDraft(admin, id, etag, BlockPhoneYaml);
        await Submit(admin, id);

        (await Post($"/api/v1/policies/{id}/publish", UserToken(await SeedUser(tenantId, UserRole.TenantAdmin)))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Post($"/api/v1/policies/{id}/reject", UserToken(await SeedUser(tenantId, UserRole.Developer)), new { reason = "no" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_rejected_draft_can_be_replaced_by_a_new_one()
    {
        var tenantId = await SeedTenant();
        var author = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var officer = UserToken(await SeedUser(tenantId, UserRole.SecurityOfficer));
        var (id, etag) = await CreatePolicy(author);
        await SaveDraft(author, id, etag, BlockPhoneYaml);
        await Submit(author, id);

        var rejected = await Post($"/api/v1/policies/{id}/reject", officer, new { reason = "SSN must be BLOCK" });
        rejected.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await Get($"/api/v1/policies/{id}", author);
        var newEtag = detail.Headers.ETag!.Tag;   // nothing is editable now, so the etag says so
        (await Json(detail)).GetProperty("draft").ValueKind.Should().Be(JsonValueKind.Null);

        var fresh = await Send(HttpMethod.Put, $"/api/v1/policies/{id}/draft", author, new { yaml = BlockPhoneYaml }, newEtag);
        fresh.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(fresh)).GetProperty("versionNo").GetInt32().Should().Be(2);
        (await AuditResults(tenantId, "policy.reject")).Should().Equal("success");
    }

    [Fact]
    public async Task Versions_diff_and_rollback()
    {
        var tenantId = await SeedTenant();
        var author = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var officer = UserToken(await SeedUser(tenantId, UserRole.SecurityOfficer));
        var (id, etag) = await CreatePolicy(author);
        (await PublishText(id, etag, BlockPhoneYaml, author, officer)).Should().Be(1);

        var current = await Get($"/api/v1/policies/{id}", author);
        var etagV1 = current.Headers.ETag!.Tag;
        const string allowPhone = "version: 1\nname: block-phone\ndefaults: { action: REDACT, minConfidence: 0.5 }\nentities:\n  PHONE: { action: MASK }\n";
        (await PublishText(id, etagV1, allowPhone, author, officer)).Should().Be(2);

        var versions = await Json(await Get($"/api/v1/policies/{id}/versions", author));
        versions.GetProperty("total").GetInt32().Should().Be(2);
        versions.GetProperty("items")[0].GetProperty("versionNo").GetInt32().Should().Be(2);   // newest first
        versions.GetProperty("items")[1].GetProperty("state").GetString().Should().Be("Archived");
        var v1Id = versions.GetProperty("items")[1].GetProperty("id").GetGuid();

        var diff = await Json(await Get($"/api/v1/policies/{id}/diff?from=1&to=2", author));
        var text = diff.GetProperty("unifiedDiff").GetString()!;
        text.Should().Contain("-  PHONE: { action: BLOCK }").And.Contain("+  PHONE: { action: MASK }");

        var rollback = await Post($"/api/v1/policies/{id}/rollback", officer, new { versionId = v1Id });
        rollback.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(rollback)).GetProperty("versionNumber").GetInt32().Should().Be(3);

        // Version 3 is live and says BLOCK again: the gateway refuses the phone number once more.
        var gateway = ClientToken(tenantId, "gateway:deidentify");
        (await Post("/api/v1/gateway/deidentify", gateway, new { text = PhoneText, policyId = id })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await AuditResults(tenantId, "policy.rollback")).Should().Equal("success");
    }

    [Fact]
    public async Task Rollback_needs_a_recent_second_factor_check()
    {
        var tenantId = await SeedTenant();
        var author = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var officerUser = await SeedUser(tenantId, UserRole.SecurityOfficer);
        var (id, _) = await CreatePolicy(author);

        var response = await Post($"/api/v1/policies/{id}/rollback", UserToken(officerUser, mfaAge: TimeSpan.FromHours(1)), new { versionId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------- isolation, archive, list, test

    [Fact]
    public async Task Another_tenant_cannot_see_or_change_a_policy()
    {
        var tenantA = await SeedTenant();
        var tenantB = await SeedTenant();
        var adminA = UserToken(await SeedUser(tenantA, UserRole.TenantAdmin));
        var officerB = UserToken(await SeedUser(tenantB, UserRole.SecurityOfficer));
        var (id, etag) = await CreatePolicy(adminA);

        (await Get($"/api/v1/policies/{id}", officerB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Put, $"/api/v1/policies/{id}/draft", officerB, new { yaml = "a: 1" }, etag)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Post($"/api/v1/policies/{id}/publish", officerB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Post($"/api/v1/policies/{id}/archive", officerB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(await Get("/api/v1/policies", officerB))).GetProperty("total").GetInt32().Should().Be(0);

        // And the gateway of tenant B cannot use tenant A's policy either.
        var gatewayB = ClientToken(tenantB, "gateway:deidentify");
        (await Post("/api/v1/gateway/deidentify", gatewayB, new { text = PhoneText, policyId = id })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_archived_policy_cannot_be_edited_or_used()
    {
        var tenantId = await SeedTenant();
        var author = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var officer = UserToken(await SeedUser(tenantId, UserRole.SecurityOfficer));
        var (id, etag) = await CreatePolicy(author);
        await PublishText(id, etag, BlockPhoneYaml, author, officer);
        var gateway = ClientToken(tenantId, "gateway:deidentify");
        (await Post("/api/v1/gateway/deidentify", gateway, new { text = PhoneText, policyId = id })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        (await Post($"/api/v1/policies/{id}/archive", author)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var detail = await Get($"/api/v1/policies/{id}", author);
        (await Json(detail)).GetProperty("status").GetString().Should().Be("Archived");
        (await Send(HttpMethod.Put, $"/api/v1/policies/{id}/draft", author, new { yaml = BlockPhoneYaml }, detail.Headers.ETag!.Tag)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Post("/api/v1/gateway/deidentify", gateway, new { text = PhoneText, policyId = id })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_list_filters_by_status_and_name_and_rejects_an_unknown_status()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        (await Post("/api/v1/policies", admin, new { name = "Research relaxed", template = "names-only" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Post("/api/v1/policies", admin, new { name = "Strict block", template = "blank" })).StatusCode.Should().Be(HttpStatusCode.Created);

        var drafts = await Json(await Get("/api/v1/policies?status=Draft", admin));
        drafts.GetProperty("total").GetInt32().Should().Be(2);

        var found = await Json(await Get("/api/v1/policies?q=research", admin));
        found.GetProperty("items").GetArrayLength().Should().Be(1);
        found.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Research relaxed");

        (await Json(await Get("/api/v1/policies?status=Published", admin))).GetProperty("total").GetInt32().Should().Be(0);
        (await Get("/api/v1/policies?status=Bogus", admin)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Testing_a_policy_masks_sample_text_without_storing_anything()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var developer = UserToken(await SeedUser(tenantId, UserRole.Developer));
        var (id, _) = await CreatePolicy(admin, "hipaa-safe-harbor");

        var response = await Post($"/api/v1/policies/{id}/test", developer, new { text = PhoneText });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("blocked").GetBoolean().Should().BeFalse();
        body.GetProperty("maskedText").GetString().Should().NotContain("513-555-0142").And.Contain("[PHONE]");
        body.GetProperty("entities")[0].GetProperty("type").GetString().Should().Be("PHONE");

        await using var db = _pg.AdminContext();
        (await db.VaultSessions.CountAsync(v => v.TenantId == tenantId)).Should().Be(0);

        (await Post($"/api/v1/policies/{id}/test", UserToken(await SeedUser(tenantId, UserRole.Auditor)), new { text = PhoneText }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Testing_a_policy_that_blocks_says_so()
    {
        var tenantId = await SeedTenant();
        var admin = UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));
        var (id, etag) = await CreatePolicy(admin);
        await SaveDraft(admin, id, etag, BlockPhoneYaml);

        var body = await Json(await Post($"/api/v1/policies/{id}/test", admin, new { text = PhoneText }));

        body.GetProperty("blocked").GetBoolean().Should().BeTrue();
        body.GetProperty("blockedTypes")[0].GetString().Should().Be("PHONE");
        body.GetProperty("maskedText").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
