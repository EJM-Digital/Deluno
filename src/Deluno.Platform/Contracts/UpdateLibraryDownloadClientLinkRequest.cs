namespace Deluno.Platform.Contracts;

public sealed record UpdateLibraryDownloadClientLinkRequest(
    string DownloadClientId,
    int? Priority);
