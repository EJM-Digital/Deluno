namespace Deluno.Integrations.DownloadClients;

public interface IDownloadClientRegistry
{
    IReadOnlyCollection<string> KnownProtocols { get; }

    bool TryGet(string? protocol, out IDownloadClient client);
}
