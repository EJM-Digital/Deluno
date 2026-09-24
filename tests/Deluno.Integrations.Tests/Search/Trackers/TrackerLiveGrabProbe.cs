using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #606: resolves one real release link through its tracker's download block,
/// live. Off unless DELUNO_LIVE_GRAB is "trackerid|link".
/// </summary>
public sealed class TrackerLiveGrabProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task A_real_release_link_resolves_to_a_magnet_or_a_torrent()
    {
        if (Environment.GetEnvironmentVariable("DELUNO_LIVE_GRAB") is not { Length: > 0 } spec)
        {
            return;
        }

        var (id, link) = (spec.Split('|')[0], spec.Split('|')[1]);
        var catalog = new TrackerDefinitionCatalog(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);
        var definition = catalog.Find(id)!;
        var engine = new TrackerEngine(NullLogger<TrackerEngine>.Instance);
        var grab = await engine.ResolveDownloadAsync(new TrackerConnection("probe", definition, definition.Links[0], new Dictionary<string, string?>()), link, CancellationToken.None);
        output.WriteLine($"magnet: {grab.MagnetUrl}");
        output.WriteLine($"torrent bytes: {grab.TorrentFile?.Length}");
    }
}
