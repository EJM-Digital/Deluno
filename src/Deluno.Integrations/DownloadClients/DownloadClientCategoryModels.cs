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
    string? SavePath = null);
