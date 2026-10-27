using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeilGate.Application.Llm;
using VeilGate.Infrastructure.Llm;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>No network and no Docker: each provider is a scripted fake that answers (or stalls) on demand.</summary>
public class LlmGatewayTests
{
    private const string OkBody = """{"id":"c1","model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""";
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private sealed class ScriptedHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> script) : HttpMessageHandler
    {
        public int Calls;
        public string? LastBody;
        public string? LastAuthorization;
        public Uri? LastUri;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await script(n, request, ct);
        }
    }

    /// <summary>The tenant's providers, fixed for the test.</summary>
    private sealed class StaticCatalog(params LlmProviderConfig[] providers) : ILlmProviderCatalog
    {
        public Task<IReadOnlyList<LlmProviderConfig>> GetForModelAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LlmProviderConfig>>(providers.Where(p => p.Serves(model)).OrderBy(p => p.Priority).ToList());
        public Task<bool> ServesModelAsync(string model, CancellationToken ct = default) => Task.FromResult(providers.Any(p => p.Serves(model)));
        public Task<LlmProviderConfig?> FindAsync(Guid providerId, CancellationToken ct = default) =>
            Task.FromResult(providers.FirstOrDefault(p => p.Id == providerId));
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body = "{}") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<HttpResponseMessage> Ok() => Task.FromResult(Json(HttpStatusCode.OK, OkBody));

    private static LlmProviderConfig Provider(string name, int priority = 100, int timeout = 30, int? attemptTimeout = null, params string[] models) =>
        new(Guid.CreateVersion7(), Tenant, name, $"http://{name}.test", "secret-key", timeout, priority,
            models.Length == 0 ? ["gpt-4o-mini"] : models, attemptTimeout ?? timeout);

    private static LlmRequest Request(string model = "gpt-4o-mini") => new(model, [new LlmMessage("user", "hi")], 0.2, 100);

    private static (ILlmGateway Gateway, ProviderCaller Caller) Build(
        LlmProviderConfig[] providers, Dictionary<string, ScriptedHandler> handlers, int breakerMinimumThroughput = 20)
    {
        var options = Options.Create(new LlmOptions { AllowPrivateNetworks = true, RetryBaseDelayMs = 1, BreakerMinimumThroughput = breakerMinimumThroughput });
        var cache = new ProviderClientCache(options, p => handlers[p.Name]);   // replaces the real network handler
        var caller = new ProviderCaller(cache, NullLogger<ProviderCaller>.Instance);
        return (new LlmGateway(new StaticCatalog(providers), caller), caller);
    }

    private static ILlmGateway Gateway(LlmProviderConfig provider, ScriptedHandler handler, int breakerMinimumThroughput = 20) =>
        Build([provider], new() { [provider.Name] = handler }, breakerMinimumThroughput).Gateway;

    // ------------------------------------------------------------ one provider

    [Fact]
    public async Task A_successful_call_returns_the_reply_and_sends_the_expected_request()
    {
        var handler = new ScriptedHandler((_, _, _) => Ok());
        var gateway = Gateway(Provider("a"), handler);

        var result = await gateway.CompleteAsync(Request());

        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("Hello");
        result.Value.PromptTokens.Should().Be(3);
        result.Value.Provider.Should().Be("a");
        handler.LastAuthorization.Should().Be("Bearer secret-key");
        handler.LastUri!.ToString().Should().Be("http://a.test/v1/chat/completions");
        handler.LastBody.Should().Contain("\"model\":\"gpt-4o-mini\"").And.Contain("\"stream\":false").And.Contain("\"max_tokens\":100");
    }

    [Fact]
    public async Task A_provider_address_with_a_path_keeps_its_path()
    {
        var handler = new ScriptedHandler((_, _, _) => Ok());
        var provider = Provider("a") with { BaseUrl = "https://gateway.test/openai" };

        await Gateway(provider, handler).CompleteAsync(Request());

        handler.LastUri!.ToString().Should().Be("https://gateway.test/openai/v1/chat/completions");
    }

    [Fact]
    public async Task A_temporary_server_error_is_retried()
    {
        var handler = new ScriptedHandler((n, _, _) => n == 1 ? Task.FromResult(Json(HttpStatusCode.ServiceUnavailable)) : Ok());

        var result = await Gateway(Provider("a"), handler).CompleteAsync(Request());

        result.IsSuccess.Should().BeTrue();
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_provider_that_keeps_failing_gets_three_attempts_then_the_call_fails()
    {
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError)));

        var result = await Gateway(Provider("a"), handler).CompleteAsync(Request());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("provider.unavailable");
        handler.Calls.Should().Be(3);   // the first try plus two retries
    }

    [Fact]
    public async Task A_bad_request_is_not_retried()
    {
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.BadRequest)));

        var result = await Gateway(Provider("a"), handler).CompleteAsync(Request());

        result.Error.Code.Should().Be("provider.unavailable");
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_garbled_answer_counts_as_a_failure()
    {
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"unexpected":true}""")));

        var result = await Gateway(Provider("a"), handler).CompleteAsync(Request());

        result.Error.Code.Should().Be("provider.unavailable");
    }

    [Fact]
    public async Task A_provider_that_never_answers_ends_in_a_timeout()
    {
        var handler = new ScriptedHandler(async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json(HttpStatusCode.OK, OkBody);
        });
        var gateway = Gateway(Provider("a", timeout: 1, attemptTimeout: 1), handler);

        var clock = Stopwatch.StartNew();
        var result = await gateway.CompleteAsync(Request());
        clock.Stop();

        result.Error.Code.Should().Be("provider.timeout");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancelling_the_call_is_not_reported_as_a_timeout()
    {
        var handler = new ScriptedHandler(async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json(HttpStatusCode.OK, OkBody);
        });
        var gateway = Gateway(Provider("a"), handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => gateway.CompleteAsync(Request(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task After_enough_failures_the_circuit_opens_and_calls_stop_reaching_the_provider()
    {
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable)));
        var gateway = Gateway(Provider("a"), handler, breakerMinimumThroughput: 4);

        for (var i = 0; i < 4; i++)
            (await gateway.CompleteAsync(Request())).Error.Code.Should().Be("provider.unavailable");

        var reached = handler.Calls;
        reached.Should().Be(4);   // the breaker opened after 4 failed attempts; the other attempts were refused without a network call
        (await gateway.CompleteAsync(Request())).Error.Code.Should().Be("provider.unavailable");
        handler.Calls.Should().Be(reached);
    }

    // ------------------------------------------------------------ several providers

    [Fact]
    public async Task When_the_first_provider_is_down_the_next_one_answers()
    {
        var down = new ScriptedHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError)));
        var up = new ScriptedHandler((_, _, _) => Ok());
        var (gateway, _) = Build([Provider("a", priority: 1), Provider("b", priority: 2)], new() { ["a"] = down, ["b"] = up });

        var result = await gateway.CompleteAsync(Request());

        result.IsSuccess.Should().BeTrue();
        result.Value.Provider.Should().Be("b");
        up.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Providers_are_tried_in_priority_order_regardless_of_list_order()
    {
        var first = new ScriptedHandler((_, _, _) => Ok());
        var second = new ScriptedHandler((_, _, _) => Ok());
        var (gateway, _) = Build([Provider("late", priority: 9), Provider("early", priority: 1)], new() { ["late"] = second, ["early"] = first });

        var result = await gateway.CompleteAsync(Request());

        result.Value.Provider.Should().Be("early");
        second.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_provider_that_does_not_serve_the_model_is_skipped()
    {
        var handler = new ScriptedHandler((_, _, _) => Ok());
        var gateway = Gateway(Provider("a", models: "other-model"), handler);

        var result = await gateway.CompleteAsync(Request("gpt-4o-mini"));

        result.Error.Code.Should().Be("provider.unavailable");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_tenant_with_no_providers_gets_provider_unavailable()
    {
        var (gateway, _) = Build([], new());

        (await gateway.CompleteAsync(Request())).Error.Code.Should().Be("provider.unavailable");
    }

    [Fact]
    public async Task Each_provider_has_its_own_circuit_so_one_sick_provider_does_not_shut_off_another()
    {
        var sick = new ScriptedHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable)));
        var healthy = new ScriptedHandler((_, _, _) => Ok());
        var (gateway, _) = Build([Provider("sick", priority: 1), Provider("healthy", priority: 2)],
            new() { ["sick"] = sick, ["healthy"] = healthy }, breakerMinimumThroughput: 3);

        for (var i = 0; i < 4; i++)
            (await gateway.CompleteAsync(Request())).Value.Provider.Should().Be("healthy");

        sick.Calls.Should().BeLessThan(12);   // its breaker opened, so later calls skipped it without a network call
        healthy.Calls.Should().Be(4);
    }

    // ------------------------------------------------------------ the client cache

    [Fact]
    public void The_same_provider_keeps_one_client_so_its_circuit_breaker_state_survives_between_requests()
    {
        var cache = new ProviderClientCache(Options.Create(new LlmOptions { AllowPrivateNetworks = true }), _ => new ScriptedHandler((_, _, _) => Ok()));
        var provider = Provider("a");

        cache.Get(provider).Should().BeSameAs(cache.Get(provider));
    }

    [Fact]
    public void Changing_only_the_api_key_keeps_the_client_but_changing_the_address_or_timeout_makes_a_new_one()
    {
        var cache = new ProviderClientCache(Options.Create(new LlmOptions { AllowPrivateNetworks = true }), _ => new ScriptedHandler((_, _, _) => Ok()));
        var provider = Provider("a");
        var original = cache.Get(provider);

        cache.Get(provider with { ApiKey = "a-different-key" }).Should().BeSameAs(original);
        cache.Get(provider with { BaseUrl = "https://elsewhere.test" }).Should().NotBeSameAs(original);
    }

    [Fact]
    public void Two_providers_never_share_a_client()
    {
        var cache = new ProviderClientCache(Options.Create(new LlmOptions { AllowPrivateNetworks = true }), _ => new ScriptedHandler((_, _, _) => Ok()));

        cache.Get(Provider("a")).Should().NotBeSameAs(cache.Get(Provider("b")));
    }

    [Fact]
    public void A_provider_never_prints_its_api_key()
    {
        var text = Provider("a").ToString();

        text.Should().NotContain("secret-key").And.Contain("a");
    }

    // ------------------------------------------------------------ the admin test button

    [Fact]
    public async Task Testing_a_working_provider_reports_ok_and_sends_only_a_ping_for_its_first_model()
    {
        var handler = new ScriptedHandler((_, _, _) => Ok());
        var provider = Provider("a", models: ["gpt-4o-mini", "gpt-4o"]);
        var (_, caller) = Build([provider], new() { ["a"] = handler });

        var result = await new LlmProviderTester(caller).TestAsync(provider);

        result.Ok.Should().BeTrue();
        result.ErrorCategory.Should().BeNull();
        handler.LastBody.Should().Contain("\"ping\"").And.Contain("\"model\":\"gpt-4o-mini\"").And.Contain("\"max_tokens\":1");
    }

    [Theory]
    [InlineData(500, "http_error")]
    [InlineData(401, "http_error")]
    public async Task Testing_a_failing_provider_reports_a_category_never_the_providers_own_text(int status, string category)
    {
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(Json((HttpStatusCode)status, """{"error":"your key sk-LEAKED is wrong"}""")));
        var provider = Provider("a");
        var (_, caller) = Build([provider], new() { ["a"] = handler });

        var result = await new LlmProviderTester(caller).TestAsync(provider);

        result.Ok.Should().BeFalse();
        result.ErrorCategory.Should().Be(category);
        result.ToString().Should().NotContain("LEAKED");
    }

    [Fact]
    public async Task Testing_an_unreachable_or_garbled_or_slow_provider_gives_distinct_categories()
    {
        var unreachable = new ScriptedHandler((_, _, _) => throw new HttpRequestException("no route"));
        var garbled = new ScriptedHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"nope":1}""")));
        var slow = new ScriptedHandler(async (_, _, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Json(HttpStatusCode.OK, OkBody); });
        var a = Provider("unreachable");
        var b = Provider("garbled");
        var c = Provider("slow", timeout: 1, attemptTimeout: 1);
        var (_, caller) = Build([a, b, c], new() { ["unreachable"] = unreachable, ["garbled"] = garbled, ["slow"] = slow });
        var tester = new LlmProviderTester(caller);

        (await tester.TestAsync(a)).ErrorCategory.Should().Be("unreachable");
        (await tester.TestAsync(b)).ErrorCategory.Should().Be("invalid_response");
        (await tester.TestAsync(c)).ErrorCategory.Should().Be("timeout");
    }
}

public class SsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]   // the cloud metadata address
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]   // a private IPv4 address wearing an IPv6 costume
    public void Private_and_special_addresses_are_forbidden(string address) =>
        SsrfGuard.IsForbidden(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]   // just outside 172.16.0.0/12
    [InlineData("203.0.113.9")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string address) =>
        SsrfGuard.IsForbidden(IPAddress.Parse(address)).Should().BeFalse();
}

/// <summary>The save-time check. It uses address literals, so it needs no DNS and no network.</summary>
public class OutboundUrlValidatorTests
{
    private static OutboundUrlValidator Production() => new(Options.Create(new LlmOptions()));
    private static OutboundUrlValidator Development() => new(Options.Create(new LlmOptions { AllowPrivateNetworks = true }));

    [Theory]
    [InlineData("https://203.0.113.10")]
    [InlineData("https://8.8.8.8/v1")]
    [InlineData("https://[2606:4700:4700::1111]")]
    public async Task A_public_https_address_is_accepted(string url)
        => (await Production().ValidateAsync(url)).IsSuccess.Should().BeTrue();

    [Theory]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://192.168.1.20:8443")]
    [InlineData("https://169.254.169.254/latest/meta-data")]   // the cloud metadata address
    [InlineData("https://100.64.1.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://[fd00::1]")]
    [InlineData("https://[::ffff:10.0.0.1]")]
    public async Task An_internal_address_is_refused(string url)
    {
        var result = await Production().ValidateAsync(url);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("provider.url_forbidden");
    }

    [Fact]
    public async Task A_name_that_resolves_to_loopback_is_refused()
        => (await Production().ValidateAsync("https://localhost")).Error.Code.Should().Be("provider.url_forbidden");

    [Theory]
    [InlineData("http://203.0.113.10")]
    [InlineData("ftp://203.0.113.10")]
    [InlineData("https://user:pass@203.0.113.10")]
    [InlineData("not a url")]
    public async Task Plain_http_credentials_and_nonsense_are_refused(string url)
    {
        var result = await Production().ValidateAsync(url);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("provider.url_invalid");
    }

    [Fact]
    public async Task In_development_http_and_local_addresses_are_allowed_for_a_mock_model()
    {
        (await Development().ValidateAsync("http://localhost:8089")).IsSuccess.Should().BeTrue();
        (await Development().ValidateAsync("http://127.0.0.1:8089")).IsSuccess.Should().BeTrue();
        (await Development().ValidateAsync("https://user:pass@127.0.0.1")).IsFailure.Should().BeTrue();   // credentials are never fine
        (await Development().ValidateAsync("ftp://127.0.0.1")).IsFailure.Should().BeTrue();
    }
}
