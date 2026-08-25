namespace Deluno.Libraries.Contracts;

public sealed record UpdateLibraryDownloadClientLinkRequest(
    string DownloadClientId,
    int? Priority,
    string? Category = null);
