namespace Deluno.Downloader.Nzb.Nntp;

/// <summary>
/// Per-server config: host + port + TLS + credentials + pool size +
/// tier/priority. Credential fields here are PLAINTEXT — the
/// repository layer unprotects them via <c>ISecretProtector</c> on the
/// way out of SQLite and re-protects on the way in.
/// </summary>
public sealed record NntpServerOptions(
    string Id,
    string Name,
    string Host,
    int Port,
    bool UseTls,
    string? Username = null,
    string? Password = null,
    int MaxConnections = 8,
    int Priority = 0,
    NntpServerTier Tier = NntpServerTier.Primary,
    int? RetentionDays = null,
    bool Enabled = true);

/// <summary>
/// Tier classification for multi-server failover. Per the architecture
/// doc's algorithm: try all Primary first (priority order); then Backup;
/// then Fill. A 430 on one tier is not job failure — just escalate.
/// </summary>
public enum NntpServerTier
{
    Primary,
    Backup,
    Fill,
}

public sealed record NntpResponse(int Code, string Text)
{
    public override string ToString() => $"{Code} {Text}";
}

public class NntpProtocolException : Exception
{
    public NntpProtocolException(string message) : base(message) { }
}

public sealed class NntpArticleNotFoundException : Exception
{
    public string MessageId { get; }
    public NntpArticleNotFoundException(string messageId) : base($"Article not found: {messageId}")
        => MessageId = messageId;
}

public sealed class NntpAuthenticationException : Exception
{
    public NntpAuthenticationException(string message) : base(message) { }
}
