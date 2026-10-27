using FluentAssertions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Policies;
using VeilGate.Domain.Policies;
using Xunit;

namespace VeilGate.Domain.Tests;

public class StoredPolicyResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Author = Guid.CreateVersion7();
    private static readonly Guid Approver = Guid.CreateVersion7();
    private static readonly string ValidYaml = PolicyTemplates.YamlOf(PolicyTemplates.NamesOnly);

    private readonly IPolicyRepository _repo = Substitute.For<IPolicyRepository>();
    private readonly IApiClientRepository _clients = Substitute.For<IApiClientRepository>();
    private readonly StoredPolicyResolver _resolver;

    public StoredPolicyResolverTests() => _resolver = new StoredPolicyResolver(_repo, _clients, new BuiltInPolicyResolver());

    private void ClientDefaults(Guid clientId, Guid? policyId) =>
        _clients.GetStateAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new ApiClientState(VeilGate.Domain.ApiClients.ApiClientStatus.Active, [], policyId));

    [Fact]
    public async Task A_client_with_a_default_policy_uses_it_when_the_request_names_none()
    {
        var (policy, _) = Published(ValidYaml);
        var clientId = Guid.NewGuid();
        ClientDefaults(clientId, policy.Id);

        var result = await _resolver.ResolveAsync(Tenant, clientId, null);

        result.Value.Document.Name.Should().Be(PolicyTemplates.Get(PolicyTemplates.NamesOnly)!.Name);
        result.Value.Version.Should().Be(1);
    }

    [Fact]
    public async Task A_policy_named_in_the_request_beats_the_clients_default()
    {
        var (named, _) = Published(PolicyTemplates.YamlOf(PolicyTemplates.HipaaSafeHarbor));
        var (defaultPolicy, _) = Published(ValidYaml);
        var clientId = Guid.NewGuid();
        ClientDefaults(clientId, defaultPolicy.Id);

        var result = await _resolver.ResolveAsync(Tenant, clientId, named.Id);

        result.Value.Document.Name.Should().Be("hipaa-safe-harbor-v1");
    }

    [Fact]
    public async Task A_client_without_a_default_policy_gets_the_built_in_template()
    {
        var clientId = Guid.NewGuid();
        ClientDefaults(clientId, null);

        var result = await _resolver.ResolveAsync(Tenant, clientId, null);

        result.Value.Document.Name.Should().Be("hipaa-safe-harbor-v1");
    }

    [Fact]
    public async Task A_default_policy_that_cannot_be_served_is_an_error_not_a_quiet_fallback()
    {
        var (policy, _) = Published(ValidYaml);
        policy.Archive(Now);
        var clientId = Guid.NewGuid();
        ClientDefaults(clientId, policy.Id);

        var result = await _resolver.ResolveAsync(Tenant, clientId, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("policy.default_unavailable");
    }

    // A policy with one published version, wired into the fake repository.
    private (Policy policy, PolicyVersion version) Published(string yaml, Guid? tenantId = null)
    {
        var policy = Policy.Create(tenantId ?? Tenant, "Custom", null).Value;
        var version = policy.NewDraft(yaml, Author).Value;
        version.Submit(Author, "Initial version", Now);
        policy.Publish(version, null, Approver, Now).IsSuccess.Should().BeTrue();
        _repo.FindAsync(policy.Id, Arg.Any<CancellationToken>()).Returns(policy);
        _repo.FindVersionAsync(version.Id, Arg.Any<CancellationToken>()).Returns(version);
        return (policy, version);
    }

    [Fact]
    public async Task No_policy_id_serves_the_built_in_template_without_touching_the_database()
    {
        var result = await _resolver.ResolveAsync(Tenant, Guid.NewGuid(), null);

        result.Value.Document.Name.Should().Be("hipaa-safe-harbor-v1");
        await _repo.DidNotReceiveWithAnyArgs().FindAsync(default, default);
    }

    [Fact]
    public async Task A_published_policy_is_parsed_and_reported_with_its_version_number()
    {
        var (policy, _) = Published(ValidYaml);

        var result = await _resolver.ResolveAsync(Tenant, Guid.NewGuid(), policy.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be(1);
        result.Value.Document.Name.Should().Be(PolicyTemplates.Get(PolicyTemplates.NamesOnly)!.Name);
    }

    [Fact]
    public async Task An_unknown_policy_id_is_not_found()
    {
        var result = await _resolver.ResolveAsync(Tenant, Guid.NewGuid(), Guid.NewGuid());

        result.Error.Code.Should().Be("resource.not_found");
    }

    [Fact]
    public async Task A_policy_of_another_tenant_is_not_found()
    {
        var (policy, _) = Published(ValidYaml, tenantId: Guid.CreateVersion7());

        var result = await _resolver.ResolveAsync(Tenant, Guid.NewGuid(), policy.Id);

        result.Error.Code.Should().Be("resource.not_found");
    }

    [Fact]
    public async Task A_policy_that_was_never_published_is_not_found()
    {
        var policy = Policy.Create(Tenant, "Draft only", null).Value;
        policy.NewDraft(ValidYaml, Author);
        _repo.FindAsync(policy.Id, Arg.Any<CancellationToken>()).Returns(policy);

        var result = await _resolver.ResolveAsync(Tenant, Guid.NewGuid(), policy.Id);

        result.Error.Code.Should().Be("resource.not_found");
    }

    [Fact]
    public async Task An_archived_policy_is_not_found()
    {
        var (policy, _) = Published(ValidYaml);
        policy.Archive(Now);

        var result = await _resolver.ResolveAsync(Tenant, Guid.NewGuid(), policy.Id);

        result.Error.Code.Should().Be("resource.not_found");
    }

    [Fact]
    public async Task A_published_policy_that_does_not_parse_fails_closed()
    {
        var (policy, _) = Published("just some words");

        var result = await _resolver.ResolveAsync(Tenant, Guid.NewGuid(), policy.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("policy.invalid");
    }
}