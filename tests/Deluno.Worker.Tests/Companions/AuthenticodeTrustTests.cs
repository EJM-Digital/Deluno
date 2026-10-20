using System.Security.Cryptography;
using Deluno.Companions;

namespace Deluno.Worker.Tests.Companions;

/// <summary>
/// C.14.18: the Authenticode check an installer must pass when its artifact says it must be signed (SABnzbd's does), run through
/// Windows' own WinVerifyTrust on real files in a real folder. A file Microsoft signed passes; the same file with one byte changed,
/// and an unsigned file, do not. Off Windows there is no trust provider, so nothing is ever accepted as signed.
/// </summary>
public sealed class AuthenticodeTrustTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-authenticode-tests", Guid.NewGuid().ToString("N"));

    public AuthenticodeTrustTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // Every .NET runtime assembly carries Microsoft's own embedded Authenticode signature.
    private static string SignedFile() => typeof(object).Assembly.Location;

    private string Copy(string name)
    {
        var path = Path.Combine(_folder, name);
        File.Copy(SignedFile(), path);
        return path;
    }

    private static CompanionArtifact ArtifactFor(string path, bool mustBeSigned)
    {
        using var stream = File.OpenRead(path);
        return new CompanionArtifact(Path.GetFileName(path), new Uri("https://example.invalid/installer.exe"), Convert.ToHexString(SHA256.HashData(stream)), mustBeSigned, MinimumBytes: 1);
    }

    [Fact]
    public void A_file_with_a_valid_signature_is_accepted_and_an_unsigned_file_is_not()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(AuthenticodeTrust.IsValid(SignedFile()), "off Windows nothing can be verified, so nothing is accepted");
            return;
        }

        var unsigned = Path.Combine(_folder, "unsigned.exe");
        File.WriteAllBytes(unsigned, RandomNumberGenerator.GetBytes(4096));

        Assert.True(AuthenticodeTrust.IsValid(Copy("signed.exe")));
        Assert.False(AuthenticodeTrust.IsValid(unsigned));
    }

    [Fact]
    public void A_signed_file_changed_by_one_byte_no_longer_verifies()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Copy("tampered.exe");
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.False(AuthenticodeTrust.IsValid(path));
    }

    [Fact]
    public void A_download_that_must_be_signed_is_refused_when_its_hash_matches_but_it_carries_no_valid_signature()
    {
        var unsigned = Path.Combine(_folder, "setup.exe");
        File.WriteAllBytes(unsigned, RandomNumberGenerator.GetBytes(4096));

        var problem = CompanionDownloader.Verify(ArtifactFor(unsigned, mustBeSigned: true), unsigned);

        Assert.Equal("setup.exe does not carry a valid signature. Nothing was installed.", problem);
        Assert.Null(CompanionDownloader.Verify(ArtifactFor(unsigned, mustBeSigned: false), unsigned));
    }

    [Fact]
    public void A_download_that_must_be_signed_is_accepted_when_the_hash_matches_and_the_signature_is_valid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Copy("setup.exe");

        Assert.Null(CompanionDownloader.Verify(ArtifactFor(path, mustBeSigned: true), path));
    }
}
