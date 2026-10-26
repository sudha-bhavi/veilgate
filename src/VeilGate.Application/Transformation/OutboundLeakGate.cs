using VeilGate.Application.Detection;
using VeilGate.Domain.Policies;

namespace VeilGate.Application.Transformation;

public sealed record LeakGateResult(bool Tripped, IReadOnlyList<string> LeakedTypes);

public interface IOutboundLeakGate
{
    Task<LeakGateResult> CheckAsync(TransformResult transformed, PolicyDocument policy, DetectionContext context, CancellationToken ct = default);
}

public sealed class OutboundLeakGate(IDetectionPipeline pipeline) : IOutboundLeakGate
{
    public async Task<LeakGateResult> CheckAsync(TransformResult transformed, PolicyDocument policy, DetectionContext context, CancellationToken ct = default)
    {
        var found = await pipeline.DetectAsync(transformed.Text, context, ct);

        // Text we wrote ourselves on purpose: shifted dates and values the policy let through.
        var intentional = transformed.Entities
            .Where(e => e.Action is PolicyAction.Shift or PolicyAction.Allow)
            .ToList();

        var leaked = found
            .Where(e => e.Confidence >= policy.MinConfidence)                               // the policy ignores weak findings
            .Where(e => policy.RuleFor(e.Type).Action != PolicyAction.Allow)                // the policy allows this type
            .Where(e => !intentional.Any(s => e.Start >= s.OutStart && e.End <= s.OutEnd))  // not inside a shifted/allowed span
            .Select(e => e.Type)
            .Distinct()
            .Order()
            .ToList();

        return new LeakGateResult(leaked.Count > 0, leaked);
    }
}