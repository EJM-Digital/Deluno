using System.Security.Cryptography;
using Deluno.Security.Hardening.Backends;

namespace Deluno.Platform.Tests.Security.Hardening;

public class FileBackedSecretProtectorTests
{
    private static byte[] FreshKey() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Round_trip_returns_original_plaintext()
    {
        var p = new FileBackedSecretProtector(FreshKey());

        var protectedValue = p.Protect("nzb-server-password", "hunter2");
        Assert.True(p.IsProtected(protectedValue));
        Assert.StartsWith(FileBackedSecretProtector.Prefix, protectedValue);

        var unprotected = p.Unprotect("nzb-server-password", protectedValue);
        Assert.Equal("hunter2", unprotected);
    }

    [Fact]
    public void Protect_with_same_input_produces_different_ciphertexts()
    {
        // AES-GCM uses a random nonce per Protect call. Two Protect calls
        // with identical plaintext + purpose must produce different ciphertext
        // — otherwise an attacker observing the DB could correlate identical
        // secrets across rows.
        var p = new FileBackedSecretProtector(FreshKey());

        var a = p.Protect("p", "same-secret");
        var b = p.Protect("p", "same-secret");

        Assert.NotEqual(a, b);
        Assert.Equal("same-secret", p.Unprotect("p", a));
        Assert.Equal("same-secret", p.Unprotect("p", b));
    }

    [Fact]
    public void Different_purposes_cannot_decrypt_each_other()
    {
        // Purpose binding via AES-GCM AAD: a ciphertext protected for purpose A
        // must fail authentication when unprotected with purpose B.
        var p = new FileBackedSecretProtector(FreshKey());

        var aCipher = p.Protect("purpose-a", "secret");

        Assert.Throws<AuthenticationTagMismatchException>(
            () => p.Unprotect("purpose-b", aCipher));
    }

    [Fact]
    public void Tampered_ciphertext_throws_on_unprotect()
    {
        var p = new FileBackedSecretProtector(FreshKey());

        var good = p.Protect("p", "real value");
        // Flip a byte in the payload (skip the "aes:v1:" prefix).
        var payload = good[FileBackedSecretProtector.Prefix.Length..];
        var raw = Convert.FromBase64String(payload);
        raw[20] ^= 0xFF;
        var tampered = FileBackedSecretProtector.Prefix + Convert.ToBase64String(raw);

        Assert.Throws<AuthenticationTagMismatchException>(
            () => p.Unprotect("p", tampered));
    }

    [Fact]
    public void Different_keys_cannot_decrypt_each_other()
    {
        var p1 = new FileBackedSecretProtector(FreshKey());
        var p2 = new FileBackedSecretProtector(FreshKey());

        var fromOne = p1.Protect("p", "value");

        Assert.Throws<AuthenticationTagMismatchException>(
            () => p2.Unprotect("p", fromOne));
    }

    [Fact]
    public void Empty_plaintext_is_rejected()
    {
        var p = new FileBackedSecretProtector(FreshKey());
        Assert.Throws<ArgumentException>(() => p.Protect("p", string.Empty));
        Assert.Throws<ArgumentException>(() => p.Protect("p", "   "));
    }

    [Fact]
    public void Unprotect_returns_passthrough_for_non_prefixed_values()
    {
        // Matches the existing DataProtectionSecretProtector convention:
        // a value with no recognized prefix is assumed to be plaintext
        // (e.g. stored before any protection was wired in). This is
        // critical for the first-run upgrade path.
        var p = new FileBackedSecretProtector(FreshKey());
        Assert.Equal("not-encrypted", p.Unprotect("p", "not-encrypted"));
    }

    [Fact]
    public void Constructor_rejects_wrong_size_keys()
    {
        Assert.Throws<ArgumentException>(() => new FileBackedSecretProtector(new byte[16]));
        Assert.Throws<ArgumentException>(() => new FileBackedSecretProtector(new byte[64]));
    }

    [Fact]
    public void ResolveOrCreateKey_uses_env_var_when_set()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var b64 = Convert.ToBase64String(key);
        Environment.SetEnvironmentVariable(FileBackedSecretProtector.EnvVarName, b64);
        try
        {
            // Path doesn't matter — env var wins. Use a guaranteed-nonexistent path.
            var bogus = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no.key");
            var (resolved, source) = FileBackedSecretProtector.ResolveOrCreateKey(bogus);

            Assert.Equal(key, resolved);
            Assert.StartsWith("env:", source);
            Assert.False(File.Exists(bogus), "Env var must take precedence; no file should be created.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileBackedSecretProtector.EnvVarName, null);
        }
    }

    [Fact]
    public void ResolveOrCreateKey_reads_existing_file()
    {
        Environment.SetEnvironmentVariable(FileBackedSecretProtector.EnvVarName, null);
        var dir = Path.Combine(Path.GetTempPath(), $"deluno-secrets-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "master.key");
        try
        {
            Directory.CreateDirectory(dir);
            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(path, key);

            var (resolved, source) = FileBackedSecretProtector.ResolveOrCreateKey(path);

            Assert.Equal(key, resolved);
            Assert.Contains("file:", source);
            Assert.DoesNotContain(":created", source);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveOrCreateKey_generates_random_key_when_neither_source_present()
    {
        Environment.SetEnvironmentVariable(FileBackedSecretProtector.EnvVarName, null);
        var dir = Path.Combine(Path.GetTempPath(), $"deluno-secrets-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "master.key");
        try
        {
            Assert.False(File.Exists(path));

            var (resolved, source) = FileBackedSecretProtector.ResolveOrCreateKey(path);

            Assert.Equal(32, resolved.Length);
            Assert.EndsWith(":created", source);
            Assert.True(File.Exists(path));
            Assert.Equal(32, new FileInfo(path).Length);

            // Calling again must NOT regenerate — same key returned.
            var (second, secondSource) = FileBackedSecretProtector.ResolveOrCreateKey(path);
            Assert.Equal(resolved, second);
            Assert.DoesNotContain(":created", secondSource);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveOrCreateKey_rejects_malformed_env_var()
    {
        Environment.SetEnvironmentVariable(FileBackedSecretProtector.EnvVarName, "not-base64-???");
        try
        {
            var bogus = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no.key");
            Assert.Throws<InvalidOperationException>(
                () => FileBackedSecretProtector.ResolveOrCreateKey(bogus));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileBackedSecretProtector.EnvVarName, null);
        }
    }

    [Fact]
    public void ResolveOrCreateKey_rejects_wrong_size_env_var()
    {
        // 16-byte key (base64 encoded) — wrong size.
        Environment.SetEnvironmentVariable(
            FileBackedSecretProtector.EnvVarName,
            Convert.ToBase64String(new byte[16]));
        try
        {
            var bogus = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no.key");
            Assert.Throws<InvalidOperationException>(
                () => FileBackedSecretProtector.ResolveOrCreateKey(bogus));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileBackedSecretProtector.EnvVarName, null);
        }
    }
}
