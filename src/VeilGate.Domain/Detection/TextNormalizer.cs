using System.Globalization;
using System.Text;

namespace VeilGate.Domain.Detection;

public static class TextNormalizer
{
    /// <summary>
    /// First step of the gateway pipeline (Backend doc 6.1). NFKC turns look-alike characters (full-width digits, ligatures) into plain ones
    /// and zero-width or control characters are removed, so "5​5​5" with hidden characters cannot slip past a detector.
    /// Newlines and tabs are kept. Detection and transformation both run on this normalized text.
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormKC))
        {
            if (ch is '\n' or '\r' or '\t') { sb.Append(ch); continue; }
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format) continue;   // Format covers zero-width space/joiners, BOM
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
