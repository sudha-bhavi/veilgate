using VeilGate.Domain.Detection;
using VeilGate.Domain.Policies;

namespace VeilGate.Application.Policies;

public sealed record DecidedEntity(DetectedEntity Entity, PolicyAction Action, int MaxDays);

public sealed record PolicyDecision(IReadOnlyList<DecidedEntity> Items)
{
    public bool IsBlocked => Items.Any(i => i.Action == PolicyAction.Block);

    public IReadOnlyList<string> BlockedTypes => Items
        .Where(i => i.Action == PolicyAction.Block)
        .Select(i => i.Entity.Type)
        .Distinct()
        .Order()
        .ToList();
}

public interface IPolicyEngine
{
    PolicyDecision Decide(IReadOnlyList<DetectedEntity> entities, PolicyDocument policy);
}

public sealed class PolicyEngine : IPolicyEngine
{
    public PolicyDecision Decide(IReadOnlyList<DetectedEntity> entities, PolicyDocument policy)
    {
        var items = entities
            .Where(e => e.Confidence >= policy.MinConfidence)
            .Select(e =>
            {
                var rule = policy.RuleFor(e.Type);
                return new DecidedEntity(e, rule.Action, rule.MaxDays);
            })
            .ToList();

        return new PolicyDecision(items);
    }
}