using System.Text.RegularExpressions;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Vault;

namespace VeilGate.Application.Transformation;

public sealed record RestoreResult(string Text, int Replaced, IReadOnlyList<string> Unresolved);

public interface IRehydrator
{
    Task<RestoreResult> RestoreAsync(string text, VaultSession session, CancellationToken ct = default);
}

/// <summary>
/// Puts the original values back where tokens appear, e.g. in the LLM's answer. Whether a caller may do this at all
/// (policy reidentify.enabled and the gateway:reidentify scope) is decided by the gateway before it calls this.
/// Streaming restoration, which must handle tokens split across chunks, comes with the SSE endpoint in M3.
/// </summary>
public sealed class Rehydrator(IVaultService vault) : IRehydrator
{
    private static readonly Regex TokenPattern = new(@"\[\[[A-Za-z0-9_]+_[A-Z2-7]{4}\]\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Restores from tokens already in memory (a session this request just created), with no database read.</summary>
    public static RestoreResult RestoreFrom(string text, IReadOnlyDictionary<string, string> known)
    {
        var replaced = 0;
        var unresolved = new List<string>();
        var restored = TokenPattern.Replace(text, m =>
        {
            if (!known.TryGetValue(m.Value, out var original))
            {
                if (!unresolved.Contains(m.Value)) unresolved.Add(m.Value);
                return m.Value;
            }
            replaced++;
            return original;
        });
        return new RestoreResult(restored, replaced, unresolved);
    }

    public async Task<RestoreResult> RestoreAsync(string text, VaultSession session, CancellationToken ct = default)
    {
        var tokens = TokenPattern.Matches(text).Select(m => m.Value).Distinct().ToList();
        if (tokens.Count == 0) return new RestoreResult(text, 0, []);

        var resolved = new Dictionary<string, string>();
        var unresolved = new List<string>();
        foreach (var token in tokens)
        {
            var value = await vault.ResolveAsync(session, token, ct);
            if (value is null) unresolved.Add(token);
            else resolved[token] = value;
        }

        var replaced = 0;
        var restored = TokenPattern.Replace(text, m =>
        {
            if (!resolved.TryGetValue(m.Value, out var original)) return m.Value;   // unknown token: leave it as it is
            replaced++;
            return original;
        });
        return new RestoreResult(restored, replaced, unresolved);
    }
}
