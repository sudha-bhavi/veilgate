using VeilGate.Domain.Common;
using FluentAssertions;
using VeilGate.Domain.Policies;
using Xunit;

namespace VeilGate.Domain.Tests;

public class PolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Author = Guid.CreateVersion7();
    private static readonly Guid Approver = Guid.CreateVersion7();
    private const string Summary = "Tighten SSN handling";

    private static Policy NewPolicy() => Policy.Create(Tenant, "Default", null).Value;

    // Drives a policy through draft -> submit -> publish and returns the published version.
    private static PolicyVersion PublishNext(Policy policy, PolicyVersion? current, string yaml)
    {
        var draft = policy.NewDraft(yaml, Author).Value;
        draft.Submit(Author, Summary, Now);
        policy.Publish(draft, current, Approver, Now).IsSuccess.Should().BeTrue();
        return draft;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_needs_a_name(string name)
        => Policy.Create(Tenant, name, null).IsFailure.Should().BeTrue();

    [Fact]
    public void NewDraft_numbers_versions_and_sets_the_draft_pointer()
    {
        var policy = NewPolicy();

        var draft = policy.NewDraft("a: 1", Author).Value;

        draft.VersionNo.Should().Be(1);
        policy.DraftVersionId.Should().Be(draft.Id);
    }

    [Fact]
    public void Only_one_draft_at_a_time()
    {
        var policy = NewPolicy();
        policy.NewDraft("a: 1", Author);

        policy.NewDraft("a: 2", Author).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Publish_makes_the_draft_current_and_clears_the_draft_pointer()
    {
        var policy = NewPolicy();

        var v1 = PublishNext(policy, null, "a: 1");

        policy.CurrentVersionId.Should().Be(v1.Id);
        policy.DraftVersionId.Should().BeNull();
        v1.State.Should().Be(PolicyState.Published);
    }

    [Fact]
    public void Publishing_a_newer_version_archives_the_old_one()
    {
        var policy = NewPolicy();
        var v1 = PublishNext(policy, null, "a: 1");

        var v2 = PublishNext(policy, v1, "a: 2");

        v1.State.Should().Be(PolicyState.Archived);
        v2.State.Should().Be(PolicyState.Published);
        policy.CurrentVersionId.Should().Be(v2.Id);
    }

    [Fact]
    public void Publish_by_the_submitter_changes_nothing()
    {
        var policy = NewPolicy();
        var draft = policy.NewDraft("a: 1", Author).Value;
        draft.Submit(Author, Summary, Now);

        var result = policy.Publish(draft, null, Author, Now);

        result.Error.Code.Should().Be("policy.self_approval");
        policy.CurrentVersionId.Should().BeNull();
        policy.DraftVersionId.Should().Be(draft.Id);
    }

    [Fact]
    public void Publish_refuses_a_version_that_is_not_this_policys_draft()
    {
        var policy = NewPolicy();
        var other = NewPolicy();
        var foreign = other.NewDraft("a: 1", Author).Value;
        foreign.Submit(Author, Summary, Now);

        policy.Publish(foreign, null, Approver, Now).Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public void Reject_frees_the_policy_for_a_new_draft()
    {
        var policy = NewPolicy();
        var draft = policy.NewDraft("a: 1", Author).Value;
        draft.Submit(Author, Summary, Now);

        policy.Reject(draft, "SSN must be BLOCK").IsSuccess.Should().BeTrue();

        policy.DraftVersionId.Should().BeNull();
        policy.NewDraft("a: 2", Author).Value.VersionNo.Should().Be(2);
    }

    [Fact]
    public void Rollback_publishes_the_old_text_as_a_new_version()
    {
        var policy = NewPolicy();
        var v1 = PublishNext(policy, null, "a: 1");
        var v2 = PublishNext(policy, v1, "a: 2");

        var v3 = policy.Rollback(v1, v2, Approver, Now).Value;

        v3.VersionNo.Should().Be(3);
        v3.Definition.Should().Be("a: 1");
        v3.State.Should().Be(PolicyState.Published);
        v3.ChangeNote.Should().Be("Rollback to version 1");
        v2.State.Should().Be(PolicyState.Archived);
        policy.CurrentVersionId.Should().Be(v3.Id);
    }

    [Fact]
    public void Rollback_to_the_current_version_is_refused()
    {
        var policy = NewPolicy();
        var v1 = PublishNext(policy, null, "a: 1");

        policy.Rollback(v1, v1, Approver, Now).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void An_archived_policy_takes_no_new_drafts()
    {
        var policy = NewPolicy();
        policy.Archive(Now).IsSuccess.Should().BeTrue();

        policy.NewDraft("a: 1", Author).IsFailure.Should().BeTrue();
    }
}