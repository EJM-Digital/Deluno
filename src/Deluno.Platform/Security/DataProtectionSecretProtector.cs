using Microsoft.AspNetCore.DataProtection;

namespace Deluno.Platform.Security;

public sealed class DataProtectionSecretProtector(IDataProtectionProvider dataProtectionProvider)
    : ISecretProtector
{
    private const string Prefix = "dp:v1:";

    public string Protect(string purpose, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            throw new ArgumentException("Secret plaintext cannot be empty.", nameof(plaintext));
        }

        return Prefix + CreateProtector(purpose).Protect(plaintext);
    }

    public string? Unprotect(string purpose, string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return protectedValue;
        }

        return CreateProtector(purpose).Unprotect(protectedValue[Prefix.Length..]);
    }

    public bool IsProtected(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.StartsWith(Prefix, StringComparison.Ordinal);

    private IDataProtector CreateProtector(string purpose)
        => dataProtectionProvider.CreateProtector("Deluno.Platform.Secrets", purpose);
}
