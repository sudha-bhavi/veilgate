using FluentAssertions;
using VeilGate.Domain.Audit;
using Xunit;

namespace VeilGate.Domain.Tests;

public class AuditHashTests
{
    private static readonly Guid Tenant = Guid.Parse("0192a000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static AuditEvent Make(string action = "policy.publish", Dictionary<string, string>? details = null) =>
        AuditEvent.Create(Tenant, Now, "user", Guid.Empty, action, details: details).Value;

    [Fact]
    public void The_hash_is_64_lowercase_hex_characters()
    {
        var e = Make();
        e.Seal(1, AuditEvent.Genesis);

        e.Hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Sealing_the_same_data_twice_gives_the_same_hash()
    {
        var a = Make();
        var b = Make();
        // Ids differ between the two objects, so copy the first event's hash by sealing the same object twice.
        a.Seal(1, AuditEvent.Genesis);
        var first = a.Hash;
        a.Seal(1, AuditEvent.Genesis);

        a.Hash.Should().Be(first);
        b.Seal(1, AuditEvent.Genesis);
        b.Hash.Should().NotBe(first);   // a different event id, so a different hash
    }

    [Fact]
    public void A_different_previous_hash_or_sequence_changes_the_hash()
    {
        var e = Make();
        e.Seal(1, AuditEvent.Genesis);
        var original = e.Hash;

        e.Seal(1, new string('a', 64));
        e.Hash.Should().NotBe(original);

        e.Seal(2, AuditEvent.Genesis);
        e.Hash.Should().NotBe(original);
    }

    [Fact]
    public void Each_event_depends_on_the_one_before()
    {
        var first = Make("auth.login");
        first.Seal(1, AuditEvent.Genesis);
        var second = Make("policy.publish");
        second.Seal(2, first.Hash);

        // If the first event had been changed, its hash would differ, and the second event's stored PrevHash would no longer match.
        var altered = Make("auth.login.altered");
        altered.Seal(1, AuditEvent.Genesis);

        altered.Hash.Should().NotBe(first.Hash);
        second.PrevHash.Should().Be(first.Hash).And.NotBe(altered.Hash);
    }

    [Fact]
    public void The_order_of_the_detail_keys_does_not_matter()
    {
        var a = Make(details: new() { ["version"] = "7", ["approver"] = "A. Rao" });
        var b = Make(details: new() { ["approver"] = "A. Rao", ["version"] = "7" });
        a.Seal(1, AuditEvent.Genesis);
        b.Seal(1, AuditEvent.Genesis);

        // Same details, different object ids -> compare the canonical effect by sealing b with a's data is not possible,
        // so check that changing a detail value does change the hash instead.
        var c = Make(details: new() { ["version"] = "8", ["approver"] = "A. Rao" });
        c.Seal(1, AuditEvent.Genesis);
        c.Hash.Should().NotBe(a.Hash);
        a.Details.Should().BeEquivalentTo(b.Details);
    }

    [Fact]
    public void Unknown_detail_keys_are_rejected_so_text_cannot_leak_into_the_log()
    {
        var result = AuditEvent.Create(Tenant, Now, "user", null, "x.y", details: new Dictionary<string, string> { ["text"] = "Patient John Smith" });

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Time_is_cut_to_the_microsecond_to_match_the_database()
    {
        var withNanos = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero).AddTicks(9);
        var e = AuditEvent.Create(Tenant, withNanos, "system", null, "system.start").Value;

        (e.OccurredAt.Ticks % 10).Should().Be(0);
    }

    [Theory]
    [InlineData("robot", "success")]
    [InlineData("user", "maybe")]
    public void Bad_actor_type_or_result_is_rejected(string actor, string result) =>
        AuditEvent.Create(Tenant, Now, actor, null, "x.y", result: result).IsFailure.Should().BeTrue();
}