using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Llm;
using VeilGate.Domain;
using VeilGate.Infrastructure.Llm;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

/// <summary>
/// Language model providers from the outside: real HTTP, real database and row-level security, real envelope encryption.
/// Where a test needs a provider to answer, the network is replaced by a scripted handler; nothing leaves the machine.
/// </summary>
[Collection("postgres")]
public sealed class LlmProvidersApiTests : IDisposable
{
    private const string Key = "sk-test-key-1234";
    private const string Note = "Patient John Smith, MRN 4471882, phone 513-555-0142, seen 03/14/2025.";
    private static readonly string[] Secrets = ["John Smith", "4471882", "513-555-0142", "03/14/2025"];
    private const string OkBody = """{"id":"c1","model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"Mock reply"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""";

    private readonly PostgresFixture _pg;
    private readonly GatewayApiFactory _factory;
    private readonly HttpClient _client;
    private readonly List<IDisposable> _disposables = [];

    public LlmProvidersApiTests(PostgresFixture pg)
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
        var tenant = Tenant.Create($"Llm {Guid.NewGuid():N}"[..16]).Value;
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

    private async Task<string> AdminToken(Guid tenantId) => UserToken(await SeedUser(tenantId, UserRole.TenantAdmin));

    private string UserToken(User u) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(
            new AccessTokenClaims(u.Id, u.TenantId, u.Role.ToString(), Guid.NewGuid(), ["pwd", "otp"], DateTimeOffset.UtcNow, u.TokenVersion));

    private string ClientToken(Guid tenantId, params string[] scopes) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueClientToken(new ClientTokenClaims(Guid.NewGuid(), tenantId, scopes));

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

    private static object ProviderBody(string name = "openai", string url = "https://203.0.113.10", string apiKey = Key, string[]? models = null, int? priority = null) =>
        new { name, baseUrl = url, apiKey, models = models ?? ["gpt-4o-mini"], priority };

    private async Task<Guid> Connect(string token, object? body = null, HttpClient? client = null)
    {
        var response = await Send(HttpMethod.Post, "/api/v1/llm-providers", token, body ?? ProviderBody(), client);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    /// <summary>What the "provider" saw and what it answers. Routes by host name, so several providers can share one handler.</summary>
    private sealed class FakeProviderHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = _ => Ok();
        public readonly List<(string Host, string? Authorization, string Body)> Requests = [];

        public static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent(OkBody, Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.RequestUri!.Host, request.Headers.Authorization?.ToString(), body));
            return Reply(request);
        }
    }

    /// <summary>The real gateway and the real catalog, with only the network replaced by the scripted handler.</summary>
    private HttpClient ClientWithRealGateway(FakeProviderHandler handler)
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddScoped<ILlmGateway, LlmGateway>();   // replaces the scripted model of the base factory
            s.AddSingleton(new ProviderClientCache(
                Microsoft.Extensions.Options.Options.Create(new LlmOptions { AllowPrivateNetworks = true, RetryBaseDelayMs = 1 }), _ => handler));
        }));
        _disposables.Add(factory);
        return factory.CreateClient();
    }

    /// <summary>As production behaves: no private networks, so internal addresses are refused when a provider is saved.</summary>
    private HttpClient StrictClient()
    {
        var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Llm:AllowPrivateNetworks", "false"));
        _disposables.Add(factory);
        return factory.CreateClient();
    }

    private Task<HttpResponseMessage> Chat(string token, HttpClient client, string text = Note, string model = "gpt-4o-mini") =>
        Send(HttpMethod.Post, "/api/v1/gateway/chat/completions", token,
            new { model, messages = new[] { new { role = "user", content = text } } }, client);

    private async Task<List<(string Action, string Result)>> Audit(Guid tenantId)
    {
        await using var db = _pg.AdminContext();
        return (await db.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.ResourceType == "llm_provider")
            .OrderBy(e => e.Seq).Select(e => new { e.Action, e.Result }).ToListAsync()).Select(e => (e.Action, e.Result)).ToList();
    }

    // ---------------------------------------------------------------- connecting and reading

    [Fact]
    public async Task Connecting_a_provider_returns_201_and_shows_only_the_last_four_characters_of_the_key()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);

        var response = await Send(HttpMethod.Post, "/api/v1/llm-providers", admin,
            new { name = "OpenAI", baseUrl = "https://203.0.113.10/", apiKey = Key, models = new[] { "gpt-4o-mini", "gpt-4o" }, timeoutSeconds = 45 });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await Json(response);
        body.GetProperty("name").GetString().Should().Be("OpenAI");
        body.GetProperty("baseUrl").GetString().Should().Be("https://203.0.113.10");
        body.GetProperty("keyLast4").GetString().Should().Be("1234");
        body.GetProperty("timeoutSeconds").GetInt32().Should().Be(45);
        body.GetProperty("priority").GetInt32().Should().Be(1);
        body.GetProperty("enabled").GetBoolean().Should().BeTrue();
        response.Headers.Location!.ToString().Should().EndWith(body.GetProperty("id").GetGuid().ToString());
    }

    [Fact]
    public async Task The_key_never_comes_back_not_in_any_response_and_not_as_plaintext_in_the_database()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var created = await Send(HttpMethod.Post, "/api/v1/llm-providers", admin, ProviderBody());
        var id = (await Json(created)).GetProperty("id").GetGuid();

        var texts = new[]
        {
            await created.Content.ReadAsStringAsync(),
            await (await Send(HttpMethod.Get, $"/api/v1/llm-providers/{id}", admin)).Content.ReadAsStringAsync(),
            await (await Send(HttpMethod.Get, "/api/v1/llm-providers", admin)).Content.ReadAsStringAsync(),
            await (await Send(HttpMethod.Put, $"/api/v1/llm-providers/{id}", admin, new { name = "openai", baseUrl = "https://203.0.113.10", models = new[] { "gpt-4o" } })).Content.ReadAsStringAsync(),
        };
        texts.Should().OnlyContain(t => !t.Contains(Key) && !t.Contains("sk-test") && !t.Contains("apiKey") && !t.Contains("cipher"));

        await using var db = _pg.AdminContext();
        var raw = (await db.LlmProviders.AsNoTracking().SingleAsync(p => p.Id == id)).ApiKeyCipher;
        Encoding.UTF8.GetString(raw).Should().NotContain("sk-test");
    }

    [Fact]
    public async Task Only_a_tenant_admin_may_manage_providers()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var id = await Connect(admin);

        foreach (var role in new[] { UserRole.SecurityOfficer, UserRole.Developer, UserRole.Auditor })
        {
            var token = UserToken(await SeedUser(tenantId, role));
            (await Send(HttpMethod.Get, "/api/v1/llm-providers", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Send(HttpMethod.Get, $"/api/v1/llm-providers/{id}", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Send(HttpMethod.Post, "/api/v1/llm-providers", token, ProviderBody("x"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Send(HttpMethod.Put, "/api/v1/llm-providers/order", token, new { ids = new[] { id } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Send(HttpMethod.Post, $"/api/v1/llm-providers/{id}/test", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Send(HttpMethod.Delete, $"/api/v1/llm-providers/{id}", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await Send(HttpMethod.Get, "/api/v1/llm-providers", ClientToken(tenantId, "gateway:invoke"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Get, "/api/v1/llm-providers", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_body_cannot_set_the_tenant_or_any_unknown_field_and_bad_fields_are_reported_one_by_one()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);

        var sneaky = await Send(HttpMethod.Post, "/api/v1/llm-providers", admin,
            new { name = "x", baseUrl = "https://203.0.113.10", apiKey = Key, models = new[] { "m" }, tenantId = Guid.NewGuid(), apiKeyEnc = "AAAA" });
        sneaky.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(sneaky)).Should().Be("request.malformed");

        var bad = await Send(HttpMethod.Post, "/api/v1/llm-providers", admin,
            new { name = "", baseUrl = "", apiKey = "short", models = new[] { "ok-model", "bad model" }, timeoutSeconds = 0, priority = -1 });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var errors = (await Json(bad)).GetProperty("errors");
        foreach (var field in new[] { "name", "baseUrl", "apiKey", "models[1]", "timeoutSeconds", "priority" })
            errors.TryGetProperty(field, out _).Should().BeTrue($"{field} should be reported");
        (await bad.Content.ReadAsStringAsync()).Should().NotContain("short");   // the rejected key is not echoed back
    }

    [Fact]
    public async Task A_provider_name_is_unique_within_the_tenant()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        await Connect(admin);

        (await Send(HttpMethod.Post, "/api/v1/llm-providers", admin, ProviderBody(url: "https://203.0.113.11"))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var other = await AdminToken(await SeedTenant());
        (await Send(HttpMethod.Post, "/api/v1/llm-providers", other, ProviderBody())).StatusCode.Should().Be(HttpStatusCode.Created);   // another tenant: fine
    }

    // ---------------------------------------------------------------- where a provider may live

    [Theory]
    [InlineData("https://10.0.0.5", "provider.url_forbidden")]
    [InlineData("https://127.0.0.1", "provider.url_forbidden")]
    [InlineData("https://169.254.169.254/latest/meta-data", "provider.url_forbidden")]
    [InlineData("https://192.168.1.20:8443", "provider.url_forbidden")]
    [InlineData("https://localhost", "provider.url_forbidden")]
    [InlineData("http://203.0.113.10", "provider.url_invalid")]
    public async Task Without_private_networks_an_internal_or_plain_http_address_is_refused(string url, string code)
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var strict = StrictClient();

        var response = await Send(HttpMethod.Post, "/api/v1/llm-providers", admin, ProviderBody(url: url), strict);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(response)).Should().Be(code);
        (await Json(await Send(HttpMethod.Get, "/api/v1/llm-providers", admin, client: strict))).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_public_https_address_is_accepted_when_private_networks_are_off()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);

        await Connect(admin, ProviderBody(url: "https://203.0.113.10"), StrictClient());
    }

    [Fact]
    public async Task Moving_a_provider_to_an_internal_address_is_refused_and_leaves_it_where_it_was()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var strict = StrictClient();
        var id = await Connect(admin, client: strict);

        var response = await Send(HttpMethod.Put, $"/api/v1/llm-providers/{id}", admin,
            new { name = "openai", baseUrl = "https://169.254.169.254", apiKey = "another-key-5678", models = new[] { "gpt-4o-mini" } }, strict);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(response)).Should().Be("provider.url_forbidden");
        var read = await Json(await Send(HttpMethod.Get, $"/api/v1/llm-providers/{id}", admin, client: strict));
        read.GetProperty("baseUrl").GetString().Should().Be("https://203.0.113.10");
        read.GetProperty("keyLast4").GetString().Should().Be("1234");
    }

    // ---------------------------------------------------------------- changing a provider

    [Fact]
    public async Task Updating_without_a_key_keeps_the_key_and_the_provider_still_authenticates_with_it()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var handler = new FakeProviderHandler();
        var client = ClientWithRealGateway(handler);
        var id = await Connect(admin, ProviderBody(url: "http://primary.test"), client);

        var updated = await Send(HttpMethod.Put, $"/api/v1/llm-providers/{id}", admin,
            new { name = "renamed", baseUrl = "http://primary.test", models = new[] { "gpt-4o-mini", "gpt-4o" }, timeoutSeconds = 20, enabled = true }, client);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(updated);
        body.GetProperty("name").GetString().Should().Be("renamed");
        body.GetProperty("keyLast4").GetString().Should().Be("1234");
        body.GetProperty("timeoutSeconds").GetInt32().Should().Be(20);

        (await Chat(ClientToken(tenantId, "gateway:invoke"), client)).StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requests.Should().ContainSingle().Which.Authorization.Should().Be($"Bearer {Key}");
    }

    [Fact]
    public async Task Changing_the_address_needs_the_key_again_so_a_stored_key_cannot_be_sent_somewhere_new()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var handler = new FakeProviderHandler();
        var client = ClientWithRealGateway(handler);
        var id = await Connect(admin, ProviderBody(url: "http://primary.test"), client);

        var without = await Send(HttpMethod.Put, $"/api/v1/llm-providers/{id}", admin,
            new { name = "openai", baseUrl = "http://attacker.test", models = new[] { "gpt-4o-mini" } }, client);
        without.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Code(await Json(without)).Should().Be("provider.key_required");

        // Nothing moved: traffic still goes to the original address with the original key.
        (await Chat(ClientToken(tenantId, "gateway:invoke"), client)).StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requests.Should().ContainSingle().Which.Should().Match<(string Host, string? Authorization, string Body)>(r => r.Host == "primary.test");

        var with = await Send(HttpMethod.Put, $"/api/v1/llm-providers/{id}", admin,
            new { name = "openai", baseUrl = "http://secondary.test", apiKey = "brand-new-key-9999", models = new[] { "gpt-4o-mini" } }, client);
        with.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(with)).GetProperty("keyLast4").GetString().Should().Be("9999");

        await Chat(ClientToken(tenantId, "gateway:invoke"), client);
        handler.Requests.Last().Should().Match<(string Host, string? Authorization, string Body)>(r =>
            r.Host == "secondary.test" && r.Authorization == "Bearer brand-new-key-9999");
    }

    [Fact]
    public async Task Update_checks_its_fields_and_the_name()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var a = await Connect(admin, ProviderBody("a"));
        await Connect(admin, ProviderBody("b", "https://203.0.113.11"));

        var invalid = await Send(HttpMethod.Put, $"/api/v1/llm-providers/{a}", admin, new { name = "a", baseUrl = "https://203.0.113.10", apiKey = "x", models = Array.Empty<string>() });
        invalid.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var errors = (await Json(invalid)).GetProperty("errors");
        errors.TryGetProperty("apiKey", out _).Should().BeTrue();
        errors.TryGetProperty("models", out _).Should().BeTrue();

        (await Send(HttpMethod.Put, $"/api/v1/llm-providers/{a}", admin, new { name = "b", baseUrl = "https://203.0.113.10", models = new[] { "m" } }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Send(HttpMethod.Put, $"/api/v1/llm-providers/{Guid.NewGuid()}", admin, new { name = "z", baseUrl = "https://203.0.113.10", models = new[] { "m" } }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_failover_order_can_be_set_and_must_list_every_provider_once()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var a = await Connect(admin, ProviderBody("a", "https://203.0.113.10"));
        var b = await Connect(admin, ProviderBody("b", "https://203.0.113.11"));
        var c = await Connect(admin, ProviderBody("c", "https://203.0.113.12"));

        (await Send(HttpMethod.Put, "/api/v1/llm-providers/order", admin, new { ids = new[] { c, a, b } })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var names = (await Json(await Send(HttpMethod.Get, "/api/v1/llm-providers", admin))).GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()).ToList();
        names.Should().Equal("c", "a", "b");

        (await Send(HttpMethod.Put, "/api/v1/llm-providers/order", admin, new { ids = new[] { a, b } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, "/api/v1/llm-providers/order", admin, new { ids = new[] { a, a, b } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, "/api/v1/llm-providers/order", admin, new { ids = Array.Empty<Guid>() })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Deleting_a_provider_removes_it()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var id = await Connect(admin);

        (await Send(HttpMethod.Delete, $"/api/v1/llm-providers/{id}", admin)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Send(HttpMethod.Get, $"/api/v1/llm-providers/{id}", admin)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Delete, $"/api/v1/llm-providers/{id}", admin)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_change_a_provider()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        var adminA = await AdminToken(a);
        var adminB = await AdminToken(b);
        var id = await Connect(adminA);

        (await Send(HttpMethod.Get, $"/api/v1/llm-providers/{id}", adminB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Put, $"/api/v1/llm-providers/{id}", adminB, new { name = "x", baseUrl = "https://203.0.113.10", apiKey = Key, models = new[] { "m" } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"/api/v1/llm-providers/{id}/test", adminB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Delete, $"/api/v1/llm-providers/{id}", adminB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(await Send(HttpMethod.Get, "/api/v1/llm-providers", adminB))).GetProperty("items").GetArrayLength().Should().Be(0);
        (await Send(HttpMethod.Put, "/api/v1/llm-providers/order", adminB, new { ids = new[] { id } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Tenant A's provider is untouched.
        (await Send(HttpMethod.Get, $"/api/v1/llm-providers/{id}", adminA)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------------------------------------------------------------- the test button

    [Fact]
    public async Task Testing_a_provider_makes_a_harmless_call_with_the_stored_key_and_reports_ok()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var handler = new FakeProviderHandler();
        var client = ClientWithRealGateway(handler);
        var id = await Connect(admin, ProviderBody(url: "http://primary.test"), client);

        var response = await Send(HttpMethod.Post, $"/api/v1/llm-providers/{id}/test", admin, client: client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("latencyMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        body.GetProperty("errorCategory").ValueKind.Should().Be(JsonValueKind.Null);
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Authorization.Should().Be($"Bearer {Key}");
        request.Body.Should().Contain("\"ping\"").And.Contain("\"max_tokens\":1");
    }

    [Fact]
    public async Task A_failing_provider_is_reported_by_category_and_what_it_said_is_never_passed_on()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var handler = new FakeProviderHandler
        {
            Reply = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"the key sk-test-key-1234 is not valid for org-secret"}""") }
        };
        var client = ClientWithRealGateway(handler);
        var id = await Connect(admin, ProviderBody(url: "http://primary.test"), client);

        var response = await Send(HttpMethod.Post, $"/api/v1/llm-providers/{id}/test", admin, client: client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("ok").GetBoolean().Should().BeFalse();
        body.GetProperty("errorCategory").GetString().Should().Be("http_error");
        text.Should().NotContain("org-secret").And.NotContain("sk-test");
    }

    // ---------------------------------------------------------------- what the gateway does with the tenant's providers

    [Fact]
    public async Task A_chat_request_goes_to_the_tenants_own_provider_with_its_key_and_only_sanitized_text()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var handler = new FakeProviderHandler();
        var client = ClientWithRealGateway(handler);
        await Connect(admin, ProviderBody(url: "http://primary.test"), client);

        var response = await Chat(ClientToken(tenantId, "gateway:invoke", "gateway:reidentify"), client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(response)).GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().Be("Mock reply");
        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Host.Should().Be("primary.test");
        sent.Authorization.Should().Be($"Bearer {Key}");
        foreach (var secret in Secrets) sent.Body.Should().NotContain(secret);   // the provider never sees the patient's data
        sent.Body.Should().Contain("[[PERSON_");
    }

    [Fact]
    public async Task A_tenant_without_a_provider_for_the_model_is_told_so_before_anything_is_sent()
    {
        var withProvider = await SeedTenant();
        var without = await SeedTenant();
        var handler = new FakeProviderHandler();
        var client = ClientWithRealGateway(handler);
        await Connect(await AdminToken(withProvider), ProviderBody(url: "http://primary.test"), client);

        // Tenant B has no provider of its own; tenant A's is not available to it.
        var response = await Chat(ClientToken(without, "gateway:invoke"), client);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await Json(response);
        Code(body).Should().Be("validation.failed");
        body.GetProperty("errors").GetProperty("model")[0].GetString().Should().Be("No provider serves this model");
        handler.Requests.Should().BeEmpty();

        // And a model that the tenant's provider does not list gets the same answer.
        (await Chat(ClientToken(withProvider, "gateway:invoke"), client, model: "some-other-model")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task When_the_first_provider_is_down_the_next_in_the_tenants_order_answers_and_reordering_changes_who_goes_first()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var handler = new FakeProviderHandler { Reply = r => r.RequestUri!.Host == "primary.test" ? new HttpResponseMessage(HttpStatusCode.BadRequest) : FakeProviderHandler.Ok() };
        var client = ClientWithRealGateway(handler);
        var primary = await Connect(admin, ProviderBody("primary", "http://primary.test", "primary-key-1111"), client);
        var backup = await Connect(admin, ProviderBody("backup", "http://backup.test", "backup-key-2222"), client);
        var gateway = ClientToken(tenantId, "gateway:invoke");

        (await Chat(gateway, client)).StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requests.Select(r => r.Host).Should().Equal("primary.test", "backup.test");   // primary failed, backup answered
        handler.Requests.Last().Authorization.Should().Be("Bearer backup-key-2222");           // each provider gets its own key

        handler.Requests.Clear();
        (await Send(HttpMethod.Put, "/api/v1/llm-providers/order", admin, new { ids = new[] { backup, primary } }, client)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Chat(gateway, client)).StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requests.Select(r => r.Host).Should().Equal("backup.test");   // now the backup goes first and the primary is never needed
    }

    [Fact]
    public async Task A_disabled_provider_is_not_used_and_when_none_is_left_the_model_is_unavailable()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var handler = new FakeProviderHandler();
        var client = ClientWithRealGateway(handler);
        var id = await Connect(admin, ProviderBody(url: "http://primary.test"), client);
        var gateway = ClientToken(tenantId, "gateway:invoke");

        var off = await Send(HttpMethod.Put, $"/api/v1/llm-providers/{id}", admin,
            new { name = "openai", baseUrl = "http://primary.test", models = new[] { "gpt-4o-mini" }, enabled = false }, client);
        off.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(off)).GetProperty("enabled").GetBoolean().Should().BeFalse();

        (await Chat(gateway, client)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        handler.Requests.Should().BeEmpty();

        await Send(HttpMethod.Put, $"/api/v1/llm-providers/{id}", admin,
            new { name = "openai", baseUrl = "http://primary.test", models = new[] { "gpt-4o-mini" }, enabled = true }, client);
        (await Chat(gateway, client)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_deleted_provider_stops_serving_requests_at_once()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var handler = new FakeProviderHandler();
        var client = ClientWithRealGateway(handler);
        var id = await Connect(admin, ProviderBody(url: "http://primary.test"), client);
        var gateway = ClientToken(tenantId, "gateway:invoke");
        (await Chat(gateway, client)).StatusCode.Should().Be(HttpStatusCode.OK);

        await Send(HttpMethod.Delete, $"/api/v1/llm-providers/{id}", admin, client: client);

        (await Chat(gateway, client)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_model_failure_still_becomes_a_502_with_the_tenants_providers()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var handler = new FakeProviderHandler { Reply = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) };
        var client = ClientWithRealGateway(handler);
        await Connect(admin, ProviderBody(url: "http://primary.test"), client);

        var response = await Chat(ClientToken(tenantId, "gateway:invoke"), client);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        Code(await Json(response)).Should().Be("provider.unavailable");
        (await response.Content.ReadAsStringAsync()).Should().NotContain(Key);
    }

    // ---------------------------------------------------------------- audit

    [Fact]
    public async Task Every_change_is_audited_and_no_audit_entry_contains_the_key()
    {
        var tenantId = await SeedTenant();
        var admin = await AdminToken(tenantId);
        var handler = new FakeProviderHandler();
        var client = ClientWithRealGateway(handler);
        var a = await Connect(admin, ProviderBody("a", "http://primary.test"), client);
        var b = await Connect(admin, ProviderBody("b", "http://backup.test"), client);
        await Send(HttpMethod.Put, $"/api/v1/llm-providers/{a}", admin, new { name = "a", baseUrl = "http://primary.test", models = new[] { "gpt-4o" } }, client);
        await Send(HttpMethod.Put, "/api/v1/llm-providers/order", admin, new { ids = new[] { b, a } }, client);
        await Send(HttpMethod.Post, $"/api/v1/llm-providers/{a}/test", admin, client: client);
        await Send(HttpMethod.Delete, $"/api/v1/llm-providers/{b}", admin, client: client);

        (await Audit(tenantId)).Should().Equal(
            ("llmprovider.create", "success"), ("llmprovider.create", "success"), ("llmprovider.update", "success"),
            ("llmprovider.reorder", "success"), ("llmprovider.test", "success"), ("llmprovider.delete", "success"));

        await using var db = _pg.AdminContext();
        var details = await db.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenantId).Select(e => e.Details).ToListAsync();
        details.SelectMany(d => d.Values).Should().NotContain(v => v.Contains("sk-test") || v.Contains("1234"));
    }
}
