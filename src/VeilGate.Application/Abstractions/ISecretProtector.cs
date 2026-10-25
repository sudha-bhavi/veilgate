namespace VeilGate.Application.Abstractions;

// "Lock and unlock a small secret." Application only knows this promise;
// Infrastructure decides how it's done (AES today, a cloud key vault later).
public interface ISecretProtector
{
    string Protect(byte[] plaintext, string context);
    byte[] Unprotect(string protectedValue, string context);
}