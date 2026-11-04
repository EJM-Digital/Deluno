using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Deluno.Security.Hardening.Backends;

/// <summary>
/// The only places the Windows data-protection calls are made. Nothing but those calls lives here (they cannot run
/// on any other system), so this is the one file of the Windows backend left out of coverage.
/// </summary>
internal static class WindowsSecretProtectors
{
    public static ISecretProtector Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows data protection is only on Windows.");
        }

        return new WindowsDpapiSecretProtector(DataProtectionScope.CurrentUser, Protect, Unprotect);
    }

    [SupportedOSPlatform("windows")]
    internal static byte[] Protect(byte[] plain, byte[] entropy, DataProtectionScope scope)
        => ProtectedData.Protect(plain, entropy, scope);

    [SupportedOSPlatform("windows")]
    internal static byte[] Unprotect(byte[] cipher, byte[] entropy, DataProtectionScope scope)
        => ProtectedData.Unprotect(cipher, entropy, scope);
}
