using System.Text;

namespace VeilGate.Application.Policies;

/// <summary>A line-by-line comparison in the unified diff format (one hunk covering both texts, so the reader sees the whole policy).</summary>
public static class UnifiedDiff
{
    /// <summary>The comparison needs a table of lines x lines, so very long texts are refused rather than allowed to use a lot of memory.</summary>
    public const int MaxLines = 2000;

    /// <summary>Null when either text has more than <see cref="MaxLines"/> lines.</summary>
    public static string? Create(string fromName, string toName, string from, string to)
    {
        var a = Lines(from);
        var b = Lines(to);
        if (a.Length > MaxLines || b.Length > MaxLines) return null;

        // lcs[i, j] = length of the longest common run of lines in a[i..] and b[j..]
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var sb = new StringBuilder();
        sb.Append("--- ").Append(fromName).Append('\n');
        sb.Append("+++ ").Append(toName).Append('\n');
        sb.Append($"@@ -1,{a.Length} +1,{b.Length} @@\n");

        int p = 0, q = 0;
        while (p < a.Length && q < b.Length)
        {
            if (a[p] == b[q]) { sb.Append(' ').Append(a[p]).Append('\n'); p++; q++; }
            else if (lcs[p + 1, q] >= lcs[p, q + 1]) { sb.Append('-').Append(a[p]).Append('\n'); p++; }
            else { sb.Append('+').Append(b[q]).Append('\n'); q++; }
        }
        for (; p < a.Length; p++) sb.Append('-').Append(a[p]).Append('\n');
        for (; q < b.Length; q++) sb.Append('+').Append(b[q]).Append('\n');
        return sb.ToString();
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}
