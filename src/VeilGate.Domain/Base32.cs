using System.Text;

namespace VeilGate.Domain;

public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // Bytes -> text. Every 5 bytes (40 bits) become 8 characters (5 bits each).
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var result = new StringBuilder();
        int buffer = 0;   // bits we've read but not yet written out
        int bits = 0;     // how many bits are waiting in buffer

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                result.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
            buffer &= (1 << bits) - 1;   // forget the bits we already used
        }

        if (bits > 0)   // leftover bits: pad with zeros on the right
            result.Append(Alphabet[(buffer << (5 - bits)) & 31]);

        return result.ToString();
    }

    // Text -> bytes. Ignores case, spaces, dashes and '=' padding,
    // because users and apps format these differently.
    public static byte[] Decode(string text)
    {
        var output = new List<byte>();
        int buffer = 0;
        int bits = 0;

        foreach (var c in text)
        {
            if (c is '=' or ' ' or '-') continue;

            int value = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (value < 0)
                throw new FormatException($"'{c}' is not a valid Base32 character.");

            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
                buffer &= (1 << bits) - 1;
            }
        }
        return output.ToArray();
    }
}