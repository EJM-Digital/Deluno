using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1061: the publisher check must prove the signature covers the file, and match the publisher exactly.</summary>
public sealed class AuthenticodeVerifierTests
{
    private static string? SignedSystemFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
            Path.Combine(Environment.SystemDirectory, "msiexec.exe")
        };
        var verifier = new WindowsAuthenticodeVerifier();
        var found = candidates.FirstOrDefault(path => File.Exists(path) && verifier.IsSigned(path));
        Assert.NotNull(found); // Windows always has a Microsoft-signed file; a null here means the check itself is broken.
        return found;
    }

    private static string TempCopy(string source, Action<byte[]>? modify = null)
    {
        var bytes = File.ReadAllBytes(source);
        modify?.Invoke(bytes);
        var path = Path.Combine(Path.GetTempPath(), $"deluno-auth-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void A_file_with_a_genuine_signature_is_accepted_for_its_exact_publisher()
    {
        var signed = SignedSystemFile();
        if (signed is null)
        {
            return; // Not Windows, or no Microsoft-signed file to test with.
        }

        var verifier = new WindowsAuthenticodeVerifier();
        Assert.True(verifier.IsSigned(signed));
        Assert.True(verifier.IsSignedBy(signed, null));
        Assert.True(verifier.IsSignedBy(signed, "Microsoft Corporation"));
    }

    [Fact]
    public void A_publisher_that_is_only_part_of_the_signers_name_is_refused()
    {
        var signed = SignedSystemFile();
        if (signed is null)
        {
            return;
        }

        var verifier = new WindowsAuthenticodeVerifier();
        Assert.False(verifier.IsSignedBy(signed, "Microsoft"));
        Assert.False(verifier.IsSignedBy(signed, "Corporation"));
        Assert.False(verifier.IsSignedBy(signed, "SignPath Foundation"));
    }

    [Fact]
    public void A_signed_file_whose_bytes_were_changed_is_refused_even_though_it_still_carries_the_signature()
    {
        var signed = SignedSystemFile();
        if (signed is null)
        {
            return;
        }

        var tampered = TempCopy(signed, bytes => bytes[0x400] ^= 0xFF);
        try
        {
            var verifier = new WindowsAuthenticodeVerifier();
            // The certificate is still readable from the file...
#pragma warning disable SYSLIB0057
            using var lifted = X509Certificate.CreateFromSignedFile(tampered);
#pragma warning restore SYSLIB0057
            Assert.Contains("Microsoft Corporation", lifted.Subject, StringComparison.Ordinal);
            // ...but the signature no longer covers the file, so it is not accepted.
            Assert.False(verifier.IsSigned(tampered));
            Assert.False(verifier.IsSignedBy(tampered, "Microsoft Corporation"));
            Assert.False(verifier.IsSignedBy(tampered, null));
        }
        finally
        {
            File.Delete(tampered);
        }
    }

    [Fact]
    public void An_unsigned_file_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deluno-auth-{Guid.NewGuid():N}.msi");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(4096));
        try
        {
            var verifier = new WindowsAuthenticodeVerifier();
            Assert.False(verifier.IsSigned(path));
            Assert.False(verifier.IsSignedBy(path, null));
            Assert.False(verifier.IsSignedBy(path, "Microsoft Corporation"));
            Assert.False(verifier.IsSignedBy(Path.Combine(Path.GetTempPath(), "does-not-exist.msi"), null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("CN=SignPath Foundation, O=SignPath Foundation, C=US", "SignPath Foundation", true)]
    [InlineData("CN=Some Product, O=SignPath Foundation, C=US", "SignPath Foundation", true)]
    [InlineData("CN=SignPath Foundation, O=Someone, C=US", "signpath foundation", true)]
    [InlineData("CN=Not SignPath Foundation Ltd, O=Not SignPath Foundation Ltd", "SignPath Foundation", false)]
    [InlineData("CN=Evil, O=Evil, OU=SignPath Foundation", "SignPath Foundation", false)]
    [InlineData("CN=Evil, O=Evil, L=SignPath Foundation", "SignPath Foundation", false)]
    [InlineData("CN=SignPath Foundation Fake, O=SignPath", "SignPath Foundation", false)]
    public void The_publisher_must_equal_the_certificates_common_name_or_organisation(string subject, string publisher, bool expected)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName(subject), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(expected, WindowsAuthenticodeVerifier.PublisherMatches(certificate, publisher));
    }
}
