using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Transmission 4.1.3's own recorded replies (#1237) for a test of how Deluno reads one torrent. The torrent list is the recorded
/// reply of a real torrent, changed only in the values the test is about; the session settings and free space are the recorded ones.
/// </summary>
internal static class RecordedTransmissionRig
{
    /// <summary>A real finished torrent whose tracker is working.</summary>
    public const string Finished = "finished-single-file";

    /// <summary>A real finished torrent whose tracker did not answer.</summary>
    public const string FinishedTrackerError = "finished-tracker-error";

    /// <summary>A real torrent with no peer, nothing arriving.</summary>
    public const string Stalled = "stalled";

    /// <summary>A real torrent being downloaded from a seeder.</summary>
    public const string Downloading = "downloading";

    /// <summary>A real magnet whose metadata never came.</summary>
    public const string NoMetadata = "magnet-no-metadata";

    /// <summary>
    /// The recorded <c>torrent-get</c> reply of the given recording reduced to its last torrent (the one the recording is about; some
    /// recordings list the earlier ones too), with that torrent's values changed.
    /// </summary>
    public static string Reply(string recording, params (string Field, object? Value)[] edits)
    {
        var all = RecordedReplies.Transmission(recording).Find("torrent-get").Reply;
        var count = all.Json()["arguments"]!["torrents"]!.AsArray().Count;
        var edited = new List<(string, object?)>();
        if (count > 1)
        {
            // Keep only the torrent the recording is about: the last one.
            edited.Add(("arguments.torrents.0", all.Json()["arguments"]!["torrents"]![count - 1]!.DeepClone()));
            edited.Add(("^arguments.torrents", 1));
        }

        edited.AddRange(edits.Select(e => ("arguments.torrents.0." + e.Field, e.Value)));
        return all.BodyWith(edited.ToArray());
    }

    /// <summary>A Transmission answering from its recordings, with the torrent list as given.</summary>
    public static HttpMessageHandler Handler(string torrentGet, Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept = null)
        => RecordedReplies.Handler(
            "transmission",
            [RecordedReplies.Transmission("settings")],
            (request, body) =>
            {
                if (intercept?.Invoke(request, body) is { } own) return own;
                return body.Contains("torrent-get", StringComparison.Ordinal) ? RecordedReplies.Json(torrentGet) : null;
            });
}
