namespace Deluno.Integrations.DownloadClients;

public sealed class DownloadClientRegistry : IDownloadClientRegistry
{
    private readonly IReadOnlyDictionary<string, IDownloadClient> clients;

    public DownloadClientRegistry(IEnumerable<IDownloadClient> clients)
    {
        this.clients = clients.ToDictionary(
            client => client.Protocol,
            StringComparer.OrdinalIgnoreCase);
        KnownProtocols = this.clients.Keys
            .OrderBy(protocol => protocol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyCollection<string> KnownProtocols { get; }

    public bool TryGet(string? protocol, out IDownloadClient client)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            client = null!;
            return false;
        }

        return clients.TryGetValue(protocol.Trim(), out client!);
    }
}
