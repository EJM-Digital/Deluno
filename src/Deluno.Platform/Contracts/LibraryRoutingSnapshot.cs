namespace Deluno.Platform.Contracts;

public sealed record LibraryRoutingSnapshot(
    string LibraryId,
    string LibraryName,
    IReadOnlyList<LibrarySourceLinkItem> Sources,
    IReadOnlyList<LibraryDownloadClientLinkItem> DownloadClients);
