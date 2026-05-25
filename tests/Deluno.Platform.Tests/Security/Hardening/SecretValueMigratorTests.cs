using System.Security.Cryptography;
using Deluno.Platform.Security;
using Deluno.Platform.Security.Hardening;
using Deluno.Platform.Security.Hardening.Backends;
using Microsoft.AspNetCore.DataProtection;

namespace Deluno.Platform.Tests.Security.Hardening;

public class SecretValueMigratorTests
{
    private static (CompositeSecretProtector Composite, DataProtectionSecretProtector Legacy)
        BuildComposite()
    {
        var dpp = new EphemeralDataProtectionProvider();
        var legacy = new DataProtectionSecretProtector(dpp);
        var active = new FileBackedSecretProtector(RandomNumberGenerator.GetBytes(32));
        var composite = new CompositeSecretProtector(active, new ISecretProtector[] { legacy });
        return (composite, legacy);
    }

    [Fact]
    public void Unprotect_on_legacy_value_returns_plaintext_AND_migrated_value()
    {
        var (composite, legacy) = BuildComposite();
        var legacyValue = legacy.Protect("p", "old-secret");
        var migrator = new SecretValueMigrator(composite);

        var outcome = migrator.Unprotect("p", legacyValue);

        Assert.Equal("old-secret", outcome.Plaintext);
        Assert.NotNull(outcome.MigratedValue);
        Assert.StartsWith(FileBackedSecretProtector.Prefix, outcome.MigratedValue);

        // The migrated value must round-trip through the composite too.
        Assert.Equal("old-secret", composite.Unprotect("p", outcome.MigratedValue));
    }

    [Fact]
    public void Unprotect_on_active_value_returns_plaintext_with_no_migration()
    {
        var (composite, _) = BuildComposite();
        var migrator = new SecretValueMigrator(composite);
        var activeValue = composite.Protect("p", "new-secret");

        var outcome = migrator.Unprotect("p", activeValue);

        Assert.Equal("new-secret", outcome.Plaintext);
        Assert.Null(outcome.MigratedValue);
    }

    [Fact]
    public void Unprotect_on_plaintext_passes_through_with_no_migration()
    {
        var (composite, _) = BuildComposite();
        var migrator = new SecretValueMigrator(composite);

        var outcome = migrator.Unprotect("p", "totally-plain-value");

        Assert.Equal("totally-plain-value", outcome.Plaintext);
        Assert.Null(outcome.MigratedValue);
    }

    [Fact]
    public void Unprotect_on_null_or_empty_returns_null_outcome()
    {
        var (composite, _) = BuildComposite();
        var migrator = new SecretValueMigrator(composite);

        var fromNull = migrator.Unprotect("p", null);
        var fromEmpty = migrator.Unprotect("p", "");
        var fromWhite = migrator.Unprotect("p", "   ");

        Assert.Null(fromNull.Plaintext);
        Assert.Null(fromNull.MigratedValue);
        Assert.Null(fromEmpty.Plaintext);
        Assert.Null(fromEmpty.MigratedValue);
        Assert.Null(fromWhite.Plaintext);
        Assert.Null(fromWhite.MigratedValue);
    }

    [Fact]
    public void Constructor_rejects_non_composite_protector()
    {
        var bare = new FileBackedSecretProtector(RandomNumberGenerator.GetBytes(32));
        Assert.Throws<ArgumentException>(() => new SecretValueMigrator(bare));
    }

    [Fact]
    public void Migration_outcome_supports_persist_pattern()
    {
        var (composite, legacy) = BuildComposite();
        var migrator = new SecretValueMigrator(composite);
        var stored = legacy.Protect("p", "secret");

        // First read: triggers migration.
        var first = migrator.Unprotect("p", stored);
        Assert.NotNull(first.MigratedValue);
        stored = first.MigratedValue!; // caller "persists"

        // Second read: no migration needed.
        var second = migrator.Unprotect("p", stored);
        Assert.Equal("secret", second.Plaintext);
        Assert.Null(second.MigratedValue);
    }
}
