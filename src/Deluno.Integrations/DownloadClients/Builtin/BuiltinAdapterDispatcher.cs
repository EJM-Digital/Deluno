namespace Deluno.Integrations.DownloadClients.Builtin;

/// <summary>
/// Dispatches a download-client protocol value to the right
/// <see cref="IBuiltinDownloaderAdapter"/>. Injected into the existing
/// <c>DownloadClientGrabService</c> and <c>DownloadClientTelemetryService</c>
/// so the switch blocks have a single dependency to call regardless of
/// how many built-in protocols ship.
/// </summary>
public sealed class BuiltinAdapterDispatcher
{
    private readonly IReadOnlyDictionary<string, IBuiltinDownloaderAdapter> _byProtocol;

    public BuiltinAdapterDispatcher(IEnumerable<IBuiltinDownloaderAdapter> adapters)
    {
        var map = new Dictionary<string, IBuiltinDownloaderAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in adapters) map[a.Protocol] = a;
        _byProtocol = map;
    }

    public bool Handles(string protocol) => _byProtocol.ContainsKey(protocol);

    public IBuiltinDownloaderAdapter Get(string protocol)
        => _byProtocol.TryGetValue(protocol, out var adapter)
            ? adapter
            : throw new InvalidOperationException(
                $"No built-in downloader adapter registered for protocol '{protocol}'. " +
                $"Did you forget to call AddDelunoBuiltInDownloaders() in DI?");
}
