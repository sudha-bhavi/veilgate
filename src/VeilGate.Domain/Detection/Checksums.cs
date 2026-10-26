namespace VeilGate.Domain.Detection;

/// <summary>Validators that turn "looks like a number" into "is a valid number", which removes most false positives.</summary>
public static class Checksums
{
    /// <summary>Luhn mod-10 check used by credit cards and NPIs. Non-digits are ignored.</summary>
    public static bool Luhn(string value)
    {
        var digits = value.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length < 2) return false;
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i];
            if (doubleIt) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            doubleIt = !doubleIt;
        }
        return sum % 10 == 0;
    }

    /// <summary>A card number is 13 to 19 digits and passes Luhn.</summary>
    public static bool CreditCard(string value)
    {
        var count = value.Count(char.IsAsciiDigit);
        return count is >= 13 and <= 19 && Luhn(value);
    }

    /// <summary>An NPI is 10 digits starting with 1 or 2; its Luhn check is computed with the prefix 80840 added.</summary>
    public static bool Npi(string value)
    {
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 10 && digits[0] is '1' or '2' && Luhn("80840" + digits);
    }

    /// <summary>SSA rules: the area is not 000, 666 or 900-999, the group is not 00, the serial is not 0000.</summary>
    public static bool Ssn(string value)
    {
        var d = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (d.Length != 9) return false;
        var area = int.Parse(d[..3]);
        return area != 0 && area != 666 && area < 900 && d[3..5] != "00" && d[5..] != "0000";
    }

    /// <summary>DEA numbers: two letters then 7 digits; (d1+d3+d5) + 2*(d2+d4+d6) ends in the 7th digit.</summary>
    public static bool Dea(string value)
    {
        if (value.Length != 9 || !char.IsAsciiLetter(value[0]) || !char.IsAsciiLetter(value[1])) return false;
        if (!value[2..].All(char.IsAsciiDigit)) return false;
        int D(int i) => value[i] - '0';
        return ((D(2) + D(4) + D(6)) + 2 * (D(3) + D(5) + D(7))) % 10 == D(8);
    }
}
