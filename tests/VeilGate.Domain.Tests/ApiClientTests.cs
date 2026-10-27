using FluentAssertions;
using VeilGate.Domain.ApiClients;
using Xunit;

namespace VeilGate.Domain.Tests;

public class ApiClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();

    private static ApiClient NewClient(IEnumerable<string>? ips = null) =>
        ApiClient.Create(Tenant, Owner, "ehr-bridge", ["gateway:invoke", "gateway:deidentify"], ipAllowlist: ips).Value;

    // ------------------------------------------------------------ create

    [Fact]
    public void Create_makes_an_active_client_with_a_generated_public_id()
    {
        var c = NewClient();

        c.TenantId.Should().Be(Tenant);
        c.OwnerUserId.Should().Be(Owner);
        c.Status.Should().Be(ApiClientStatus.Active);
        c.ClientId.Should().MatchRegex("^vg_[0-9a-f]{32}$");
        c.RateLimitPerMinute.Should().Be(ApiClient.DefaultRateLimitPerMinute);
        c.IpAllowlist.Should().BeEmpty();
    }

    [Fact]
    public void Two_clients_never_share_a_public_id()
        => NewClient().ClientId.Should().NotBe(NewClient().ClientId);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_needs_a_name(string name)
        => ApiClient.Create(Tenant, Owner, name, ["gateway:invoke"]).IsFailure.Should().BeTrue();

    [Fact]
    public void Create_refuses_a_name_over_80_characters()
        => ApiClient.Create(Tenant, Owner, new string('x', 81), ["gateway:invoke"]).IsFailure.Should().BeTrue();

    [Fact]
    public void Create_needs_at_least_one_known_scope()
    {
        ApiClient.Create(Tenant, Owner, "x", []).IsFailure.Should().BeTrue();
        ApiClient.Create(Tenant, Owner, "x", ["gateway:admin"]).IsFailure.Should().BeTrue();
        ApiClient.Create(Tenant, Owner, "x", ["gateway:invoke", "nope"]).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_removes_duplicate_scopes()
        => ApiClient.Create(Tenant, Owner, "x", ["gateway:invoke", "gateway:invoke"]).Value.Scopes.Should().Equal("gateway:invoke");

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void Create_keeps_the_rate_limit_within_bounds(int limit)
        => ApiClient.Create(Tenant, Owner, "x", ["gateway:invoke"], rateLimitPerMinute: limit).IsFailure.Should().BeTrue();

    [Fact]
    public void Create_refuses_an_empty_tenant_or_owner()
    {
        ApiClient.Create(Guid.Empty, Owner, "x", ["gateway:invoke"]).IsFailure.Should().BeTrue();
        ApiClient.Create(Tenant, Guid.Empty, "x", ["gateway:invoke"]).IsFailure.Should().BeTrue();
    }

    // ------------------------------------------------------------ networks

    [Theory]
    [InlineData("203.0.113.0/24", "203.0.113.0/24")]
    [InlineData("203.0.113.9", "203.0.113.9/32")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("  10.0.0.0/8  ", "10.0.0.0/8")]
    public void A_network_is_stored_in_one_canonical_spelling(string input, string expected)
    {
        ApiClient.TryNormalizeCidr(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.0/33")]
    [InlineData("203.0.113.9/24")]   // bits set after the prefix: refused, not silently changed
    [InlineData("999.1.1.1")]
    public void A_bad_network_is_refused(string input)
        => ApiClient.TryNormalizeCidr(input, out _).Should().BeFalse();

    [Fact]
    public void Create_refuses_a_bad_network_and_too_many_networks()
    {
        ApiClient.Create(Tenant, Owner, "x", ["gateway:invoke"], ipAllowlist: ["nope"]).IsFailure.Should().BeTrue();

        var many = Enumerable.Range(0, ApiClient.MaxIpAllowlistEntries + 1).Select(i => $"10.0.{i}.0/24");
        ApiClient.Create(Tenant, Owner, "x", ["gateway:invoke"], ipAllowlist: many).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void No_allowlist_means_any_address_may_call()
    {
        var c = NewClient();

        c.IsIpAllowed("198.51.100.7").Should().BeTrue();
        c.IsIpAllowed(null).Should().BeTrue();
    }

    [Fact]
    public void With_an_allowlist_only_addresses_inside_it_may_call()
    {
        var c = NewClient(["203.0.113.0/24", "2001:db8::/32"]);

        c.IsIpAllowed("203.0.113.9").Should().BeTrue();
        c.IsIpAllowed("2001:db8::1").Should().BeTrue();
        c.IsIpAllowed("203.0.114.9").Should().BeFalse();
        c.IsIpAllowed("198.51.100.7").Should().BeFalse();
        c.IsIpAllowed(null).Should().BeFalse();
        c.IsIpAllowed("garbage").Should().BeFalse();
    }

    [Fact]
    public void An_ipv4_address_wrapped_as_ipv6_counts_as_the_ipv4_address()
        => NewClient(["203.0.113.0/24"]).IsIpAllowed("::ffff:203.0.113.9").Should().BeTrue();

    // ------------------------------------------------------------ update and revoke

    [Fact]
    public void Update_changes_only_what_is_given()
    {
        var c = NewClient(["203.0.113.0/24"]);
        var policy = Guid.CreateVersion7();

        c.Update(scopes: null, ipAllowlist: null, rateLimitPerMinute: 50, defaultPolicyId: policy).IsSuccess.Should().BeTrue();

        c.RateLimitPerMinute.Should().Be(50);
        c.DefaultPolicyId.Should().Be(policy);
        c.Scopes.Should().Equal("gateway:invoke", "gateway:deidentify");
        c.IpAllowlist.Should().Equal("203.0.113.0/24");
    }

    [Fact]
    public void An_update_with_one_bad_field_changes_nothing()
    {
        var c = NewClient();

        var result = c.Update(scopes: ["gateway:detect"], ipAllowlist: ["nope"], rateLimitPerMinute: 50, defaultPolicyId: null);

        result.IsFailure.Should().BeTrue();
        c.Scopes.Should().Equal("gateway:invoke", "gateway:deidentify");
        c.RateLimitPerMinute.Should().Be(ApiClient.DefaultRateLimitPerMinute);
    }

    [Fact]
    public void An_empty_list_clears_the_allowlist_but_not_the_scopes()
    {
        var c = NewClient(["203.0.113.0/24"]);

        c.Update(null, [], null, null).IsSuccess.Should().BeTrue();
        c.IpAllowlist.Should().BeEmpty();

        c.Update([], null, null, null).IsFailure.Should().BeTrue();   // a client with no scope is useless; revoke it instead
        c.Scopes.Should().NotBeEmpty();
    }

    [Fact]
    public void Revoke_ends_the_client_for_good()
    {
        var c = NewClient();

        c.Revoke(Now).IsSuccess.Should().BeTrue();

        c.IsActive.Should().BeFalse();
        c.RevokedAt.Should().Be(Now);
        c.Revoke(Now).IsFailure.Should().BeTrue();
        c.Update(null, null, 10, null).IsFailure.Should().BeTrue();
    }

    // ------------------------------------------------------------ credentials and secrets

    [Fact]
    public void A_generated_secret_is_long_url_safe_and_different_every_time()
    {
        var a = ApiClientCredentials.NewSecret();
        var b = ApiClientCredentials.NewSecret();

        a.Should().MatchRegex("^vgs_[A-Za-z0-9_-]{43}$");
        a.Should().NotBe(b);
    }

    [Fact]
    public void A_secret_without_an_end_date_is_valid_until_it_is_given_one()
    {
        var s = ApiClientSecret.Create(Tenant, Guid.CreateVersion7(), "$argon2id$x").Value;

        s.IsValidAt(Now).Should().BeTrue();
        s.ExpireNoLaterThan(Now.AddHours(1));
        s.IsValidAt(Now.AddMinutes(59)).Should().BeTrue();
        s.IsValidAt(Now.AddHours(1)).Should().BeFalse();
    }

    [Fact]
    public void A_secret_end_date_can_be_brought_forward_but_never_pushed_back()
    {
        var s = ApiClientSecret.Create(Tenant, Guid.CreateVersion7(), "$argon2id$x").Value;
        s.ExpireNoLaterThan(Now.AddHours(24));

        s.ExpireNoLaterThan(Now.AddHours(1));
        s.ExpiresAt.Should().Be(Now.AddHours(1));

        s.ExpireNoLaterThan(Now.AddHours(48));
        s.ExpiresAt.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void A_secret_needs_a_hash_and_an_owner()
    {
        ApiClientSecret.Create(Tenant, Guid.CreateVersion7(), " ").IsFailure.Should().BeTrue();
        ApiClientSecret.Create(Tenant, Guid.Empty, "$argon2id$x").IsFailure.Should().BeTrue();
    }
}
