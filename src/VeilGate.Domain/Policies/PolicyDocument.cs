namespace VeilGate.Domain.Policies;

public enum PolicyAction { Allow, Mask, Tokenize, Redact, Shift, Block }

/// <summary>What to do with one entity type. MaxDays only matters for SHIFT.</summary>
public sealed record EntityRule(PolicyAction Action, int MaxDays = 365);

public sealed record PolicyDocument
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Applied to any entity type the policy doesn't list. REDACT is the safest default.</summary>
    public PolicyAction DefaultAction { get; init; } = PolicyAction.Redact;

    public double MinConfidence { get; init; } = 0.5;

    public IReadOnlyDictionary<string, EntityRule> Entities { get; init; } = new Dictionary<string, EntityRule>();

    public EntityRule RuleFor(string entityType) =>
        Entities.TryGetValue(entityType, out var rule) ? rule : new EntityRule(DefaultAction);

        public bool ReidentifyEnabled { get; init; } = false;
    public int ReidentifyTtlSeconds { get; init; } = 3600;

    /// <summary>Whether to run detection on the model's answer too (leak gate on output).</summary>
    public bool ScanOutput { get; init; } = true;
    public PolicyAction OutputOnLeak { get; init; } = PolicyAction.Redact;   // Redact or Block only

    public int MaxTextChars { get; init; } = 32768;
}