using System.Text;
using VeilGate.Application.Policies;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Policies;
using VeilGate.Domain.Vault;

namespace VeilGate.Application.Transformation;

/// <summary>One protected value. Start/End are positions in the input; OutStart/OutEnd are positions in the sanitized output.</summary>
public sealed record TransformedEntity(string Type, int Start, int End, PolicyAction Action, string? Token, int OutStart, int OutEnd);

public sealed record TransformResult(string Text, IReadOnlyList<TransformedEntity> Entities);

public interface ITransformer
{
    TransformResult Apply(string text, PolicyDecision decision, VaultSession session);
}

public sealed class Transformer : ITransformer
{
    public TransformResult Apply(string text, PolicyDecision decision, VaultSession session)
    {
        var output = new StringBuilder(text.Length);
        var done = new List<TransformedEntity>();
        var cursor = 0;   // how much of the input we have already copied

        foreach (var item in decision.Items.OrderBy(i => i.Entity.Start))
        {
            var e = item.Entity;
            if (e.Start < cursor) continue;   // overlap should already be merged away; never replace the same text twice

            output.Append(text, cursor, e.Start - cursor);   // copy the untouched text before this finding
            var original = text[e.Start..e.End];
            var outStart = output.Length;

            var applied = item.Action;
            string? token = null;
            string replacement;
            switch (item.Action)
            {
                case PolicyAction.Allow:
                    replacement = original;
                    break;
                case PolicyAction.Mask:
                    replacement = $"[{EntityTypes.Label(e.Type)}]";
                    break;
                case PolicyAction.Redact:
                    replacement = "[REDACTED]";
                    break;
                case PolicyAction.Tokenize:
                    token = session.TokenFor(e.Type, original);
                    replacement = token;
                    break;
                                case PolicyAction.Shift:
                    var shifted = session.DateShiftKey.Length == 0
                        ? null
                        : DateShifter.Shift(original, DateShifter.OffsetDays(session.DateShiftKey, session.DateShiftSeed, item.MaxDays));
                    if (shifted is null)
                    {
                        // No key, or a date format we don't understand: hide it rather than leak it.
                        applied = PolicyAction.Redact;
                        replacement = "[REDACTED]";
                    }
                    else replacement = shifted;
                    break;
                default:   // Block must have stopped the request before we get here
                    throw new InvalidOperationException($"Action {item.Action} cannot be applied to text.");
            }

            output.Append(replacement);
            done.Add(new TransformedEntity(e.Type, e.Start, e.End, applied, token, outStart, output.Length));
            cursor = e.End;
        }

        output.Append(text, cursor, text.Length - cursor);   // the tail after the last finding
        return new TransformResult(output.ToString(), done);
    }
}