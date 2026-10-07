namespace Deluno.Integrations.DownloadClients;

public static class DownloadClientCategoryStatuses
{
    public const string Ready = "ready";
    public const string Missing = "missing";
    public const string Unsupported = "unsupported";
    public const string Unreachable = "unreachable";
    public const string Configuration = "configuration";
}

public sealed record DownloadClientCategoryCheckRequest(string Category);

public sealed record DownloadClientCategoryCheckResult(
    string ClientId,
    string ClientName,
    string Category,
    string Status,
    string Message,
    bool Supported,
    bool Found,
    /// <summary>
    /// Where the client will actually put files for this category, when it will
    /// say. Reported because a category's *name* existing is not the thing that
    /// makes a download land where Deluno is looking - and checking only the
    /// name is how a misrouted category came back "ready".
    /// </summary>
    string? SavePath = null,
    /// <summary>
    /// Where the client keeps a download while it is in progress (#1164): Deluge's download location,
    /// qBittorrent's temporary path, Transmission's incomplete folder, SABnzbd's temporary download
    /// folder, NZBGet's intermediate folder. When the client has none switched on this is the folder
    /// the download finishes in, because that is where it is then written. Null when the client did not say.
    /// </summary>
    string? IncompletePath = null);
