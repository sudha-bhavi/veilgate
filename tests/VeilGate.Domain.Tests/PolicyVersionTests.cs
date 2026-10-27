using FluentAssertions;
using VeilGate.Domain.Policies;
using Xunit;

namespace VeilGate.Domain.Tests;

public class PolicyVersionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid PolicyId = Guid.CreateVersion7();
    private static readonly Guid Author = Guid.CreateVersion7();
    private static readonly Guid Approver = Guid.CreateVersion7();
    private const string Yaml = "version: 1\nname: Default\n";
    private const string Summary = "Tighten SSN handling";

    private static PolicyVersion Draft() => PolicyVersion.CreateDraft(Tenant, PolicyId, 1, Yaml, Author).Value;

    private static PolicyVersion Pending()
    {
        var v = Draft();
        v.Submit(Author, Summary, Now);
        return v;
    }

    [Fact]
    public void CreateDraft_starts_in_draft_with_a_hash_of_the_text()
    {
        var v = Draft();

        v.State.Should().Be(PolicyState.Draft);
        v.TenantId.Should().Be(Tenant);
        v.DefinitionSha256.Should().Be(PolicyVersion.Hash(Yaml));
    }

    [Fact]
    public void CreateDraft_rejects_empty_text()
        => PolicyVersion.CreateDraft(Tenant, PolicyId, 1, "  ", Author).IsFailure.Should().BeTrue();

    [Fact]
    public void EditDraft_updates_text_and_hash()
    {
        var v = Draft();
        v.EditDraft("version: 1\nname: Other\n").IsSuccess.Should().BeTrue();
        v.DefinitionSha256.Should().Be(PolicyVersion.Hash("version: 1\nname: Other\n"));
    }

    [Fact]
    public void Submit_moves_to_pending_and_records_who_and_why()
    {
        var v = Pending();

        v.State.Should().Be(PolicyState.PendingApproval);
        v.SubmittedBy.Should().Be(Author);
        v.ChangeNote.Should().Be(Summary);
    }

    [Theory]
    [InlineData("too short")]
    [InlineData("")]
    public void Submit_needs_a_summary_of_10_to_500_characters(string summary)
        => Draft().Submit(Author, summary, Now).IsFailure.Should().BeTrue();

    [Fact]
    public void A_submitted_version_cannot_be_edited()
    {
        var v = Pending();
        v.EditDraft("version: 1\nname: Sneaky\n").IsFailure.Should().BeTrue();
        v.Definition.Should().Be(Yaml);
    }

    [Fact]
    public void Publish_by_a_different_person_succeeds()
    {
        var v = Pending();

        v.Publish(Approver, Now).IsSuccess.Should().BeTrue();

        v.State.Should().Be(PolicyState.Published);
        v.ApprovedBy.Should().Be(Approver);
        v.PublishedAt.Should().Be(Now);
    }

    [Fact]
    public void Publish_by_the_submitter_is_refused()
    {
        var v = Pending();

        var result = v.Publish(Author, Now);

        result.Error.Code.Should().Be("policy.self_approval");
        v.State.Should().Be(PolicyState.PendingApproval);
    }

    [Fact]
    public void A_draft_cannot_be_published_directly()
        => Draft().Publish(Approver, Now).IsFailure.Should().BeTrue();

    [Fact]
    public void Reject_needs_a_reason_and_ends_the_review()
    {
        var v = Pending();
        v.Reject("").IsFailure.Should().BeTrue();
        v.Reject("SSN must be BLOCK").IsSuccess.Should().BeTrue();
        v.State.Should().Be(PolicyState.Rejected);
    }

    [Fact]
    public void Only_a_published_version_can_be_archived()
    {
        var v = Pending();
        v.Archive().IsFailure.Should().BeTrue();
        v.Publish(Approver, Now);
        v.Archive().IsSuccess.Should().BeTrue();
        v.State.Should().Be(PolicyState.Archived);
    }
}