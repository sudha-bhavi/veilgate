using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Policies;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Policies;
using Xunit;

namespace VeilGate.Domain.Tests;

/// <summary>The policy workflow handlers against a fake repository: who may do what, in which state, and what gets audited.</summary>
public class PolicyHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Author = Guid.CreateVersion7();
    private static readonly Guid Approver = Guid.CreateVersion7();
    private static readonly string ValidYaml = PolicyTemplates.YamlOf(PolicyTemplates.NamesOnly);
    private static readonly string OtherValidYaml = PolicyTemplates.YamlOf(PolicyTemplates.HipaaSafeHarbor);
    private static readonly PolicyActor AuthorActor = new(Author, "203.0.113.9");
    private static readonly PolicyActor ApproverActor = new(Approver, "203.0.113.10");

    private readonly IPolicyRepository _repo = Substitute.For<IPolicyRepository>();
    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly PolicyAuditor _auditor;

    public PolicyHandlerTests()
    {
        _tenant.TenantId.Returns(Tenant);
        _clock.UtcNow.Returns(Now);
        _auditor = new PolicyAuditor(_audit, _tenant, _clock, NullLogger<PolicyAuditor>.Instance);
    }

    private void Wire(Policy policy, params PolicyVersion[] versions)
    {
        _repo.FindAsync(policy.Id, Arg.Any<CancellationToken>()).Returns(policy);
        foreach (var v in versions) _repo.FindVersionAsync(v.Id, Arg.Any<CancellationToken>()).Returns(v);
    }

    private (Policy policy, PolicyVersion draft) WithDraft(string? yaml = null)
    {
        var policy = Policy.Create(Tenant, "Default", null).Value;
        var draft = policy.NewDraft(yaml ?? ValidYaml, Author).Value;
        Wire(policy, draft);
        return (policy, draft);
    }

    private (Policy policy, PolicyVersion draft) WithPending(string? yaml = null)
    {
        var (policy, draft) = WithDraft(yaml);
        draft.Submit(Author, "Ready for review", Now).IsSuccess.Should().BeTrue();
        return (policy, draft);
    }

    private (Policy policy, PolicyVersion published) WithPublished(string? yaml = null)
    {
        var (policy, draft) = WithPending(yaml);
        policy.Publish(draft, null, Approver, Now).IsSuccess.Should().BeTrue();
        return (policy, draft);
    }

    private void AuditShouldHaveRecorded(string action, string result) =>
        _audit.Received(1).WriteAsync(
            Arg.Is<AuditEvent>(e => e.Action == action && e.Result == result && e.TenantId == Tenant && e.ResourceType == "policy"),
            Arg.Any<CancellationToken>());

    // ------------------------------------------------------------ create

    [Fact]
    public async Task Create_saves_a_policy_with_a_draft_copied_from_the_template_and_audits_it()
    {
        var handler = new CreatePolicyHandler(_repo, _tenant, _auditor);

        var result = await handler.HandleAsync(new CreatePolicyCommand("Default", "blank", "first", AuthorActor));

        result.IsSuccess.Should().BeTrue();
        _repo.Received(1).AddPolicy(Arg.Is<Policy>(p => p.Name == "Default" && p.TenantId == Tenant));
        _repo.Received(1).AddVersion(Arg.Is<PolicyVersion>(v => v.Definition == PolicyTemplates.YamlOf("blank") && v.State == PolicyState.Draft));
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        AuditShouldHaveRecorded("policy.create", "success");
    }

    [Fact]
    public async Task Create_refuses_an_unknown_template_and_saves_nothing()
    {
        var handler = new CreatePolicyHandler(_repo, _tenant, _auditor);

        var result = await handler.HandleAsync(new CreatePolicyCommand("Default", "nope", null, AuthorActor));

        result.Error.Code.Should().Be("validation.failed");
        await _repo.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Create_refuses_a_name_that_is_taken()
    {
        _repo.NameExistsAsync("Default", Arg.Any<CancellationToken>()).Returns(true);
        var handler = new CreatePolicyHandler(_repo, _tenant, _auditor);

        var result = await handler.HandleAsync(new CreatePolicyCommand("Default", "blank", null, AuthorActor));

        result.Error.Code.Should().Be("resource.conflict");
        await _repo.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    // ------------------------------------------------------------ save draft

    [Fact]
    public async Task Saving_with_the_current_etag_updates_the_draft_and_returns_a_new_etag()
    {
        var (policy, draft) = WithDraft();
        var etag = PolicyEtag.Of(draft);

        var result = await new SaveDraftHandler(_repo).HandleAsync(new SaveDraftCommand(policy.Id, "a: 2", etag, Author));

        result.IsSuccess.Should().BeTrue();
        draft.Definition.Should().Be("a: 2");
        result.Value.Etag.Should().Be(PolicyEtag.Of(draft)).And.NotBe(etag);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("\"stale\"")]
    [InlineData(null)]
    public async Task Saving_with_a_stale_or_missing_etag_is_refused_and_changes_nothing(string? ifMatch)
    {
        var (policy, draft) = WithDraft();
        var before = draft.Definition;

        var result = await new SaveDraftHandler(_repo).HandleAsync(new SaveDraftCommand(policy.Id, "a: 2", ifMatch, Author));

        result.Error.Code.Should().Be("precondition.failed");
        draft.Definition.Should().Be(before);
        await _repo.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task A_submitted_draft_cannot_be_overwritten()
    {
        var (policy, draft) = WithPending();
        var before = draft.Definition;

        var result = await new SaveDraftHandler(_repo).HandleAsync(new SaveDraftCommand(policy.Id, "a: 2", PolicyEtag.Of(draft), Author));

        result.Error.Code.Should().Be("resource.conflict");
        draft.Definition.Should().Be(before);
    }

    [Fact]
    public async Task Saving_a_published_policy_without_a_draft_starts_the_next_version()
    {
        var (policy, published) = WithPublished();

        var result = await new SaveDraftHandler(_repo).HandleAsync(new SaveDraftCommand(policy.Id, "a: 2", PolicyEtag.Of(published), Author));

        result.Value.VersionNo.Should().Be(2);
        policy.DraftVersionId.Should().Be(result.Value.VersionId);
        _repo.Received(1).AddVersion(Arg.Is<PolicyVersion>(v => v.VersionNo == 2 && v.Definition == "a: 2"));
    }

    [Fact]
    public async Task Saving_text_that_is_too_long_is_refused()
    {
        var (policy, draft) = WithDraft();

        var result = await new SaveDraftHandler(_repo).HandleAsync(
            new SaveDraftCommand(policy.Id, new string('x', PolicyParser.MaxYamlChars + 1), PolicyEtag.Of(draft), Author));

        result.Error.Code.Should().Be("validation.failed");
    }

    [Fact]
    public async Task Saving_an_unknown_policy_is_not_found()
    {
        var result = await new SaveDraftHandler(_repo).HandleAsync(new SaveDraftCommand(Guid.NewGuid(), "a: 1", "\"x\"", Author));

        result.Error.Code.Should().Be("resource.not_found");
    }

    [Fact]
    public async Task An_archived_policy_cannot_be_edited()
    {
        var (policy, draft) = WithDraft();
        policy.Archive(Now);

        var result = await new SaveDraftHandler(_repo).HandleAsync(new SaveDraftCommand(policy.Id, "a: 2", PolicyEtag.Of(draft), Author));

        result.Error.Code.Should().Be("resource.conflict");
    }

    // ------------------------------------------------------------ validate

    [Fact]
    public async Task Validate_accepts_a_good_draft()
    {
        var (policy, _) = WithDraft();

        var result = await new ValidatePolicyHandler(_repo).HandleAsync(policy.Id);

        result.Value.Valid.Should().BeTrue();
        result.Value.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_reports_problems_in_a_bad_draft()
    {
        var (policy, _) = WithDraft("version: 1\nname: x\nbogus: 1\n");

        var result = await new ValidatePolicyHandler(_repo).HandleAsync(policy.Id);

        result.Value.Valid.Should().BeFalse();
        result.Value.Errors.Should().NotBeEmpty();
    }

    // ------------------------------------------------------------ submit

    [Fact]
    public async Task Submit_moves_a_valid_draft_to_pending_and_audits_it()
    {
        var (policy, draft) = WithDraft();

        var result = await new SubmitPolicyHandler(_repo, _auditor, _clock)
            .HandleAsync(new SubmitPolicyCommand(policy.Id, "Tighten SSN handling", AuthorActor));

        result.IsSuccess.Should().BeTrue();
        draft.State.Should().Be(PolicyState.PendingApproval);
        draft.SubmittedBy.Should().Be(Author);
        AuditShouldHaveRecorded("policy.submit", "success");
    }

    [Fact]
    public async Task Submit_refuses_text_that_does_not_validate()
    {
        var (policy, draft) = WithDraft("just some words");

        var result = await new SubmitPolicyHandler(_repo, _auditor, _clock)
            .HandleAsync(new SubmitPolicyCommand(policy.Id, "Tighten SSN handling", AuthorActor));

        result.Error.Code.Should().Be("policy.invalid_definition");
        draft.State.Should().Be(PolicyState.Draft);
        await _repo.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Submit_needs_a_summary()
    {
        var (policy, draft) = WithDraft();

        var result = await new SubmitPolicyHandler(_repo, _auditor, _clock)
            .HandleAsync(new SubmitPolicyCommand(policy.Id, "short", AuthorActor));

        result.IsFailure.Should().BeTrue();
        draft.State.Should().Be(PolicyState.Draft);
    }

    // ------------------------------------------------------------ publish

    [Fact]
    public async Task Publish_by_another_person_makes_the_version_current_and_audits_it()
    {
        var (policy, draft) = WithPending();

        var result = await new PublishPolicyHandler(_repo, _auditor, _clock).HandleAsync(new PublishPolicyCommand(policy.Id, ApproverActor));

        result.Value.Should().Be(1);
        policy.CurrentVersionId.Should().Be(draft.Id);
        draft.State.Should().Be(PolicyState.Published);
        draft.ApprovedBy.Should().Be(Approver);
        AuditShouldHaveRecorded("policy.publish", "success");
    }

    [Fact]
    public async Task Publish_by_the_submitter_is_refused_and_the_attempt_is_audited_as_denied()
    {
        var (policy, draft) = WithPending();

        var result = await new PublishPolicyHandler(_repo, _auditor, _clock).HandleAsync(new PublishPolicyCommand(policy.Id, AuthorActor));

        result.Error.Code.Should().Be("policy.self_approval");
        draft.State.Should().Be(PolicyState.PendingApproval);
        policy.CurrentVersionId.Should().BeNull();
        await _repo.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
        AuditShouldHaveRecorded("policy.publish", "denied");
    }

    [Fact]
    public async Task Publish_needs_a_submitted_draft()
    {
        var (policy, _) = WithDraft();

        var result = await new PublishPolicyHandler(_repo, _auditor, _clock).HandleAsync(new PublishPolicyCommand(policy.Id, ApproverActor));

        result.Error.Code.Should().Be("policy.not_pending");
    }

    [Fact]
    public async Task Publishing_a_newer_version_archives_the_old_one()
    {
        var (policy, v1) = WithPublished();
        var v2 = policy.NewDraft(OtherValidYaml, Author).Value;
        Wire(policy, v1, v2);
        v2.Submit(Author, "Second version", Now);

        var result = await new PublishPolicyHandler(_repo, _auditor, _clock).HandleAsync(new PublishPolicyCommand(policy.Id, ApproverActor));

        result.Value.Should().Be(2);
        v1.State.Should().Be(PolicyState.Archived);
        policy.CurrentVersionId.Should().Be(v2.Id);
    }

    // ------------------------------------------------------------ reject

    [Fact]
    public async Task Reject_ends_the_review_and_audits_it()
    {
        var (policy, draft) = WithPending();

        var result = await new RejectPolicyHandler(_repo, _auditor).HandleAsync(new RejectPolicyCommand(policy.Id, "SSN must be BLOCK", ApproverActor));

        result.IsSuccess.Should().BeTrue();
        draft.State.Should().Be(PolicyState.Rejected);
        policy.DraftVersionId.Should().BeNull();
        AuditShouldHaveRecorded("policy.reject", "success");
    }

    [Fact]
    public async Task Reject_needs_a_submitted_draft()
    {
        var (policy, _) = WithDraft();

        var result = await new RejectPolicyHandler(_repo, _auditor).HandleAsync(new RejectPolicyCommand(policy.Id, "Because", ApproverActor));

        result.Error.Code.Should().Be("policy.not_pending");
    }

    // ------------------------------------------------------------ rollback

    [Fact]
    public async Task Rollback_publishes_an_old_version_as_a_new_one()
    {
        var (policy, v1) = WithPublished(ValidYaml);
        var v2 = policy.NewDraft(OtherValidYaml, Author).Value;
        Wire(policy, v1, v2);
        v2.Submit(Author, "Second version", Now);
        policy.Publish(v2, v1, Approver, Now).IsSuccess.Should().BeTrue();

        var result = await new RollbackPolicyHandler(_repo, _auditor, _clock)
            .HandleAsync(new RollbackPolicyCommand(policy.Id, v1.Id, ApproverActor));

        result.Value.Should().Be(3);
        v2.State.Should().Be(PolicyState.Archived);
        _repo.Received(1).AddVersion(Arg.Is<PolicyVersion>(v => v.VersionNo == 3 && v.Definition == ValidYaml && v.State == PolicyState.Published));
        AuditShouldHaveRecorded("policy.rollback", "success");
    }

    [Fact]
    public async Task Rollback_to_a_version_of_another_policy_is_not_found()
    {
        var (policy, _) = WithPublished();
        var (_, foreign) = WithDraft();

        var result = await new RollbackPolicyHandler(_repo, _auditor, _clock)
            .HandleAsync(new RollbackPolicyCommand(policy.Id, foreign.Id, ApproverActor));

        result.Error.Code.Should().Be("resource.not_found");
    }

    [Fact]
    public async Task Rollback_to_the_version_that_is_already_live_is_refused()
    {
        var (policy, v1) = WithPublished();

        var result = await new RollbackPolicyHandler(_repo, _auditor, _clock)
            .HandleAsync(new RollbackPolicyCommand(policy.Id, v1.Id, ApproverActor));

        result.IsFailure.Should().BeTrue();
        await _repo.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    // ------------------------------------------------------------ archive, list, diff

    [Fact]
    public async Task Archive_marks_the_policy_and_audits_it()
    {
        var (policy, _) = WithDraft();

        var result = await new ArchivePolicyHandler(_repo, _auditor, _clock).HandleAsync(new ArchivePolicyCommand(policy.Id, AuthorActor));

        result.IsSuccess.Should().BeTrue();
        policy.IsArchived.Should().BeTrue();
        AuditShouldHaveRecorded("policy.archive", "success");
    }

    [Fact]
    public async Task List_refuses_an_unknown_status()
    {
        var result = await new ListPoliciesHandler(_repo).HandleAsync("Bogus", null, 1, 25);

        result.Error.Code.Should().Be("validation.failed");
    }

    [Fact]
    public async Task List_keeps_the_page_size_within_limits()
    {
        _repo.ListAsync(null, null, 1, 100, Arg.Any<CancellationToken>()).Returns(new PolicyPage([], 0));

        var result = await new ListPoliciesHandler(_repo).HandleAsync(null, null, 0, 5000);

        result.Value.Page.Should().Be(1);
        result.Value.PageSize.Should().Be(100);
    }

    [Fact]
    public async Task Diff_of_a_missing_version_number_is_not_found()
    {
        var (policy, _) = WithDraft();

        var result = await new DiffPolicyHandler(_repo).HandleAsync(policy.Id, 1, 9);

        result.Error.Code.Should().Be("resource.not_found");
    }
}
