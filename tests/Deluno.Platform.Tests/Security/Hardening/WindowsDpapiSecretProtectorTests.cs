using System.Runtime.InteropServices;
using Deluno.Platform.Security.Hardening.Backends;

namespace Deluno.Platform.Tests.Security.Hardening;

/// <summary>
/// Windows-only DPAPI round-trip tests. xUnit doesn't have a built-in
/// platform-skip attribute; we use a runtime check and short-circuit
/// non-Windows runs.
/// </summary>
public class WindowsDpapiSecretProtectorTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void Round_trip_returns_original_plaintext_on_windows()
    {
        if (!IsWindows) return; // skip silently on non-Windows CI

#pragma warning disable CA1416
        var p = new WindowsDpapiSecretProtector();

        var protectedValue = p.Protect("nzb-server-password", "hunter2");
        Assert.True(p.IsProtected(protectedValue));
        Assert.StartsWith(WindowsDpapiSecretProtector.Prefix, protectedValue);

        var unprotected = p.Unprotect("nzb-server-password", protectedValue);
        Assert.Equal("hunter2", unprotected);
#pragma warning restore CA1416
    }

    [Fact]
    public void Different_purposes_cannot_decrypt_each_other_on_windows()
    {
        if (!IsWindows) return;

#pragma warning disable CA1416
        var p = new WindowsDpapiSecretProtector();
        var aCipher = p.Protect("purpose-a", "secret");

        // DPAPI throws CryptographicException on entropy mismatch.
        Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => p.Unprotect("purpose-b", aCipher));
#pragma warning restore CA1416
    }

    [Fact]
    public void IsProtected_recognizes_only_dpapi_prefix()
    {
        if (!IsWindows) return;

#pragma warning disable CA1416
        var p = new WindowsDpapiSecretProtector();
        Assert.True(p.IsProtected("dpapi:v1:somecipher"));
        Assert.False(p.IsProtected("dp:v1:legacy"));
        Assert.False(p.IsProtected("aes:v1:other"));
        Assert.False(p.IsProtected("plaintext"));
        Assert.False(p.IsProtected(null));
#pragma warning restore CA1416
    }
}
