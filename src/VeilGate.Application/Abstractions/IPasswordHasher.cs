namespace VeilGate.Application.Abstractions;

public interface IPasswordHasher
{
    /// <summary>Returns a self-describing Argon2id string ($argon2id$v=19$m=..,t=..,p=..$salt$hash).</summary>
    string Hash(string password);
    bool Verify(string password, string encodedHash);
    /// <summary>Burns the same CPU time as a real check, so "unknown email" and "wrong password" take equally long.</summary>
    void VerifyDummy(string password);
    bool NeedsRehash(string encodedHash);
}
