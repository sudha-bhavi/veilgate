using FluentAssertions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.ApiClients;
using VeilGate.Domain.ApiClients;
using VeilGate.Domain.Audit;
using Xunit;

namespace VeilGate.Domain.Tests;

/// <summary>POST /oauth/token without HTTP: who gets a token, with which scopes, and what is recorded when someone does not.</summary>
public class IssueClientTokenHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private const string Secret = "vgs_correct-secret";

    private readonly IApiClientRepository _clients = Substitute.For<IApiClientRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();
    private readonly ISecurityEventWriter _events = Substitute.For<ISecurityEventWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly CountingHasher _hasher = new();
    private readonly IssueClientTokenHandler _handler;

    public IssueClientTokenHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _users.IsTenantActiveAsync(Arg.Any<CancellationToken>()).Returns(true);
        _tokens.IssueClientToken(Arg.Any<ClientTokenClaims>(), Arg.Any<DateTimeOffset?>()).Returns("signed.jwt.token");
        _handler = new IssueClientTokenHandler(_clients, _users, _hasher, _tenant, _tokens, _events, _clock);
    }

    private ApiClient Registered(string[]? scopes = null, string[]? ips = null, int? rate = null, params (string plain, DateTimeOffset? expires)[] secrets)
    {
        var client = ApiClient.Create(Tenant, Guid.CreateVersion7(), "ehr-bridge", scopes ?? ["gateway:invoke", "gateway:deidentify"],
            ipAllowlist: ips, rateLimitPerMinute: rate).Value;
        _clients.FindTenantByClientIdAsync(client.ClientId, Arg.Any<CancellationToken>()).Returns(Tenant);
        _clients.FindByClientIdAsync(client.ClientId, Arg.Any<CancellationToken>()).Returns(client);

        if (secrets.Length == 0) secrets = [(Secret, null)];
        var rows = secrets.Select(s =>
        {
            var row = ApiClientSecret.Create(Tenant, client.Id, _hasher.Hash(s.plain)).Value;
            if (s.expires is { } e) row.ExpireNoLaterThan(e);
            return row;
        }).ToList();
        _clients.ListSecretsAsync(client.Id, Arg.Any<CancellationToken>()).Returns(rows);
        return client;
    }

    private Task<VeilGate.Domain.Common.Result<IssuedClientToken>> Ask(ApiClient c, string? secret = Secret, string? scope = null, string? ip = "203.0.113.9") =>
        _handler.HandleAsync(new IssueClientTokenCommand(c.ClientId, secret, scope, ip));

    private void SecurityEventShouldBe(string type, Severity severity, Guid clientId) =>
        _events.Received(1).WriteAsync(
            Arg.Is<SecurityEvent>(e => e.Type == type && e.Severity == severity && e.ClientId == clientId && e.TenantId == Tenant),
            Arg.Any<CancellationToken>());

    // ------------------------------------------------------------ success

    [Fact]
    public async Task The_right_secret_gets_a_token_with_all_of_the_clients_scopes()
    {
        var client = Registered(rate: 250);

        var result = await Ask(client);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("signed.jwt.token");
        result.Value.ExpiresInSeconds.Should().Be(900);
        result.Value.Scope.Should().Be("gateway:invoke gateway:deidentify");
        _tenant.Received(1).Set(Tenant);
        _tokens.Received(1).IssueClientToken(
            Arg.Is<ClientTokenClaims>(c => c.ClientId == client.Id && c.TenantId == Tenant && c.RateLimitPerMinute == 250
                                           && c.Scopes.SequenceEqual(new[] { "gateway:invoke", "gateway:deidentify" })),
            Arg.Any<DateTimeOffset?>());
        await _events.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }

    [Fact]
    public async Task A_narrower_scope_can_be_asked_for()
    {
        var client = Registered();

        var result = await Ask(client, scope: "gateway:deidentify");

        result.Value.Scope.Should().Be("gateway:deidentify");
        _tokens.Received(1).IssueClientToken(Arg.Is<ClientTokenClaims>(c => c.Scopes.SequenceEqual(new[] { "gateway:deidentify" })), Arg.Any<DateTimeOffset?>());
    }

    [Fact]
    public async Task A_scope_the_client_does_not_have_is_refused_and_no_token_is_made()
    {
        var client = Registered();

        var result = await Ask(client, scope: "gateway:invoke gateway:reidentify");

        result.Error.Code.Should().Be("oauth.invalid_scope");
        _tokens.DidNotReceiveWithAnyArgs().IssueClientToken(default!, default);
    }

    [Fact]
    public async Task Both_secrets_work_during_a_rotation_and_the_old_one_stops_when_its_grace_ends()
    {
        var client = Registered(secrets: [("old-secret", Now.AddHours(1)), ("new-secret", null)]);

        (await Ask(client, "old-secret")).IsSuccess.Should().BeTrue();
        (await Ask(client, "new-secret")).IsSuccess.Should().BeTrue();

        _clock.UtcNow.Returns(Now.AddHours(2));
        (await Ask(client, "old-secret")).Error.Code.Should().Be("oauth.invalid_client");
        (await Ask(client, "new-secret")).IsSuccess.Should().BeTrue();
    }

    // ------------------------------------------------------------ failures

    [Fact]
    public async Task A_wrong_secret_is_refused_and_recorded_as_a_security_event()
    {
        var client = Registered();

        var result = await Ask(client, "vgs_wrong");

        result.Error.Code.Should().Be("oauth.invalid_client");
        SecurityEventShouldBe("oauth.invalid_secret", Severity.Low, client.Id);
        _tokens.DidNotReceiveWithAnyArgs().IssueClientToken(default!, default);
    }

    [Fact]
    public async Task An_unknown_client_gets_the_same_answer_and_the_same_hashing_effort()
    {
        var result = await _handler.HandleAsync(new IssueClientTokenCommand("vg_doesnotexist", Secret, null, "203.0.113.9"));

        result.Error.Code.Should().Be("oauth.invalid_client");
        _hasher.DummyCalls.Should().Be(1);
        await _events.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);   // nothing to attach the event to: no tenant is known
    }

    [Fact]
    public async Task A_revoked_client_cannot_get_a_token()
    {
        var client = Registered();
        // The lookup function only finds ACTIVE clients, so a revoked one is unknown to it.
        _clients.FindTenantByClientIdAsync(client.ClientId, Arg.Any<CancellationToken>()).Returns((Guid?)null);

        var result = await Ask(client);

        result.Error.Code.Should().Be("oauth.invalid_client");
        _hasher.DummyCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_revoked_client_that_is_still_found_is_refused_too()
    {
        var client = Registered();
        client.Revoke(Now);

        var result = await Ask(client);

        result.Error.Code.Should().Be("oauth.invalid_client");
    }

    [Fact]
    public async Task A_suspended_tenant_gets_no_tokens()
    {
        var client = Registered();
        _users.IsTenantActiveAsync(Arg.Any<CancellationToken>()).Returns(false);

        var result = await Ask(client);

        result.Error.Code.Should().Be("oauth.invalid_client");
    }

    [Fact]
    public async Task A_client_whose_secrets_have_all_expired_is_refused()
    {
        var client = Registered(secrets: [(Secret, Now.AddMinutes(-1))]);

        var result = await Ask(client);

        result.Error.Code.Should().Be("oauth.invalid_client");
    }

    [Fact]
    public async Task A_correct_secret_from_outside_the_allowlist_is_refused_like_a_wrong_one_and_recorded()
    {
        var client = Registered(ips: ["203.0.113.0/24"]);

        var outside = await Ask(client, ip: "198.51.100.7");
        var inside = await Ask(client, ip: "203.0.113.50");

        outside.Error.Code.Should().Be("oauth.invalid_client");
        inside.IsSuccess.Should().BeTrue();
        SecurityEventShouldBe("oauth.ip_denied", Severity.Medium, client.Id);
    }

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("", "secret")]
    [InlineData("vg_x", null)]
    [InlineData("vg_x", "")]
    public async Task A_missing_id_or_secret_is_refused_without_touching_the_database(string? id, string? secret)
    {
        var result = await _handler.HandleAsync(new IssueClientTokenCommand(id, secret, null, null));

        result.Error.Code.Should().Be("oauth.invalid_client");
        await _clients.DidNotReceiveWithAnyArgs().FindTenantByClientIdAsync(default!, default);
    }

    [Fact]
    public async Task Giant_inputs_are_refused_without_a_database_call_or_a_long_hash()
    {
        var result = await _handler.HandleAsync(new IssueClientTokenCommand(new string('x', 41), Secret, null, null));
        var secret = await _handler.HandleAsync(new IssueClientTokenCommand("vg_x", new string('s', 257), null, null));

        result.Error.Code.Should().Be("oauth.invalid_client");
        secret.Error.Code.Should().Be("oauth.invalid_client");
        await _clients.DidNotReceiveWithAnyArgs().FindTenantByClientIdAsync(default!, default);
    }

    private sealed class CountingHasher : IPasswordHasher
    {
        public int DummyCalls;
        public string Hash(string password) => "fake$" + password;
        public bool Verify(string password, string encodedHash) => encodedHash == "fake$" + password;
        public void VerifyDummy(string password) => DummyCalls++;
        public bool NeedsRehash(string encodedHash) => false;
    }
}
