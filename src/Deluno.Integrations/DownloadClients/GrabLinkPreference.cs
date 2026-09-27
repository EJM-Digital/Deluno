using Deluno.Integrations.Search.Trackers;

namespace Deluno.Integrations.DownloadClients;

/// <summary>Which of a resolved grab's two possible links Deluno should actually send to the download client.</summary>
public sealed record GrabLinkChoice(string? MagnetUrl, byte[]? TorrentFile);

/// <summary>
/// #752: when a grab resolves to both a magnet URL and a .torrent file,
/// the indexer's own Prefer Magnet Links setting decides which one Deluno
/// sends to the download client. Neither field alone changes - only which
/// one wins when there really is a choice to make.
/// </summary>
public static class GrabLinkPreference
{
    public static GrabLinkChoice Choose(TrackerGrab grab, bool preferMagnetUrl)
    {
        if (grab.MagnetUrl is { } magnet && grab.TorrentFile is { } torrentFile)
        {
            return preferMagnetUrl
                ? new GrabLinkChoice(magnet, null)
                : new GrabLinkChoice(null, torrentFile);
        }

        return grab.MagnetUrl is { } onlyMagnet
            ? new GrabLinkChoice(onlyMagnet, null)
            : new GrabLinkChoice(null, grab.TorrentFile);
    }
}
