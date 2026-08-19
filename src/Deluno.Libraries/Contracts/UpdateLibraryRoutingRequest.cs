namespace Deluno.Libraries.Contracts;

public sealed record UpdateLibraryRoutingRequest(
    IReadOnlyList<UpdateLibrarySourceLinkRequest>? Sources,
    IReadOnlyList<UpdateLibraryDownloadClientLinkRequest>? DownloadClients);
