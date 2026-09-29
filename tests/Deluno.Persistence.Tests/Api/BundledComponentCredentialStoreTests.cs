using Deluno.Api.Network;
using Deluno.Security;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The sign-in the installer generates for a client (#1050) is kept protected
/// on disk, read back by the running Deluno, and never shown as plain text in
/// the file.
/// </summary>
public sealed class BundledComponentCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-cred-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_saved_sign_in_is_read_back_and_is_not_plain_text_in_the_file()
    {
        var path = FileBundledComponentCredentialStore.PathFor(_root);
        var store = new FileBundledComponentCredentialStore(path, new ReversingProtector());

        Assert.False(store.Has("qbittorrent"));
        store.Save("qbittorrent", new BundledComponentCredentials("deluno", "S3cretPassw0rd"));

        Assert.True(store.Has("qbittorrent"));
        var read = store.TryGet("qbittorrent");
        Assert.Equal("deluno", read!.Username);
        Assert.Equal("S3cretPassw0rd", read.Password);
        Assert.DoesNotContain("S3cretPassw0rd", File.ReadAllText(path));
    }

    [Fact]
    public void A_removed_sign_in_is_gone_and_the_others_stay()
    {
        var store = new FileBundledComponentCredentialStore(FileBundledComponentCredentialStore.PathFor(_root), new ReversingProtector());
        store.Save("qbittorrent", new BundledComponentCredentials("deluno", "one"));
        store.Save("nzbget", new BundledComponentCredentials("deluno", "two"));

        store.Remove("qbittorrent");
        store.Remove("never-saved");

        Assert.False(store.Has("qbittorrent"));
        Assert.Equal("two", store.TryGet("nzbget")!.Password);
    }

    [Fact]
    public void Another_kind_has_no_sign_in_and_a_second_save_replaces_the_first()
    {
        var store = new FileBundledComponentCredentialStore(FileBundledComponentCredentialStore.PathFor(_root), new ReversingProtector());
        store.Save("nzbget", new BundledComponentCredentials("deluno", "first"));
        store.Save("nzbget", new BundledComponentCredentials("deluno", "second"));

        Assert.Null(store.TryGet("qbittorrent"));
        Assert.Equal("second", store.TryGet("nzbget")!.Password);
    }

    [Fact]
    public void A_value_that_cannot_be_unprotected_reads_as_no_sign_in()
    {
        var path = FileBundledComponentCredentialStore.PathFor(_root);
        new FileBundledComponentCredentialStore(path, new ReversingProtector())
            .Save("qbittorrent", new BundledComponentCredentials("deluno", "pw"));

        var other = new FileBundledComponentCredentialStore(path, new ThrowingProtector());

        Assert.Null(other.TryGet("qbittorrent"));
    }

    [Fact]
    public void A_host_that_is_not_windows_keeps_no_sign_ins()
    {
        var store = new NoBundledComponentCredentialStore();
        store.Save("qbittorrent", new BundledComponentCredentials("deluno", "pw"));

        Assert.False(store.Has("qbittorrent"));
        Assert.Null(store.TryGet("qbittorrent"));
    }

    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string purpose, string plaintext) => "rev:" + purpose + ":" + new string(plaintext.Reverse().ToArray());

        public string? Unprotect(string purpose, string? protectedValue)
            => protectedValue is not null && protectedValue.StartsWith("rev:" + purpose + ":", StringComparison.Ordinal)
                ? new string(protectedValue[("rev:" + purpose + ":").Length..].Reverse().ToArray())
                : null;

        public bool IsProtected(string? value) => value?.StartsWith("rev:", StringComparison.Ordinal) == true;
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public string Protect(string purpose, string plaintext) => throw new InvalidOperationException();
        public string? Unprotect(string purpose, string? protectedValue) => throw new InvalidOperationException();
        public bool IsProtected(string? value) => true;
    }
}
