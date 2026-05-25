namespace Deluno.Downloader.Persistence;

/// <summary>
/// Downloader-local credential-protection contract. The Downloader
/// project cannot reference Deluno.Platform directly (boundary rule
/// per the architecture doc), so we define our own narrow contract
/// and the Integrations module supplies an adapter that delegates to
/// the platform-wide <c>ISecretProtector</c>.
///
/// Same shape: <c>Protect(purpose, plaintext) → opaque prefixed string</c>;
/// <c>Unprotect(purpose, value) → plaintext or null</c>;
/// <c>IsProtected(value)</c> checks the prefix. Implementation details
/// (DPAPI / AES-GCM / DataProtection) live in Platform.
/// </summary>
public interface IDownloaderSecretProtector
{
    string Protect(string purpose, string plaintext);
    string? Unprotect(string purpose, string? protectedValue);
    bool IsProtected(string? value);
}
