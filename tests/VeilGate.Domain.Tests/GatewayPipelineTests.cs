using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Detection;
using VeilGate.Application.Gateway;
using VeilGate.Application.Llm;
using VeilGate.Application.Policies;
using VeilGate.Application.Transformation;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Policies;
using VeilGate.Domain.Vault;
using VeilGate.Infrastructure.Detection;
using Xunit;

namespace VeilGate.Domain.Tests;

public class GatewayPipelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("0192a000-0000-7000-8000-00000000000a");
    private static readonly Guid ClientId = Guid.Parse("0192a000-0000-7000-8000-00000000000c");
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private const string Note = "Patient John Smith, MRN 4471882, phone 513-555-0142, seen 03/14/2025.";
    private static readonly string[] Secrets = ["John Smith", "4471882", "513-555-0142", "03/14/2025"];

    // ------------------------------------------------------------------ fakes

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FakeVault : IVaultService
    {
        public readonly Dictionary<Guid, VaultSession> Sessions = new();
        public Task<VaultSession> OpenAsync(Guid tenantId, Guid clientId, TimeSpan ttl, string? subjectId = null, CancellationToken ct = default) =>
            Task.FromResult(new VaultSession { TenantId = tenantId, ClientId = clientId, CreatedAt = Now, ExpiresAt = Now + ttl, DateShiftKey = Key, SubjectId = subjectId });
        public Task StoreAsync(VaultSession session, CancellationToken ct = default) { Sessions[session.Id] = session; return Task.CompletedTask; }
        public Task<VaultSession?> FindAsync(Guid sessionId, Guid clientId, CancellationToken ct = default) =>
            Task.FromResult(Sessions.TryGetValue(sessionId, out var s) && s.ClientId == clientId ? (VaultSession?)s : null);
        public Task<string?> ResolveAsync(VaultSession session, string token, CancellationToken ct = default) =>
            Task.FromResult(session.Tokens.TryGetValue(token, out var v) ? v : null);
        public Task<int> PurgeExpiredAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeLlm(Func<LlmRequest, Result<LlmResponse>> reply) : ILlmGateway
    {
        public readonly List<LlmRequest> Requests = [];
        public Task<Result<LlmResponse>> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(reply(request));
        }
    }

    private sealed class FakeAudit : IAuditWriter
    {
        public readonly List<AuditEvent> Events = [];
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken ct = default) { Events.Add(auditEvent); return Task.CompletedTask; }
    }

    private sealed class FakeSecurity : ISecurityEventWriter
    {
        public readonly List<SecurityEvent> Events = [];
        public Task WriteAsync(SecurityEvent securityEvent, CancellationToken ct = default) { Events.Add(securityEvent); return Task.CompletedTask; }
    }

    private sealed class FixedResolver(PolicyDocument document) : IPolicyResolver
    {
        public Task<Result<ResolvedPolicy>> ResolveAsync(Guid tenantId, Guid clientId, Guid? policyId, CancellationToken ct = default) =>
            Task.FromResult<Result<ResolvedPolicy>>(policyId is null ? new ResolvedPolicy(document, 7) : Error.NotFound());
    }

    /// <summary>A detector that misses everything the first time it is asked. It stands in for "a detector had a blind spot".</summary>
    private sealed class MissesFirstCall(IDetector inner) : IDetector
    {
        private int _calls;
        public string Name => "misses-first";
        public IEnumerable<DetectedEntity> Detect(string text, DetectionContext context) =>
            Interlocked.Increment(ref _calls) == 1 ? [] : inner.Detect(text, context);
    }

    private sealed class Rig
    {
        public readonly FakeVault Vault = new();
        public readonly FakeLlm Llm;
        public readonly FakeAudit Audit = new();
        public readonly FakeSecurity Security = new();
        public readonly IGatewayPipeline Pipeline;

        public Rig(Func<LlmRequest, Result<LlmResponse>>? llm = null, PolicyDocument? policy = null, IEnumerable<IDetector>? detectors = null)
        {
            Llm = new FakeLlm(llm ?? (_ => Reply("Mock reply")));
            var detection = new DetectionPipeline(detectors ?? [new RegexDetector(), new NameHeuristicDetector()]);
            Pipeline = new GatewayPipeline(
                detection, new PolicyEngine(), new Transformer(), new OutboundLeakGate(detection), Vault, new Rehydrator(Vault), Llm,
                new FixedResolver(policy ?? PolicyTemplates.Get("hipaa-safe-harbor")!), Audit, Security, new FixedClock(),
                NullLogger<GatewayPipeline>.Instance);
        }
    }

    private static Result<LlmResponse> Reply(string content) =>
        Result.Success(new LlmResponse("chatcmpl-1", "gpt-4o-mini", content, "stop", 5, 5, "mock-llm"));

    private static GatewayCaller Caller(bool canReidentify = true) => new(TenantId, ClientId, canReidentify, "203.0.113.9");

    private static ChatInput Chat(params string[] userMessages) =>
        new("gpt-4o-mini", userMessages.Select(m => new LlmMessage("user", m)).ToList(), 0.2, 256);

    private static PolicyDocument Policy(string yaml) => PolicyParser.Parse(yaml).Policy!;

    // ------------------------------------------------------------------- chat

    [Fact]
    public async Task Chat_sends_only_sanitized_text_to_the_model()
    {
        var rig = new Rig();

        var result = await rig.Pipeline.ChatAsync(Caller(), Chat(Note), null);

        result.IsSuccess.Should().BeTrue();
        var sent = rig.Llm.Requests.Should().ContainSingle().Subject.Messages[0].Content;
        foreach (var secret in Secrets) sent.Should().NotContain(secret);
        sent.Should().Contain("[PHONE]").And.Contain("[REDACTED]").And.MatchRegex(@"\[\[PERSON_[A-Z2-7]{4}\]\]");
        result.Value.PolicyVersion.Should().Be(7);
        result.Value.EntitiesProtected.Should().BeEquivalentTo(new Dictionary<string, int> { ["PERSON"] = 1, ["MRN"] = 1, ["PHONE"] = 1, ["DATE"] = 1 });
    }

    [Fact]
    public async Task Chat_restores_the_original_names_in_the_answer_when_the_caller_may()
    {
        var rig = new Rig(r => Reply($"Summary for {Regex.Match(r.Messages[0].Content, @"\[\[PERSON_[A-Z2-7]{4}\]\]").Value}."));

        var result = await rig.Pipeline.ChatAsync(Caller(canReidentify: true), Chat(Note), null);

        result.Value.Content.Should().Be("Summary for John Smith.");
        result.Value.Restored.Should().BeTrue();
    }

    [Fact]
    public async Task Chat_keeps_the_tokens_when_the_caller_lacks_the_reidentify_scope()
    {
        var rig = new Rig(r => Reply($"Summary for {Regex.Match(r.Messages[0].Content, @"\[\[PERSON_[A-Z2-7]{4}\]\]").Value}."));

        var result = await rig.Pipeline.ChatAsync(Caller(canReidentify: false), Chat(Note), null);

        result.Value.Content.Should().MatchRegex(@"^Summary for \[\[PERSON_[A-Z2-7]{4}\]\]\.$");
        result.Value.Restored.Should().BeFalse();
    }

    [Fact]
    public async Task The_same_person_gets_the_same_token_across_messages()
    {
        var rig = new Rig();

        await rig.Pipeline.ChatAsync(Caller(), Chat("Patient John Smith arrived.", "Patient John Smith left."), null);

        var tokens = rig.Llm.Requests.Single().Messages.Select(m => Regex.Match(m.Content, @"\[\[PERSON_[A-Z2-7]{4}\]\]").Value).ToList();
        tokens[0].Should().NotBeEmpty().And.Be(tokens[1]);
    }

    [Fact]
    public async Task A_blocked_entity_stops_the_request_before_the_model_is_called()
    {
        var rig = new Rig(policy: Policy("""
            version: 1
            name: strict
            defaults: { action: REDACT }
            entities:
              SSN: { action: BLOCK }
            """));

        var result = await rig.Pipeline.ChatAsync(Caller(), Chat("SSN 123-45-6789 for the patient"), null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("policy.blocked");
        result.Error.Kind.Should().Be(ErrorKind.Unprocessable);
        result.Error.Extensions!["entityTypes"].Should().BeEquivalentTo(new[] { "SSN" });
        rig.Llm.Requests.Should().BeEmpty();
        rig.Audit.Events.Should().ContainSingle().Which.Result.Should().Be("denied");
    }

    [Fact]
    public async Task If_a_detector_misses_something_the_leak_gate_stops_the_request()
    {
        var rig = new Rig(detectors: [new MissesFirstCall(new RegexDetector())]);

        var result = await rig.Pipeline.ChatAsync(Caller(), Chat("Call 513-555-0142 please"), null);

        result.Error.Code.Should().Be("leak.gate_tripped");
        rig.Llm.Requests.Should().BeEmpty("a leaking request must never reach a provider");
        var evt = rig.Security.Events.Should().ContainSingle().Subject;
        evt.Type.Should().Be("leak.gate_tripped");
        evt.Severity.Should().Be(Severity.High);
        evt.Details["types"].Should().Be("PHONE");
        evt.Details.Values.Should().NotContain(v => v.Contains("513"));
    }

    [Fact]
    public async Task Sensitive_data_in_the_models_answer_is_redacted()
    {
        var rig = new Rig(_ => Reply("Please call 513-555-0199 about it."));

        var result = await rig.Pipeline.ChatAsync(Caller(), Chat("Hello"), null);

        result.Value.Content.Should().Be("Please call [REDACTED] about it.");
        rig.Security.Events.Should().ContainSingle().Which.Type.Should().Be("output.leak");
    }

    [Fact]
    public async Task A_policy_can_withhold_a_leaking_answer_instead_of_redacting()
    {
        var rig = new Rig(_ => Reply("Please call 513-555-0199 about it."), Policy("""
            version: 1
            name: strict-output
            defaults: { action: REDACT }
            output: { scan: true, onLeak: BLOCK }
            """));

        var result = await rig.Pipeline.ChatAsync(Caller(), Chat("Hello"), null);

        result.Error.Code.Should().Be("leak.gate_tripped");
    }

    [Fact]
    public async Task Restored_originals_in_the_answer_are_not_mistaken_for_a_leak()
    {
        var rig = new Rig(r => Reply($"Seen: {Regex.Match(r.Messages[0].Content, @"\[\[PERSON_[A-Z2-7]{4}\]\]").Value}"));

        var result = await rig.Pipeline.ChatAsync(Caller(), Chat("Patient John Smith arrived."), null);

        result.Value.Content.Should().Be("Seen: John Smith");
        rig.Security.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_failure_is_reported_and_audited_as_a_failure()
    {
        var rig = new Rig(_ => Result.Failure<LlmResponse>(Error.ProviderUnavailable()));

        var result = await rig.Pipeline.ChatAsync(Caller(), Chat(Note), null);

        result.Error.Code.Should().Be("provider.unavailable");
        result.Error.Kind.Should().Be(ErrorKind.BadGateway);
        rig.Audit.Events.Should().ContainSingle().Which.Result.Should().Be("failure");
    }

    [Fact]
    public async Task The_audit_log_records_counts_and_never_the_content()
    {
        var rig = new Rig();

        await rig.Pipeline.ChatAsync(Caller(), Chat(Note), null);

        var evt = rig.Audit.Events.Should().ContainSingle().Subject;
        evt.Action.Should().Be("gateway.chat");
        evt.ActorType.Should().Be("client");
        evt.ActorId.Should().Be(ClientId);
        evt.TenantId.Should().Be(TenantId);
        evt.Details["entities"].Should().Be("DATE=1,MRN=1,PERSON=1,PHONE=1");
        evt.Details["policyVersion"].Should().Be("7");
        foreach (var secret in Secrets)
            evt.Details.Values.Should().NotContain(v => v.Contains(secret));
    }

    [Fact]
    public async Task A_message_over_the_policy_limit_is_rejected()
    {
        var rig = new Rig(policy: Policy("""
            version: 1
            name: small
            defaults: { action: REDACT }
            limits: { maxTextChars: 20 }
            """));

        var result = await rig.Pipeline.ChatAsync(Caller(), Chat(new string('a', 21)), null);

        result.Error.Code.Should().Be("validation.failed");
        rig.Llm.Requests.Should().BeEmpty();
    }

    // ------------------------------------------------------ deidentify / reidentify

    [Fact]
    public async Task Deidentify_returns_safe_text_entity_positions_and_a_vault_session()
    {
        var rig = new Rig();

        var result = await rig.Pipeline.DeidentifyAsync(Caller(), Note, null, "patient-881", null);

        result.IsSuccess.Should().BeTrue();
        foreach (var secret in Secrets) result.Value.Text.Should().NotContain(secret);
        result.Value.Entities.Select(e => e.Type).Should().BeEquivalentTo("PERSON", "MRN", "PHONE", "DATE");
        result.Value.Entities.Single(e => e.Type == "PERSON").Token.Should().MatchRegex(@"^\[\[PERSON_[A-Z2-7]{4}\]\]$");
        result.Value.Entities.Single(e => e.Type == "PERSON").Action.Should().Be(PolicyAction.Tokenize);
        rig.Vault.Sessions.Should().ContainKey(result.Value.VaultSessionId);
        result.Value.ExpiresAt.Should().Be(Now.AddSeconds(3600));
    }

    [Fact]
    public async Task Deidentify_then_reidentify_restores_the_tokens()
    {
        var rig = new Rig();
        var protectedNote = await rig.Pipeline.DeidentifyAsync(Caller(), "Patient John Smith arrived.", null, null, null);
        var token = protectedNote.Value.Entities.Single().Token!;

        var restored = await rig.Pipeline.ReidentifyAsync(Caller(), protectedNote.Value.VaultSessionId, $"Please call {token} tomorrow");

        restored.Value.Text.Should().Be("Please call John Smith tomorrow");
        restored.Value.Replaced.Should().Be(1);
        restored.Value.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public async Task Reidentify_needs_the_scope()
    {
        var rig = new Rig();
        var protectedNote = await rig.Pipeline.DeidentifyAsync(Caller(), "Patient John Smith arrived.", null, null, null);

        var result = await rig.Pipeline.ReidentifyAsync(Caller(canReidentify: false), protectedNote.Value.VaultSessionId, "x");

        result.Error.Code.Should().Be("policy.reidentify_denied");
        result.Error.Kind.Should().Be(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Reidentify_is_refused_when_the_policy_does_not_allow_it()
    {
        var rig = new Rig(policy: PolicyTemplates.Get("blank"));
        var protectedNote = await rig.Pipeline.DeidentifyAsync(Caller(), "Hello", null, null, null);

        var result = await rig.Pipeline.ReidentifyAsync(Caller(), protectedNote.Value.VaultSessionId, "x");

        result.Error.Code.Should().Be("policy.reidentify_denied");
    }

    [Fact]
    public async Task Another_client_cannot_use_the_vault_session()
    {
        var rig = new Rig();
        var protectedNote = await rig.Pipeline.DeidentifyAsync(Caller(), "Patient John Smith arrived.", null, null, null);
        var stranger = new GatewayCaller(TenantId, Guid.NewGuid(), true);

        var result = await rig.Pipeline.ReidentifyAsync(stranger, protectedNote.Value.VaultSessionId, "x");

        result.Error.Code.Should().Be("vault.session_not_found");
    }

    [Fact]
    public async Task An_unknown_policy_id_is_not_found()
    {
        var rig = new Rig();

        var result = await rig.Pipeline.DeidentifyAsync(Caller(), Note, Guid.NewGuid(), null, null);

        result.Error.Code.Should().Be("resource.not_found");
    }

    // ----------------------------------------------------------------- detect

    [Fact]
    public async Task Detect_reports_positions_and_types_but_no_text()
    {
        var rig = new Rig();

        var result = await rig.Pipeline.DetectAsync(Caller(), "Call 513-555-0142", 0.5);

        var e = result.Value.Entities.Should().ContainSingle().Subject;
        (e.Type, e.Start, e.End).Should().Be(("PHONE", 5, 17));
        rig.Audit.Events.Should().ContainSingle().Which.Action.Should().Be("gateway.detect");
    }

    [Fact]
    public async Task Detect_honours_the_minimum_confidence()
    {
        var rig = new Rig();

        var result = await rig.Pipeline.DetectAsync(Caller(), "Call 513-555-0142", 0.99);

        result.Value.Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Zero_width_characters_cannot_hide_a_phone_number()
    {
        var rig = new Rig();

        var result = await rig.Pipeline.DeidentifyAsync(Caller(), "Call 513​-555​-0142 now", null, null, null);

        result.Value.Text.Should().Be("Call [PHONE] now");
    }
}
