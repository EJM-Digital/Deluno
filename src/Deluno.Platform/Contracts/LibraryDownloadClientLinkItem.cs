namespace Deluno.Platform.Contracts;

public sealed record LibraryDownloadClientLinkItem(
    string Id,
    string LibraryId,
    string DownloadClientId,
    string DownloadClientName,
    int Priority,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
