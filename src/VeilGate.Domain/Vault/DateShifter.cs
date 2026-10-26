using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VeilGate.Domain.Vault;

public static class DateShifter
{
    // Tried in order. The 2-digit forms come first so "03/14/2025" keeps its zero-padding when written back.
    private static readonly string[] Formats = ["MM/dd/yyyy", "M/d/yyyy", "MM/dd/yy", "M/d/yy", "yyyy-MM-dd"];

    /// <summary>
    /// A stable offset in days, from -maxDays to +maxDays, never zero (a zero shift would leak the real date).
    /// The same key and seed always give the same offset; without the key nobody can work it out.
    /// </summary>
    public static int OffsetDays(byte[] key, string seed, int maxDays)
    {
        maxDays = Math.Clamp(maxDays, 1, 365);
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(seed));
        var n = (int)(BitConverter.ToUInt32(hash, 0) % (uint)(2 * maxDays));   // 0 .. 2*maxDays-1
        return n < maxDays ? n - maxDays : n - maxDays + 1;                    // -max..-1  and  1..max
    }

    /// <summary>Moves a date written in one of the known formats and writes it back in the same format. Returns null if it cannot.</summary>
    public static string? Shift(string dateText, int days)
    {
        foreach (var format in Formats)
        {
            if (!DateTime.TryParseExact(dateText, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            try { return date.AddDays(days).ToString(format, CultureInfo.InvariantCulture); }
            catch (ArgumentOutOfRangeException) { return null; }   // shifted past year 1 or 9999
        }
        return null;
    }
}