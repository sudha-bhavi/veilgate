using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Llm;
using VeilGate.Domain;
using VeilGate.Domain.Common;
using VeilGate.Domain.LlmProviders;
using VeilGate.Infrastructure.Persistence;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

/// <summary>The gateway from the outside: real HTTP, real database, real tokens, a scripted model.</summary>
[Collection("postgres")]
public sealed class GatewayApiTests : IDisposable
{
    private const string Note = "Patient John Smith, MRN 4471882, phone 513-555-0142, seen 03/14/2025.";
    private static readonly string[] Secrets = ["John Smith", "4471882", "513-555-0142", "03/14/2025"];
    private const string PersonToken = @"\[\[PERSON_[A-Z2-7]{4}\]\]";

    private readonly PostgresFixture _pg;
    private readonly GatewayApiFactory _factory;
    private readonly HttpClient _client;

    public GatewayApiTests(PostgresFixture pg)
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
        var tenant = Tenant.Create($"Gw {Guid.NewGuid():N}"[..16]).Value;
        await using var db = _pg.AdminContext();
        db.Add(tenant);
        // Chat needs a provider that serves the model. Its key is never decrypted here, because these tests replace the model call itself.
        db.Add(LlmProvider.Create(Guid.CreateVersion7(), tenant.Id, "test-llm", "http://test-llm.invalid", ["gpt-4o-mini"],
            new ProtectedApiKey([1], 1, "test")).Value);
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

    private string ClientToken(Guid clientId, Guid tenantId, params string[] scopes) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueClientToken(new ClientTokenClaims(clientId, tenantId, scopes));

    private string UserToken(User u) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(
            new AccessTokenClaims(u.Id, u.TenantId, u.Role.ToString(), Guid.NewGuid(), ["pwd", "otp"], DateTimeOffset.UtcNow, u.TokenVersion));

    private Task<HttpResponseMessage> Post(string path, object body, string? token, Action<HttpRequestMessage>? tweak = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        tweak?.Invoke(request);
        return (client ?? _client).SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static object ChatBody(string text = Note) => new { model = "gpt-4o-mini", messages = new[] { new { role = "user", content = text } } };

    private static Result<LlmResponse> Reply(string content) =>
        Result.Success(new LlmResponse("chatcmpl-test", "gpt-4o-mini", content, "stop", 11, 7, "test-llm"));

    // ---------------------------------------------------------- authorization

    [Fact]
    public async Task The_gateway_needs_a_token()
    {
        var response = await Post("/api/v1/gateway/detect", new { text = "x" }, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_persons_token_cannot_call_the_gateway()
    {
        var tenantId = await SeedTenant();
        var developer = await SeedUser(tenantId, UserRole.Developer);

        var response = await Post("/api/v1/gateway/detect", new { text = "x" }, UserToken(developer));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_client_needs_the_scope_of_the_endpoint_it_calls()
    {
        var tenantId = await SeedTenant();
        var token = ClientToken(Guid.NewGuid(), tenantId, "gateway:invoke");   // chat only

        (await Post("/api/v1/gateway/detect", new { text = "x" }, token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Post("/api/v1/gateway/deidentify", new { text = "x" }, token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Post("/api/v1/gateway/reidentify", new { vaultSessionId = Guid.NewGuid(), text = "x" }, token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Post("/api/v1/gateway/chat/completions", ChatBody("hello"), token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ----------------------------------------------------------------- detect

    [Fact]
    public async Task Detect_reports_where_the_entities_are_and_never_sends_the_text_back()
    {
        var tenantId = await SeedTenant();

        var response = await Post("/api/v1/gateway/detect", new { text = "Call 513-555-0142 now" }, ClientToken(Guid.NewGuid(), tenantId, "gateway:detect"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("513-555-0142");
        var body = JsonDocument.Parse(raw).RootElement;
        var entity = body.GetProperty("entities").EnumerateArray().Single();
        entity.GetProperty("type").GetString().Should().Be("PHONE");
        (entity.GetProperty("start").GetInt32(), entity.GetProperty("end").GetInt32()).Should().Be((5, 17));
        body.GetProperty("requestId").GetString().Should().StartWith("req_");
    }

    // -------------------------------------------------- deidentify / reidentify

    [Fact]
    public async Task Deidentify_returns_safe_text_and_reidentify_restores_a_token()
    {
        var tenantId = await SeedTenant();
        var token = ClientToken(Guid.NewGuid(), tenantId, "gateway:deidentify", "gateway:reidentify");

        var deidentified = await Post("/api/v1/gateway/deidentify", new { text = Note, subjectId = "patient-881" }, token);

        deidentified.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(deidentified);
        var safe = body.GetProperty("text").GetString()!;
        foreach (var secret in Secrets) safe.Should().NotContain(secret);
        var person = body.GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("type").GetString() == "PERSON");
        person.GetProperty("action").GetString().Should().Be("TOKENIZE");
        var personToken = person.GetProperty("token").GetString()!;
        var sessionId = body.GetProperty("vaultSessionId").GetGuid();

        var restored = await Post("/api/v1/gateway/reidentify", new { vaultSessionId = sessionId, text = $"Please call {personToken} tomorrow" }, token);

        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        var restoredBody = await Json(restored);
        restoredBody.GetProperty("text").GetString().Should().Be("Please call John Smith tomorrow");
        restoredBody.GetProperty("replaced").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_vault_session_is_invisible_to_other_clients_and_other_tenants()
    {
        var tenantA = await SeedTenant();
        var tenantB = await SeedTenant();
        var owner = ClientToken(Guid.NewGuid(), tenantA, "gateway:deidentify", "gateway:reidentify");
        var sameTenantOtherClient = ClientToken(Guid.NewGuid(), tenantA, "gateway:reidentify");
        var otherTenant = ClientToken(Guid.NewGuid(), tenantB, "gateway:reidentify");
        var sessionId = (await Json(await Post("/api/v1/gateway/deidentify", new { text = "Patient John Smith arrived." }, owner))).GetProperty("vaultSessionId").GetGuid();

        var neighbour = await Post("/api/v1/gateway/reidentify", new { vaultSessionId = sessionId, text = "x" }, sameTenantOtherClient);
        var stranger = await Post("/api/v1/gateway/reidentify", new { vaultSessionId = sessionId, text = "x" }, otherTenant);
        var missing = await Post("/api/v1/gateway/reidentify", new { vaultSessionId = Guid.NewGuid(), text = "x" }, owner);

        foreach (var response in new[] { neighbour, stranger, missing })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            Code(await Json(response)).Should().Be("vault.session_not_found");   // all three look exactly the same
        }
    }

    // ------------------------------------------------------------------- chat

    [Fact]
    public async Task Chat_protects_what_the_model_sees_and_restores_the_answer()
    {
        var tenantId = await SeedTenant();
        _factory.ModelReply = r => Reply($"Summary for {Regex.Match(r.Messages[0].Content, PersonToken).Value}.");

        var response = await Post("/api/v1/gateway/chat/completions", ChatBody(), ClientToken(Guid.NewGuid(), tenantId, "gateway:invoke", "gateway:reidentify"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("object").GetString().Should().Be("chat.completion");
        body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().Be("Summary for John Smith.");
        body.GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("stop");
        body.GetProperty("usage").GetProperty("total_tokens").GetInt32().Should().Be(18);
        var veilgate = body.GetProperty("veilgate");
        veilgate.GetProperty("restored").GetBoolean().Should().BeTrue();
        veilgate.GetProperty("policyVersion").GetInt32().Should().Be(1);
        veilgate.GetProperty("entitiesProtected").GetProperty("PERSON").GetInt32().Should().Be(1);

        var seenByModel = _factory.ModelRequests.Single().Messages[0].Content;
        foreach (var secret in Secrets) seenByModel.Should().NotContain(secret);
    }

    [Fact]
    public async Task Without_the_reidentify_scope_the_answer_keeps_its_tokens()
    {
        var tenantId = await SeedTenant();
        _factory.ModelReply = r => Reply($"Summary for {Regex.Match(r.Messages[0].Content, PersonToken).Value}.");

        var response = await Post("/api/v1/gateway/chat/completions", ChatBody(), ClientToken(Guid.NewGuid(), tenantId, "gateway:invoke"));

        var body = await Json(response);
        body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().MatchRegex($"^Summary for {PersonToken}\\.$");
        body.GetProperty("veilgate").GetProperty("restored").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("provider.unavailable", 502)]
    [InlineData("provider.timeout", 504)]
    public async Task A_model_failure_becomes_a_502_or_504_with_a_stable_code(string code, int status)
    {
        var tenantId = await SeedTenant();
        _factory.ModelReply = _ => Result.Failure<LlmResponse>(code == "provider.timeout" ? Error.ProviderTimeout() : Error.ProviderUnavailable());

        var response = await Post("/api/v1/gateway/chat/completions", ChatBody("hello"), ClientToken(Guid.NewGuid(), tenantId, "gateway:invoke"));

        ((int)response.StatusCode).Should().Be(status);
        Code(await Json(response)).Should().Be(code);
    }

    [Fact]
    public async Task The_policy_header_must_name_a_policy_that_exists()
    {
        var tenantId = await SeedTenant();
        var token = ClientToken(Guid.NewGuid(), tenantId, "gateway:invoke");

        var unknown = await Post("/api/v1/gateway/chat/completions", ChatBody("hello"), token, r => r.Headers.Add("X-VeilGate-Policy", Guid.NewGuid().ToString()));
        var garbage = await Post("/api/v1/gateway/chat/completions", ChatBody("hello"), token, r => r.Headers.Add("X-VeilGate-Policy", "not-a-guid"));

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        garbage.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ------------------------------------------------------ errors and limits

    [Fact]
    public async Task Invalid_input_is_a_422_that_names_the_field_and_does_not_repeat_the_input()
    {
        var tenantId = await SeedTenant();
        var token = ClientToken(Guid.NewGuid(), tenantId, "gateway:detect", "gateway:invoke");

        var empty = await Post("/api/v1/gateway/detect", new { text = "" }, token);
        var tooLong = await Post("/api/v1/gateway/chat/completions", ChatBody("Patient John Smith " + new string('a', 32_800)), token);

        empty.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var emptyBody = await Json(empty);
        Code(emptyBody).Should().Be("validation.failed");
        emptyBody.GetProperty("errors").GetProperty("text")[0].GetString().Should().Be("Text is required");

        tooLong.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var raw = await tooLong.Content.ReadAsStringAsync();
        raw.Should().Contain("messages[0].content").And.Contain("Maximum 32768 characters").And.NotContain("John Smith");
    }

    [Fact]
    public async Task Streaming_is_rejected_until_it_exists()
    {
        var tenantId = await SeedTenant();

        var response = await Post("/api/v1/gateway/chat/completions",
            new { model = "gpt-4o-mini", stream = true, messages = new[] { new { role = "user", content = "hi" } } },
            ClientToken(Guid.NewGuid(), tenantId, "gateway:invoke"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_tenant_id_in_the_body_is_rejected_as_an_unknown_property()
    {
        var tenantId = await SeedTenant();

        var response = await Post("/api/v1/gateway/detect", new { text = "hello", tenantId = Guid.NewGuid() }, ClientToken(Guid.NewGuid(), tenantId, "gateway:detect"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(response)).Should().Be("request.malformed");
    }

    [Fact]
    public async Task Only_json_is_accepted()
    {
        var tenantId = await SeedTenant();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/gateway/detect") { Content = new StringContent("text=hello", Encoding.UTF8, "text/plain") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ClientToken(Guid.NewGuid(), tenantId, "gateway:detect"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task A_busy_client_is_limited_without_affecting_another_client()
    {
        var tenantId = await SeedTenant();
        using var limited = new GatewayApiFactory(_pg, rateLimitPerMinute: 2);
        using var http = limited.CreateClient();
        var busy = limited.Services.GetRequiredService<ITokenService>().IssueClientToken(new ClientTokenClaims(Guid.NewGuid(), tenantId, ["gateway:detect"]));
        var calm = limited.Services.GetRequiredService<ITokenService>().IssueClientToken(new ClientTokenClaims(Guid.NewGuid(), tenantId, ["gateway:detect"]));

        (await Post("/api/v1/gateway/detect", new { text = "a" }, busy, client: http)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Post("/api/v1/gateway/detect", new { text = "a" }, busy, client: http)).StatusCode.Should().Be(HttpStatusCode.OK);
        var third = await Post("/api/v1/gateway/detect", new { text = "a" }, busy, client: http);

        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        third.Headers.RetryAfter.Should().NotBeNull();
        Code(await Json(third)).Should().Be("rate_limit.exceeded");
        (await Post("/api/v1/gateway/detect", new { text = "a" }, calm, client: http)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ audit

    [Fact]
    public async Task Every_call_is_audited_in_a_valid_chain_without_any_patient_data()
    {
        var tenantId = await SeedTenant();
        var clientId = Guid.NewGuid();
        var token = ClientToken(clientId, tenantId, "gateway:detect", "gateway:deidentify", "gateway:invoke");
        await Post("/api/v1/gateway/detect", new { text = Note }, token);
        await Post("/api/v1/gateway/deidentify", new { text = Note }, token);
        await Post("/api/v1/gateway/chat/completions", ChatBody(), token);

        await using var admin = _pg.AdminContext();
        var events = await admin.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenantId).OrderBy(e => e.Seq).ToListAsync();

        events.Select(e => e.Action).Should().Equal("gateway.detect", "gateway.deidentify", "gateway.chat");
        events.Should().OnlyContain(e => e.ActorType == "client" && e.ActorId == clientId && e.Result == "success");
        events.SelectMany(e => e.Details.Values).Should().NotContain(v => Secrets.Any(v.Contains));
        events[2].Details["entities"].Should().Be("DATE=1,MRN=1,PERSON=1,PHONE=1");
        (await new AuditChainVerifier(admin).VerifyAsync(tenantId)).Should().Match<ChainVerification>(v => v.Ok && v.Verified == 3);
    }

    // ------------------------------------------------------------- playground

    [Fact]
    public async Task The_playground_shows_every_stage_to_a_developer()
    {
        var tenantId = await SeedTenant();
        var developer = await SeedUser(tenantId, UserRole.Developer);
        _factory.ModelReply = r => Reply($"Summary for {Regex.Match(r.Messages[0].Content, PersonToken).Value}.");

        var response = await Post("/api/v1/playground/run", new { mode = "chat", text = Note, model = "gpt-4o-mini" }, UserToken(developer));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("maskedText").GetString().Should().Contain("[PHONE]").And.MatchRegex(PersonToken);
        body.GetProperty("llmResponseMasked").GetString().Should().MatchRegex($"^Summary for {PersonToken}\\.$");
        body.GetProperty("restoredResponse").GetString().Should().Be("Summary for John Smith.");

        await using var admin = _pg.AdminContext();
        var evt = await admin.AuditEvents.AsNoTracking().SingleAsync(e => e.TenantId == tenantId);
        (evt.ActorType, evt.ActorId).Should().Be(("user", developer.Id));
    }

    [Theory]
    [InlineData("detect")]
    [InlineData("deidentify")]
    public async Task The_playground_can_also_just_detect_or_deidentify(string mode)
    {
        var tenantId = await SeedTenant();
        var admin = await SeedUser(tenantId, UserRole.TenantAdmin);

        var response = await Post("/api/v1/playground/run", new { mode, text = "Call 513-555-0142 now" }, UserToken(admin));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(response)).GetProperty("entities").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task The_playground_is_for_developers_officers_and_admins_only()
    {
        var tenantId = await SeedTenant();
        var auditor = await SeedUser(tenantId, UserRole.Auditor);
        var body = new { mode = "detect", text = "hello" };

        (await Post("/api/v1/playground/run", body, UserToken(auditor))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Post("/api/v1/playground/run", body, ClientToken(Guid.NewGuid(), tenantId, "gateway:invoke"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Post("/api/v1/playground/run", body, token: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
