using VeilGate.Domain.Common;

namespace VeilGate.Domain;

/// <summary>
/// Password rules (Security doc 4.1): length matters more than complexity. 12 to 128 characters, no tiny blocklist of obvious
/// passwords, not just one repeated character, and not built from the person's own email name.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1234", "passwordpassword", "123456789012", "1234567890123", "qwertyuiop12", "qwertyuiopas",
        "iloveyou1234", "administrator", "welcome12345", "letmein12345", "changemechange"
    };

    public static Result Validate(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return Result.Failure(Error.Validation("password.weak", $"Too short. Use at least {MinLength} characters. Longer phrases are better."));
        if (password.Length > MaxLength)
            return Result.Failure(Error.Validation("password.weak", $"Too long. Use at most {MaxLength} characters."));
        if (Common.Contains(password) || password.Distinct().Count() == 1)
            return Result.Failure(Error.Validation("password.weak", "That password is too common. Choose something less guessable."));

        var local = (email ?? string.Empty).Split('@')[0];
        if (local.Length >= 4 && password.Contains(local, StringComparison.OrdinalIgnoreCase))
            return Result.Failure(Error.Validation("password.weak", "Do not build your password from your email name."));

        return Result.Success();
    }
}
