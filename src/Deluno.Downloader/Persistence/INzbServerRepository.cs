using Deluno.Downloader.Nzb.Nntp;

namespace Deluno.Downloader.Persistence;

/// <summary>
/// CRUD over the <c>nzb_servers</c> table. The execution worker reads
/// every enabled server here to build a <see cref="NntpConnectionPool"/>
/// per server before kicking off a job's multi-server fetch.
///
/// Credential round-trip goes through <see cref="ISecretProtector"/>:
/// values stored as <c>aes:v1:</c> / <c>dpapi:v1:</c> / <c>dp:v1:</c>
/// prefixed strings in the <c>username_protected</c> /
/// <c>password_protected</c> / <c>proxy_url_protected</c> columns are
/// unprotected on the way out and re-protected on the way in. Callers
/// see plaintext.
/// </summary>
public interface INzbServerRepository
{
    Task<IReadOnlyList<NntpServerOptions>> ListEnabledAsync(CancellationToken ct);
    Task<NntpServerOptions?> GetAsync(string id, CancellationToken ct);
    Task UpsertAsync(NntpServerOptions server, CancellationToken ct);
    Task RemoveAsync(string id, CancellationToken ct);
}
