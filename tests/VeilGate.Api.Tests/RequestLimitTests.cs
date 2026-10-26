using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using VeilGate.Api.Controllers;
using VeilGate.Api.Middleware;
using Xunit;

namespace VeilGate.Api.Tests;

/// <summary>
/// The in-memory test server does not enforce Kestrel's body-size limit, so these check the two parts we own:
/// that each endpoint declares the limit from Backend doc 2.1, and that an over-size body becomes our 413 problem.
/// </summary>
public class RequestLimitTests
{
    private static long? LimitOf(Type controller, string action) =>
        ((IRequestSizeLimitMetadata)controller.GetMethod(action)!.GetCustomAttribute<RequestSizeLimitAttribute>()!).MaxRequestBodySize;

    [Fact]
    public void Chat_is_limited_to_256_KB_and_the_text_endpoints_to_1_MB()
    {
        LimitOf(typeof(GatewayController), nameof(GatewayController.ChatCompletions)).Should().Be(262_144);
        LimitOf(typeof(GatewayController), nameof(GatewayController.Detect)).Should().Be(1_048_576);
        LimitOf(typeof(GatewayController), nameof(GatewayController.Deidentify)).Should().Be(1_048_576);
        LimitOf(typeof(GatewayController), nameof(GatewayController.Reidentify)).Should().Be(1_048_576);
        LimitOf(typeof(PlaygroundController), nameof(PlaygroundController.Run)).Should().Be(262_144);
    }

    [Fact]
    public async Task A_body_that_is_too_large_becomes_a_413_with_a_stable_code()
    {
        var middleware = new ExceptionHandlingMiddleware(_ => throw new BadHttpRequestException("too big", StatusCodes.Status413PayloadTooLarge),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(413);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("code").GetString().Should().Be("payload.too_large");
    }
}
