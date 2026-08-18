using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Deluno.Security.Hardening.Backends;

/// <summary>
/// Windows-only <see cref="ISecretProtector"/> backed by DPAPI via
/// <see cref="ProtectedData"/>. Ciphertext is bound to the current user
/// account by default (<see cref="DataProtectionScope.CurrentUser"/>).
///
/// Output format: <c>dpapi:v1:&lt;base64 ciphertext&gt;</c>. The "v1" tag
/// lets us migrate to a different scope or AAD later without breaking
/// existing reads.
///
/// The <c>purpose</c> argument is passed as DPAPI's optional entropy
/// parameter, giving per-purpose domain separation: a value protected for
/// purpose "nzb-server-password" cannot be unprotected as
/// "tracker-passkey" even by the same user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    public const string Prefix = "dpapi:v1:";

    private readonly DataProtectionScope _scope;

    public WindowsDpapiSecretProtector(DataProtectionScope scope = DataProtectionScope.CurrentUser)
        => _scope = scope;

    public string Protect(string purpose, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            throw new ArgumentException("Secret plaintext cannot be empty.", nameof(plaintext));

        var entropy = PurposeEntropy(purpose);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = ProtectedData.Protect(plain, entropy, _scope);
        return Prefix + Convert.ToBase64String(cipher);
    }

    public string? Unprotect(string purpose, string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
            return null;
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            return protectedValue; // matches DataProtectionSecretProtector convention: passthrough for non-prefixed

        var entropy = PurposeEntropy(purpose);
        var cipher = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        var plain = ProtectedData.Unprotect(cipher, entropy, _scope);
        return Encoding.UTF8.GetString(plain);
    }

    public bool IsProtected(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.StartsWith(Prefix, StringComparison.Ordinal);

    private static byte[] PurposeEntropy(string purpose)
        => Encoding.UTF8.GetBytes("Deluno.Platform.Secrets:" + purpose);
}
