using System.Collections.Concurrent;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// Remembers which download clients have had their one look, since Deluno
/// started, for dispatches still filed under Transmission's old session
/// numbers (#1062). A session number is only good for the session that handed
/// it out, so the look happens on the first poll and never again.
/// </summary>
public sealed class LegacyItemIdUpgradeMemory
{
    private readonly ConcurrentDictionary<string, byte> looked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True only the first time a client is asked about since Deluno started.</summary>
    public bool TryBegin(string clientId) => looked.TryAdd(clientId, 0);
}
