using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// qBittorrent 5.2.1's own recorded replies (#1237) for a test of how Deluno reads one torrent. The torrent list is the recorded
/// reply of a real torrent, changed only in the values the test is about (see <see cref="RecordedReply.BodyWith"/>); sign-in, the
/// version and the main data are the recorded ones.
/// </summary>
internal static class RecordedQbittorrentRig
{
    /// <summary>A real finished torrent (state stalledUP, 100%), with its tracker working.</summary>
    public const string Finished = "finished-single-file";

    /// <summary>A real finished torrent whose tracker did not answer.</summary>
    public const string FinishedTrackerError = "finished-tracker-error";

    /// <summary>A real torrent with nothing arriving (state stalledDL, 0%).</summary>
    public const string Stalled = "stalled";

    /// <summary>A real torrent being downloaded from a seeder.</summary>
    public const string Downloading = "downloading";

    /// <summary>A real magnet whose metadata never came (state metaDL).</summary>
    public const string NoMetadata = "magnet-no-metadata";

    /// <summary>The recorded <c>torrents/info</c> reply of the given recording, with the first torrent's values changed.</summary>
    public static string Info(string recording, params (string Field, object? Value)[] edits)
        => RecordedReplies.Qbittorrent(recording).Body("torrents/info", edits.Select(e => ("0." + e.Field, e.Value)).ToArray());

    /// <summary>The recorded tracker list (the three pseudo-trackers a real torrent has) with its first entry made a real tracker in the given state.</summary>
    public static string Trackers(int status, string message, string url = "http://tracker.example/announce")
        => RecordedReplies.Qbittorrent("trackers").Body("torrents/trackers", ("0.status", status), ("0.msg", message), ("0.url", url));

    /// <summary>A qBittorrent answering from its recordings, with the torrent list (and optionally the tracker list) as given.</summary>
    public static HttpMessageHandler Handler(string info, string? trackers = null, Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept = null)
        => RecordedReplies.Handler(
            "qbittorrent",
            [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("settings"), RecordedReplies.Qbittorrent("trackers")],
            (request, body) =>
            {
                if (intercept?.Invoke(request, body) is { } own) return own;
                var path = request.RequestUri!.AbsolutePath;
                if (path.Contains("torrents/trackers", StringComparison.Ordinal) && trackers is not null) return RecordedReplies.Json(trackers);
                if (path.EndsWith("torrents/info", StringComparison.Ordinal)) return RecordedReplies.Json(info);
                return null;
            });
}
