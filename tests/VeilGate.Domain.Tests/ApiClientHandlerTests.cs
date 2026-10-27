using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.ApiClients;
using VeilGate.Domain.ApiClients;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Policies;
using Xunit;

namespace VeilGate.Domain.Tests;

/// <summary>Creating, changing, rotating and revoking API clients, and who may do it.</summary>
public class ApiClientHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly ApiClientActor Admin = new(Guid.CreateVersion7(), "TenantAdmin", "203.0.113.9");
    private static readonly ApiClientActor Dev = new(Guid.CreateVersion7(), "Developer", "203.0.113.10");
    private static readonly ApiClientActor OtherDev = new(Guid.CreateVersion7(), "Developer", "203.0.113.11");

    private readonly IApiClientRepository _clients = Substitute.For<IApiClientRepository>();
    private readonly IPolicyRepository _policies = Substitute.For<IPolicyRepository>();
    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly FakeHasher _hasher = new();
    private readonly ApiClientAuditor _auditor;

    public ApiClientHandlerTests()
    {
        _tenant.TenantId.Returns(Tenant);
        _clock.UtcNow.Returns(Now);
        _auditor = new ApiClientAuditor(_audit, _tenant, _clock, NullLogger<ApiClientAuditor>.Instance);
    }

    private ApiClient ExistingClient(ApiClientActor owner, params ApiClientSecret[] secrets)
    {
        var client = ApiClient.Create(Tenant, owner.UserId, "ehr-bridge", ["gateway:invoke"]).Value;
        _clients.FindAsync(client.Id, Arg.Any<CancellationToken>()).Returns(client);
        _clients.ListSecretsAsync(client.Id, Arg.Any<CancellationToken>()).Returns(secrets.ToList());
        return client;
    }

    private ApiClientSecret Secret(Guid clientPk, string plain, DateTimeOffset? expires = null)
    {
        var s = ApiClientSecret.Create(Tenant, clientPk, _hasher.Hash(plain)).Value;
        if (expires is { } e) s.ExpireNoLaterThan(e);
        return s;
    }

    private void AuditShouldHaveRecorded(string action) =>
        _audit.Received(1).WriteAsync(
            Arg.Is<AuditEvent>(e => e.Action == action && e.TenantId == Tenant && e.ResourceType == "api_client" && e.Details.ContainsKey("clientId")),
            Arg.Any<CancellationToken>());

    private CreateApiClientHandler CreateHandler() => new(_clients, _policies, _hasher, _tenant, _auditor);

    // ------------------------------------------------------------ create

    [Fact]
    public async Task Create_stores_the_client_and_only_a_hash_of_its_secret_and_returns_the_secret_once()
    {
        ApiClientSecret? stored = null;
        _clients.When(c => c.AddSecret(Arg.Any<ApiClientSecret>())).Do(call => stored = call.Arg<ApiClientSecret>());

        var result = await CreateHandler().HandleAsync(
            new CreateApiClientCommand("ehr-bridge", ["gateway:invoke", "gateway:deidentify"], null, ["203.0.113.0/24"], 100, Dev));

        result.IsSuccess.Should().BeTrue();
        result.Value.ClientSecret.Should().StartWith("vgs_");
        result.Value.Client.ClientId.Should().StartWith("vg_");
        result.Value.Client.OwnerUserId.Should().Be(Dev.UserId);
        result.Value.Client.RateLimitPerMinute.Should().Be(100);
        stored!.SecretHash.Should().NotBe(result.Value.ClientSecret);   // the real database test checks that no plaintext reaches a table
        _hasher.Verify(result.Value.ClientSecret, stored.SecretHash).Should().BeTrue();
        await _clients.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        AuditShouldHaveRecorded("apiclient.create");
    }

    [Fact]
    public async Task The_audit_event_never_contains_the_secret()
    {
        AuditEvent? written = null;
        await _audit.WriteAsync(Arg.Do<AuditEvent>(e => written = e), Arg.Any<CancellationToken>());

        var result = await CreateHandler().HandleAsync(new CreateApiClientCommand("x", ["gateway:invoke"], null, null, null, Admin));

        written.Should().NotBeNull();
        written!.Details.Values.Should().NotContain(v => v.Contains(result.Value.ClientSecret));
    }

    [Fact]
    public async Task Create_refuses_a_name_that_is_taken()
    {
        _clients.NameExistsAsync("ehr-bridge", Arg.Any<CancellationToken>()).Returns(true);

        var result = await CreateHandler().HandleAsync(new CreateApiClientCommand("ehr-bridge", ["gateway:invoke"], null, null, null, Admin));

        result.Error.Code.Should().Be("resource.conflict");
        await _clients.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Create_refuses_a_default_policy_that_does_not_exist_or_is_archived()
    {
        var archived = Policy.Create(Tenant, "Old", null).Value;
        archived.Archive(Now);
        _policies.FindAsync(archived.Id, Arg.Any<CancellationToken>()).Returns(archived);

        var missing = await CreateHandler().HandleAsync(new CreateApiClientCommand("a", ["gateway:invoke"], Guid.NewGuid(), null, null, Admin));
        var gone = await CreateHandler().HandleAsync(new CreateApiClientCommand("b", ["gateway:invoke"], archived.Id, null, null, Admin));

        missing.Error.Code.Should().Be("validation.failed");
        gone.Error.Code.Should().Be("validation.failed");
        await _clients.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Create_accepts_a_default_policy_of_the_tenant()
    {
        var policy = Policy.Create(Tenant, "Default", null).Value;
        _policies.FindAsync(policy.Id, Arg.Any<CancellationToken>()).Returns(policy);

        var result = await CreateHandler().HandleAsync(new CreateApiClientCommand("a", ["gateway:invoke"], policy.Id, null, null, Admin));

        result.Value.Client.DefaultPolicyId.Should().Be(policy.Id);
    }

    [Fact]
    public async Task Create_passes_on_the_domain_rules()
    {
        var result = await CreateHandler().HandleAsync(new CreateApiClientCommand("a", ["gateway:root"], null, null, null, Admin));

        result.Error.Code.Should().Be("validation.failed");
    }

    // ------------------------------------------------------------ update, get, list

    [Fact]
    public async Task A_developer_can_change_their_own_client_but_not_someone_elses()
    {
        var client = ExistingClient(Dev);
        var handler = new UpdateApiClientHandler(_clients, _policies, _auditor);

        var mine = await handler.HandleAsync(new UpdateApiClientCommand(client.Id, null, null, 50, null, Dev));
        var theirs = await handler.HandleAsync(new UpdateApiClientCommand(client.Id, null, null, 70, null, OtherDev));

        mine.Value.RateLimitPerMinute.Should().Be(50);
        theirs.Error.Code.Should().Be("resource.not_found");
        client.RateLimitPerMinute.Should().Be(50);
        AuditShouldHaveRecorded("apiclient.update");
    }

    [Fact]
    public async Task A_tenant_admin_can_change_any_client_of_the_tenant()
    {
        var client = ExistingClient(Dev);

        var result = await new UpdateApiClientHandler(_clients, _policies, _auditor)
            .HandleAsync(new UpdateApiClientCommand(client.Id, ["gateway:detect"], null, null, null, Admin));

        result.Value.Scopes.Should().Equal("gateway:detect");
    }

    [Fact]
    public async Task Update_refuses_a_default_policy_that_does_not_exist()
    {
        var client = ExistingClient(Admin);

        var result = await new UpdateApiClientHandler(_clients, _policies, _auditor)
            .HandleAsync(new UpdateApiClientCommand(client.Id, null, null, null, Guid.NewGuid(), Admin));

        result.Error.Code.Should().Be("validation.failed");
        client.DefaultPolicyId.Should().BeNull();
    }

    [Fact]
    public async Task Get_hides_another_developers_client_as_not_found()
    {
        var client = ExistingClient(Dev);
        var handler = new GetApiClientHandler(_clients);

        (await handler.HandleAsync(client.Id, Dev)).IsSuccess.Should().BeTrue();
        (await handler.HandleAsync(client.Id, Admin)).IsSuccess.Should().BeTrue();
        (await handler.HandleAsync(client.Id, OtherDev)).Error.Code.Should().Be("resource.not_found");
        (await handler.HandleAsync(Guid.NewGuid(), Admin)).Error.Code.Should().Be("resource.not_found");
    }

    [Fact]
    public async Task A_developers_list_is_limited_to_their_own_clients_and_an_admins_is_not()
    {
        _clients.ListAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), 1, 25, Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<ApiClient>)[], 0));
        var handler = new ListApiClientsHandler(_clients);

        await handler.HandleAsync(Dev, null, null, 1, 25);
        await handler.HandleAsync(Admin, null, null, 1, 25);

        await _clients.Received(1).ListAsync(Dev.UserId, null, null, 1, 25, Arg.Any<CancellationToken>());
        await _clients.Received(1).ListAsync(null, null, null, 1, 25, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_refuses_an_unknown_status()
    {
        var result = await new ListApiClientsHandler(_clients).HandleAsync(Admin, "Bogus", null, 1, 25);

        result.Error.Code.Should().Be("validation.failed");
    }

    // ------------------------------------------------------------ rotate

    [Fact]
    public async Task Rotate_issues_a_new_secret_and_gives_the_old_one_a_grace_period()
    {
        var client = ExistingClient(Admin);
        var old = Secret(client.Id, "old-secret");
        _clients.ListSecretsAsync(client.Id, Arg.Any<CancellationToken>()).Returns(new[] { old });
        ApiClientSecret? added = null;
        _clients.When(c => c.AddSecret(Arg.Any<ApiClientSecret>())).Do(call => added = call.Arg<ApiClientSecret>());

        var result = await new RotateSecretHandler(_clients, _hasher, _tenant, _clock, _auditor)
            .HandleAsync(new RotateSecretCommand(client.Id, 24, Admin));

        result.IsSuccess.Should().BeTrue();
        result.Value.OldSecretsEndAt.Should().Be(Now.AddHours(24));
        old.ExpiresAt.Should().Be(Now.AddHours(24));
        old.IsValidAt(Now.AddHours(23)).Should().BeTrue();
        old.IsValidAt(Now.AddHours(25)).Should().BeFalse();
        added!.ExpiresAt.Should().BeNull();
        _hasher.Verify(result.Value.ClientSecret, added.SecretHash).Should().BeTrue();
        AuditShouldHaveRecorded("apiclient.rotate");
    }

    [Fact]
    public async Task Rotating_again_during_a_grace_period_is_refused()
    {
        var client = ExistingClient(Admin);
        var old = Secret(client.Id, "old", expires: Now.AddHours(5));
        var current = Secret(client.Id, "new");
        _clients.ListSecretsAsync(client.Id, Arg.Any<CancellationToken>()).Returns(new[] { old, current });

        var result = await new RotateSecretHandler(_clients, _hasher, _tenant, _clock, _auditor)
            .HandleAsync(new RotateSecretCommand(client.Id, 1, Admin));

        result.Error.Code.Should().Be("apiclient.rotation_in_progress");
        await _clients.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task An_expired_secret_does_not_count_towards_the_limit_of_two()
    {
        var client = ExistingClient(Admin);
        var expired = Secret(client.Id, "ancient", expires: Now.AddHours(-1));
        var current = Secret(client.Id, "current");
        _clients.ListSecretsAsync(client.Id, Arg.Any<CancellationToken>()).Returns(new[] { expired, current });

        var result = await new RotateSecretHandler(_clients, _hasher, _tenant, _clock, _auditor)
            .HandleAsync(new RotateSecretCommand(client.Id, 1, Admin));

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(1000)]
    public async Task Rotate_allows_only_the_documented_grace_periods(int hours)
    {
        var client = ExistingClient(Admin);

        var result = await new RotateSecretHandler(_clients, _hasher, _tenant, _clock, _auditor)
            .HandleAsync(new RotateSecretCommand(client.Id, hours, Admin));

        result.Error.Code.Should().Be("validation.failed");
    }

    [Fact]
    public async Task A_developer_cannot_rotate_someone_elses_secret_and_a_revoked_client_cannot_be_rotated()
    {
        var client = ExistingClient(Dev);
        var handler = new RotateSecretHandler(_clients, _hasher, _tenant, _clock, _auditor);

        (await handler.HandleAsync(new RotateSecretCommand(client.Id, 1, OtherDev))).Error.Code.Should().Be("resource.not_found");

        client.Revoke(Now);
        (await handler.HandleAsync(new RotateSecretCommand(client.Id, 1, Dev))).Error.Code.Should().Be("resource.conflict");
    }

    // ------------------------------------------------------------ revoke

    [Fact]
    public async Task Revoke_ends_the_client_and_every_secret_at_once()
    {
        var client = ExistingClient(Admin);
        var a = Secret(client.Id, "a");
        var b = Secret(client.Id, "b", expires: Now.AddHours(10));
        _clients.ListSecretsAsync(client.Id, Arg.Any<CancellationToken>()).Returns(new[] { a, b });

        var result = await new RevokeApiClientHandler(_clients, _clock, _auditor).HandleAsync(new RevokeApiClientCommand(client.Id, Admin));

        result.IsSuccess.Should().BeTrue();
        client.IsActive.Should().BeFalse();
        a.IsValidAt(Now).Should().BeFalse();
        b.IsValidAt(Now).Should().BeFalse();
        await _clients.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        AuditShouldHaveRecorded("apiclient.revoke");
    }

    [Fact]
    public async Task A_developer_cannot_revoke_someone_elses_client()
    {
        var client = ExistingClient(Dev);

        var result = await new RevokeApiClientHandler(_clients, _clock, _auditor).HandleAsync(new RevokeApiClientCommand(client.Id, OtherDev));

        result.Error.Code.Should().Be("resource.not_found");
        client.IsActive.Should().BeTrue();
    }

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => "fake$" + password;
        public bool Verify(string password, string encodedHash) => encodedHash == "fake$" + password;
        public void VerifyDummy(string password) { }
        public bool NeedsRehash(string encodedHash) => false;
    }
}
