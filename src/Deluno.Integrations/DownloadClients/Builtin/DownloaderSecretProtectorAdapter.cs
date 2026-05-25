using Deluno.Downloader.Persistence;
using Deluno.Platform.Security;

namespace Deluno.Integrations.DownloadClients.Builtin;

/// <summary>
/// Bridges the platform-wide <see cref="ISecretProtector"/> (which
/// Downloader cannot reference directly per the architecture-doc
/// boundary rule) to the Downloader-local
/// <see cref="IDownloaderSecretProtector"/> contract. Pure
/// delegation — no business logic of its own.
/// </summary>
public sealed class DownloaderSecretProtectorAdapter(ISecretProtector inner)
    : IDownloaderSecretProtector
{
    public string Protect(string purpose, string plaintext) => inner.Protect(purpose, plaintext);
    public string? Unprotect(string purpose, string? protectedValue) => inner.Unprotect(purpose, protectedValue);
    public bool IsProtected(string? value) => inner.IsProtected(value);
}
