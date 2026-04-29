namespace Deluno.Platform.Security;

public interface ISecretProtector
{
    string Protect(string purpose, string plaintext);

    string? Unprotect(string purpose, string? protectedValue);

    bool IsProtected(string? value);
}
