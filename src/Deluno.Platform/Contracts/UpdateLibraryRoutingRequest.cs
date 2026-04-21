namespace Deluno.Platform.Contracts;

public sealed record UpdateLibraryRoutingRequest(
    IReadOnlyList<UpdateLibrarySourceLinkRequest>? Sources,
    IReadOnlyList<UpdateLibraryDownloadClientLinkRequest>? DownloadClients);
