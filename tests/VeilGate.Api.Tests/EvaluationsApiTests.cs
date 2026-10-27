using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Evaluation;
using VeilGate.Application.Llm;
using VeilGate.Application.Policies;
using VeilGate.Domain;
using VeilGate.Domain.Policies;
using VeilGate.Infrastructure.Evaluation;
using VeilGate.Infrastructure.Llm;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

/// <summary>Datasets and evaluations from the outside: real HTTP, real database, and the worker's processor run in-process.</summary>
[Collection("postgres")]
public sealed class EvaluationsApiTests : IDisposable
{
    private readonly PostgresFixture _pg;
    private readonly GatewayApiFactory _factory;
    private readonly HttpClient _client;

    public EvaluationsApiTests(PostgresFixture pg)
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

    private sealed record Setup(Guid TenantId, string Token, Guid PolicyId);

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

    private string UserToken(User u) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(
            new AccessTokenClaims(u.Id, u.TenantId, u.Role.ToString(), Guid.NewGuid(), ["pwd", "otp"], DateTimeOffset.UtcNow, u.TokenVersion));

    private string ClientToken(Guid tenantId) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueClientToken(new ClientTokenClaims(Guid.NewGuid(), tenantId, ["gateway:detect"]));

    /// <summary>A new tenant with one person of the given role and one policy (a draft of the HIPAA template).</summary>
    private async Task<Setup> NewTenant(UserRole role = UserRole.SecurityOfficer)
    {
        var tenant = Tenant.Create($"Eval {Guid.NewGuid():N}"[..16]).Value;
        await using (var admin = _pg.AdminContext())
        {
            admin.Add(tenant);
            await admin.SaveChangesAsync();
        }

        var user = await SeedUser(tenant.Id, role);
        var policy = Policy.Create(tenant.Id, $"p-{Guid.NewGuid():N}"[..12], null).Value;
        var draft = policy.NewDraft(PolicyTemplates.YamlOf(PolicyTemplates.HipaaSafeHarbor), user.Id).Value;
        await using (var admin = _pg.AdminContext())
        {
            admin.Add(policy);
            admin.Add(draft);
            await admin.SaveChangesAsync();
        }
        return new Setup(tenant.Id, UserToken(user), policy.Id);
    }

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

    private async Task<Guid> CreateDataset(string token, string? name = null, int count = 40, int seed = 99)
    {
        var response = await Send(HttpMethod.Post, "/api/v1/datasets/synthetic", token, new { name = name ?? $"ds-{Guid.NewGuid():N}"[..12], count, seed });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> StartRun(Setup s, Guid datasetId, string mode = "rules+ner")
    {
        var response = await Send(HttpMethod.Post, "/api/v1/evaluations", s.Token, new { datasetId, policyId = s.PolicyId, mode });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Does what the worker does: works through the queue until it is empty.</summary>
    private async Task RunQueue()
    {
        var processor = new EvaluationQueueProcessor(_factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EvaluationQueueProcessor>.Instance);
        while (await processor.ProcessNextAsync(TimeSpan.FromMinutes(15), 3, default)) { }
    }

    private async Task<JsonElement> GetRun(string token, Guid id) => await Json(await Send(HttpMethod.Get, $"/api/v1/evaluations/{id}", token));

    // ---------------------------------------------------------------- who may

    [Fact]
    public async Task Only_the_people_who_write_policies_may_use_datasets_and_evaluations()
    {
        var s = await NewTenant();
        var auditor = UserToken(await SeedUser(s.TenantId, UserRole.Auditor));

        (await Send(HttpMethod.Get, "/api/v1/datasets", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Get, "/api/v1/datasets", auditor)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Get, "/api/v1/evaluations", auditor)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Get, "/api/v1/evaluations", ClientToken(s.TenantId))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Get, "/api/v1/datasets", s.Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------------------------------------------------------------- datasets

    [Fact]
    public async Task A_dataset_can_be_created_listed_and_read_and_a_name_is_used_once()
    {
        var s = await NewTenant();
        var id = await CreateDataset(s.Token, name: "standard", count: 25);

        var one = await Json(await Send(HttpMethod.Get, $"/api/v1/datasets/{id}", s.Token));
        one.GetProperty("name").GetString().Should().Be("standard");
        one.GetProperty("caseCount").GetInt32().Should().Be(25);

        var list = await Json(await Send(HttpMethod.Get, "/api/v1/datasets", s.Token));
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Contain(id);

        var again = await Send(HttpMethod.Post, "/api/v1/datasets/synthetic", s.Token, new { name = "standard", count = 5 });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var empty = await Send(HttpMethod.Post, "/api/v1/datasets/synthetic", s.Token, new { name = "none", count = 0 });
        empty.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ---------------------------------------------------------------- the life of a run

    [Fact]
    public async Task A_run_is_queued_then_finished_by_the_worker_and_the_report_can_be_read_both_ways()
    {
        var s = await NewTenant();
        var dataset = await CreateDataset(s.Token);
        var run = await StartRun(s, dataset);

        var queued = await GetRun(s.Token, run);
        queued.GetProperty("state").GetString().Should().Be("Queued");
        queued.GetProperty("progress").GetProperty("total").GetInt32().Should().Be(40);
        queued.GetProperty("progress").GetProperty("done").GetInt32().Should().Be(0);
        queued.GetProperty("policyVersion").GetInt32().Should().Be(1);   // no version was named and nothing is published: the draft

        await RunQueue();

        var done = await GetRun(s.Token, run);
        done.GetProperty("state").GetString().Should().Be("Succeeded");
        done.GetProperty("progress").GetProperty("done").GetInt32().Should().Be(40);
        var result = done.GetProperty("result");
        result.GetProperty("recall").GetDouble().Should().BeGreaterThan(0.5);
        result.GetProperty("perType").TryGetProperty("SSN", out _).Should().BeTrue();

        var json = await Send(HttpMethod.Get, $"/api/v1/evaluations/{run}/report", s.Token);
        json.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await Json(json);
        report.GetProperty("notes").GetInt32().Should().Be(40);
        report.GetProperty("mode").GetString().Should().Be("rules+ner");

        var md = await Send(HttpMethod.Get, $"/api/v1/evaluations/{run}/report?format=md", s.Token);
        md.StatusCode.Should().Be(HttpStatusCode.OK);
        md.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        (await md.Content.ReadAsStringAsync()).Should().Contain("# Evaluation report").And.Contain("## By type");

        (await Send(HttpMethod.Get, $"/api/v1/evaluations/{run}/report?format=pdf", s.Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var list = await Json(await Send(HttpMethod.Get, "/api/v1/evaluations", s.Token));
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Contain(run);
    }

    [Fact]
    public async Task A_run_that_has_not_finished_has_no_report_and_cannot_be_compared()
    {
        var s = await NewTenant();
        var dataset = await CreateDataset(s.Token);
        var run = await StartRun(s, dataset);

        var report = await Send(HttpMethod.Get, $"/api/v1/evaluations/{run}/report", s.Token);
        report.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Code(await Json(report)).Should().Be("evaluation.not_finished");

        (await Send(HttpMethod.Get, $"/api/v1/evaluations/compare?a={run}&b={run}", s.Token)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await RunQueue();
    }

    [Fact]
    public async Task Leaks_are_listed_in_order_and_a_better_setup_shows_up_in_the_comparison()
    {
        var s = await NewTenant();
        var dataset = await CreateDataset(s.Token);
        var rules = await StartRun(s, dataset, "rules");        // no name detector: names get through
        var withNer = await StartRun(s, dataset, "rules+ner");
        await RunQueue();

        var leaks = await Json(await Send(HttpMethod.Get, $"/api/v1/evaluations/{rules}/leaks", s.Token));
        var items = leaks.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        items.Should().Contain(i => i.GetProperty("entityType").GetString() == "PERSON" && i.GetProperty("reason").GetString() == "not_detected");
        items.Select(i => i.GetProperty("caseOrdinal").GetInt32()).Should().BeInAscendingOrder();
        items.Should().OnlyContain(i => !string.IsNullOrEmpty(i.GetProperty("value").GetString()));

        var firstPage = await Json(await Send(HttpMethod.Get, $"/api/v1/evaluations/{rules}/leaks?pageSize=1", s.Token));
        firstPage.GetProperty("items").GetArrayLength().Should().Be(1);

        var compare = await Json(await Send(HttpMethod.Get, $"/api/v1/evaluations/compare?a={rules}&b={withNer}", s.Token));
        compare.GetProperty("datasetId").GetGuid().Should().Be(dataset);
        compare.GetProperty("recall").GetProperty("delta").GetDouble().Should().BeGreaterThan(0);
        compare.GetProperty("leaksA").GetInt32().Should().BeGreaterThan(compare.GetProperty("leaksB").GetInt32());
        compare.GetProperty("perType").GetProperty("PERSON").GetProperty("recall").GetProperty("delta").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Evaluations_of_different_datasets_cannot_be_compared()
    {
        var s = await NewTenant();
        var first = await StartRun(s, await CreateDataset(s.Token));
        var second = await StartRun(s, await CreateDataset(s.Token));
        await RunQueue();

        var response = await Send(HttpMethod.Get, $"/api/v1/evaluations/compare?a={first}&b={second}", s.Token);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Code(await Json(response)).Should().Be("evaluation.different_datasets");
        (await Send(HttpMethod.Get, $"/api/v1/evaluations/compare?a={first}&b={Guid.NewGuid()}", s.Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public async Task A_run_naming_something_that_does_not_exist_or_is_malformed_is_refused()
    {
        var s = await NewTenant();
        var dataset = await CreateDataset(s.Token);

        var noDataset = await Send(HttpMethod.Post, "/api/v1/evaluations", s.Token, new { datasetId = Guid.NewGuid(), policyId = s.PolicyId });
        noDataset.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Code(await Json(noDataset)).Should().Be("dataset.not_found");

        var noPolicy = await Send(HttpMethod.Post, "/api/v1/evaluations", s.Token, new { datasetId = dataset, policyId = Guid.NewGuid() });
        Code(await Json(noPolicy)).Should().Be("policy.not_found");

        var noVersion = await Send(HttpMethod.Post, "/api/v1/evaluations", s.Token, new { datasetId = dataset, policyId = s.PolicyId, policyVersion = 99 });
        noVersion.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Code(await Json(noVersion)).Should().Be("policy.version_not_found");

        (await Send(HttpMethod.Post, "/api/v1/evaluations", s.Token, new { datasetId = dataset, policyId = s.PolicyId, mode = "deep" }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Send(HttpMethod.Post, "/api/v1/evaluations", s.Token, new { datasetId = dataset, policyId = s.PolicyId, recallThreshold = 1.5 }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Only_a_few_runs_can_wait_at_once()
    {
        var s = await NewTenant();
        var dataset = await CreateDataset(s.Token, count: 5);
        for (var i = 0; i < CreateEvaluationHandler.MaxActiveRuns; i++) await StartRun(s, dataset);

        var response = await Send(HttpMethod.Post, "/api/v1/evaluations", s.Token, new { datasetId = dataset, policyId = s.PolicyId });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Code(await Json(response)).Should().Be("evaluation.too_many_active");
        await RunQueue();
    }

    // ---------------------------------------------------------------- isolation

    [Fact]
    public async Task Another_tenant_can_neither_see_nor_use_an_evaluation_dataset_or_policy()
    {
        var owner = await NewTenant();
        var dataset = await CreateDataset(owner.Token);
        var run = await StartRun(owner, dataset);
        await RunQueue();
        var other = await NewTenant();

        foreach (var path in new[] { "", "/leaks", "/report" })
            (await Send(HttpMethod.Get, $"/api/v1/evaluations/{run}{path}", other.Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, $"/api/v1/datasets/{dataset}", other.Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var theirListing = await Json(await Send(HttpMethod.Get, "/api/v1/evaluations", other.Token));
        theirListing.GetProperty("items").GetArrayLength().Should().Be(0);

        var useDataset = await Send(HttpMethod.Post, "/api/v1/evaluations", other.Token, new { datasetId = dataset, policyId = other.PolicyId });
        Code(await Json(useDataset)).Should().Be("dataset.not_found");

        var ownDataset = await CreateDataset(other.Token);
        var usePolicy = await Send(HttpMethod.Post, "/api/v1/evaluations", other.Token, new { datasetId = ownDataset, policyId = owner.PolicyId });
        Code(await Json(usePolicy)).Should().Be("policy.not_found");
    }
}
