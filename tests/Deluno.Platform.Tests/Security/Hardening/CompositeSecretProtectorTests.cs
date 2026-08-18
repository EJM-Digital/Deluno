using System.Security.Cryptography;
using Deluno.Security;
using Deluno.Security.Hardening;
using Deluno.Security.Hardening.Backends;
using Microsoft.AspNetCore.DataProtection;

namespace Deluno.Platform.Tests.Security.Hardening;

public class CompositeSecretProtectorTests
{
    [Fact]
    public void Protect_always_uses_active_backend()
    {
        var activeKey = RandomNumberGenerator.GetBytes(32);
        var active = new FileBackedSecretProtector(activeKey);
        var legacy = new DataProtectionSecretProtector(EphemeralDataProtectionProvider());

        var composite = new CompositeSecretProtector(active, new ISecretProtector[] { legacy });

        var output = composite.Protect("p", "value");
        // New writes carry the active backend's prefix, not the legacy one.
        Assert.StartsWith(FileBackedSecretProtector.Prefix, output);
        Assert.False(output.StartsWith("dp:v1:", StringComparison.Ordinal));
    }

    [Fact]
    public void Unprotect_dispatches_to_legacy_reader_for_legacy_prefix()
    {
        // Write something with the legacy DataProtection backend, then
        // attempt to read it through a composite whose active backend is
        // FileBacked. The composite must route the read to the legacy
        // reader based on prefix.
        var dpp = EphemeralDataProtectionProvider();
        var legacy = new DataProtectionSecretProtector(dpp);
        var legacyValue = legacy.Protect("p", "legacy-value");
        Assert.StartsWith("dp:v1:", legacyValue);

        var active = new FileBackedSecretProtector(RandomNumberGenerator.GetBytes(32));
        var composite = new CompositeSecretProtector(active, new ISecretProtector[] { legacy });

        var unprotected = composite.Unprotect("p", legacyValue);
        Assert.Equal("legacy-value", unprotected);
    }

    [Fact]
    public void Unprotect_dispatches_to_active_for_active_prefix()
    {
        var active = new FileBackedSecretProtector(RandomNumberGenerator.GetBytes(32));
        var legacy = new DataProtectionSecretProtector(EphemeralDataProtectionProvider());

        var composite = new CompositeSecretProtector(active, new ISecretProtector[] { legacy });

        var protectedValue = composite.Protect("p", "new-value");
        Assert.Equal("new-value", composite.Unprotect("p", protectedValue));
    }

    [Fact]
    public void Unprotect_returns_passthrough_for_unknown_prefix()
    {
        // Same convention as the underlying protectors: unknown prefix
        // means treat as plaintext (legacy upgrade path).
        var active = new FileBackedSecretProtector(RandomNumberGenerator.GetBytes(32));
        var composite = new CompositeSecretProtector(active);

        Assert.Equal("not-protected-anything", composite.Unprotect("p", "not-protected-anything"));
    }

    [Fact]
    public void IsProtected_recognizes_active_or_legacy_prefix()
    {
        var active = new FileBackedSecretProtector(RandomNumberGenerator.GetBytes(32));
        var legacy = new DataProtectionSecretProtector(EphemeralDataProtectionProvider());

        var composite = new CompositeSecretProtector(active, new ISecretProtector[] { legacy });

        Assert.True(composite.IsProtected("aes:v1:something"));
        Assert.True(composite.IsProtected("dp:v1:legacy"));
        Assert.False(composite.IsProtected("plaintext"));
        Assert.False(composite.IsProtected(null));
    }

    [Fact]
    public void Empty_or_null_protected_value_returns_null()
    {
        var active = new FileBackedSecretProtector(RandomNumberGenerator.GetBytes(32));
        var composite = new CompositeSecretProtector(active);

        Assert.Null(composite.Unprotect("p", null));
        Assert.Null(composite.Unprotect("p", string.Empty));
        Assert.Null(composite.Unprotect("p", "   "));
    }

    private static IDataProtectionProvider EphemeralDataProtectionProvider()
        => new EphemeralDataProtectionProvider();
}
