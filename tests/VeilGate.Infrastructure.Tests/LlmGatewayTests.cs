using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VeilGate.Application.Llm;
using VeilGate.Infrastructure.Llm;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>No network and no Docker: each provider is a scripted fake that answers (or stalls) on demand.</summary>
public class LlmGatewayTests
{
    private const string OkBody = """{"id":"c1","model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""";

    private sealed class ScriptedHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> script) : HttpMessageHandler
    {
        public int Calls;
        public string? LastBody;
        public string? LastAuthorization;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await script(n, request, ct);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body = "{}") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<HttpResponseMessage> Ok() => Task.FromResult(Json(HttpStatusCode.OK, OkBody));

    private static LlmProviderOptions Provider(string name, int priority = 100, int timeout = 30, int attemptTimeout = 30, params string[] models) =>
        new() { Name = name, BaseUrl = $"http://{name}.test", ApiKey = "secret-key", Priority = priority, TimeoutSeconds = timeout, AttemptTimeoutSeconds = attemptTimeout, Models = [.. models] };

    private static LlmRequest Request(string model = "gpt-4o-mini") => new(model, [new LlmMessage("user", "hi")], 0.2, 100);

    private static ILlmGateway Build(LlmOptions options, params (string Provider, ScriptedHandler Handler)[] handlers)
    {
        options.AllowPrivateNetworks = true;   // the fake hosts are not real https addresses
        options.RetryBaseDelayMs = 1;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLlmGateway(options);
        foreach (var (provider, handler) in handlers)
            services.AddHttpClient(LlmGateway.ClientName(provider)).ConfigurePrimaryHttpMessageHandler(() => handler);   // replaces the real network handler
        return services.BuildServiceProvider().GetRequiredService<ILlmGateway>();
    }

    [Fact]
    public async Task A_successful_call_returns_the_reply_and_sends_the_expected_request()
    {
        var handler = new ScriptedHandler((_, _, _) => Ok());
        var gateway = Build(new LlmOptions { Providers = [Provider("a")] }, ("a", handler));

        var result = await gateway.CompleteAsync(Request());

        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("Hello");
        result.Value.PromptTokens.Should().Be(3);
        result.Value.Provider.Should().Be("a");
        handler.LastAuthorization.Should().Be("Bearer secret-key");
        handler.LastBody.Should().Contain("\"model\":\"gpt-4o-mini\"").And.Contain("\"stream\":false").And.Contain("\"max_tokens\":100");
    }

    [Fact]
    public async Task A_temporary_server_error_is_retried()
    {
        var handler = new ScriptedHandler((n, _, _) => n == 1 ? Task.FromResult(Json(HttpStatusCode.ServiceUnavailable)) : Ok());
        var gateway = Build(new LlmOptions { Providers = [Provider("a")] }, ("a", handler));

        var result = await gateway.CompleteAsync(Request());

        result.IsSuccess.Should().BeTrue();
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_provider_that_keeps_failing_gets_three_attempts_then_the_call_fails()
    {
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError)));
        var gateway = Build(new LlmOptions { Providers = [Provider("a")] }, ("a", handler));

        var result = await gateway.CompleteAsync(Request());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("provider.unavailable");
        handler.Calls.Should().Be(3);   // the first try plus two retries
    }

    [Fact]
    public async Task A_bad_request_is_not_retried()
    {
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.BadRequest)));
        var gateway = Build(new LlmOptions { Providers = [Provider("a")] }, ("a", handler));

        var result = await gateway.CompleteAsync(Request());

        result.Error.Code.Should().Be("provider.unavailable");
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task When_the_first_provider_is_down_the_next_one_answers()
    {
        var down = new ScriptedHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError)));
        var up = new ScriptedHandler((_, _, _) => Ok());
        var gateway = Build(new LlmOptions { Providers = [Provider("a", priority: 1), Provider("b", priority: 2)] }, ("a", down), ("b", up));

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
        var gateway = Build(new LlmOptions { Providers = [Provider("late", priority: 9), Provider("early", priority: 1)] }, ("late", second), ("early", first));

        var result = await gateway.CompleteAsync(Request());

        result.Value.Provider.Should().Be("early");
        second.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_provider_that_does_not_serve_the_model_is_skipped()
    {
        var handler = new ScriptedHandler((_, _, _) => Ok());
        var gateway = Build(new LlmOptions { Providers = [Provider("a", models: "other-model")] }, ("a", handler));

        var result = await gateway.CompleteAsync(Request("gpt-4o-mini"));

        result.Error.Code.Should().Be("provider.unavailable");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_garbled_answer_counts_as_a_failure()
    {
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"unexpected":true}""")));
        var gateway = Build(new LlmOptions { Providers = [Provider("a")] }, ("a", handler));

        var result = await gateway.CompleteAsync(Request());

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
        var gateway = Build(new LlmOptions { Providers = [Provider("a", timeout: 1, attemptTimeout: 1)] }, ("a", handler));

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
        var gateway = Build(new LlmOptions { Providers = [Provider("a")] }, ("a", handler));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => gateway.CompleteAsync(Request(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task After_enough_failures_the_circuit_opens_and_calls_stop_reaching_the_provider()
    {
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable)));
        var gateway = Build(new LlmOptions { BreakerMinimumThroughput = 4, Providers = [Provider("a")] }, ("a", handler));

        for (var i = 0; i < 4; i++)
            (await gateway.CompleteAsync(Request())).Error.Code.Should().Be("provider.unavailable");

        var reached = handler.Calls;
        reached.Should().Be(4);   // the breaker opened after 4 failed attempts; the other attempts were refused without a network call
        (await gateway.CompleteAsync(Request())).Error.Code.Should().Be("provider.unavailable");
        handler.Calls.Should().Be(reached);
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

    [Fact]
    public void A_plain_http_provider_is_refused_unless_private_networks_are_allowed()
    {
        var options = new LlmOptions { Providers = [new LlmProviderOptions { Name = "x", BaseUrl = "http://api.example.com" }] };

        var act = () => new ServiceCollection().AddLlmGateway(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*https*");
    }
}
