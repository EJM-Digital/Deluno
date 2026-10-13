using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Deluge 2.2.0's own recorded replies (#1237) for a test of how Deluno reads one torrent. The torrent list is the recorded
/// reply of a real torrent (finished and seeding, or downloading), changed only in the values the test is about; every other
/// call (sign-in, label options, core settings) is answered by its recording.
/// </summary>
internal static class RecordedDelugeRig
{
    /// <summary>The recorded list of one finished, seeding torrent, with the given values of that torrent changed ("state", "save_path"...).</summary>
    public static string Finished(params (string Field, object? Value)[] edits)
        => RecordedReplies.Deluge("finished-single-file").Body("web.update_ui", Torrent(edits));

    /// <summary>The recorded list of one torrent still downloading, with the given values of that torrent changed.</summary>
    public static string Downloading(params (string Field, object? Value)[] edits)
        => RecordedReplies.Deluge("downloading").Body("web.update_ui", Torrent(edits));

    /// <summary>The recorded list of a magnet with no metadata yet, with the given values changed.</summary>
    public static string NoMetadata(params (string Field, object? Value)[] edits)
        => RecordedReplies.Deluge("magnet-no-metadata").Body("web.update_ui", Torrent(edits));

    private static (string, object?)[] Torrent((string Field, object? Value)[] edits)
        => edits.Select(e => ("result.torrents.*." + e.Field, e.Value)).ToArray();

    /// <summary>A Deluge answering every call from its recordings, with the torrent list (and optionally the label's options and core settings) as given.</summary>
    public static HttpMessageHandler Handler(string updateUi, string? labelOptions = null, string? coreConfig = null, Action<string>? onCall = null, string? labels = null)
        => RecordedReplies.Handler(
            "deluge",
            [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("settings"), RecordedReplies.Deluge("label-set")],
            (_, body) =>
            {
                onCall?.Invoke(body);
                // Unless a test says otherwise, neither the label nor Deluge itself moves a finished torrent anywhere.
                if (body.Contains("web.update_ui", StringComparison.Ordinal)) return RecordedReplies.Json(updateUi);
                if (labels is not null && body.Contains("label.get_labels", StringComparison.Ordinal)) return RecordedReplies.Json(labels);
                if (body.Contains("label.get_options", StringComparison.Ordinal)) return RecordedReplies.Json(labelOptions ?? LabelOptions(false, string.Empty));
                if (body.Contains("core.get_config_values", StringComparison.Ordinal)) return RecordedReplies.Json(coreConfig ?? CoreConfig(false, string.Empty));
                return null;
            });

    /// <summary>The recorded list of labels, with the two Deluno made (their names are scrubbed in the recording) named as a person would.</summary>
    public static string Labels()
        => RecordedReplies.Deluge("settings").Body("label.get_labels", ("result.0", "movies"), ("result.1", "tv"));

    /// <summary>A Deluge answering the folder check from its recordings: its core folders as recorded (the download location as given), its labels, and the label's options.</summary>
    public static HttpMessageHandler FolderCheck(bool labelMoves, string labelFolder, string downloadLocation = @"D:\Incomplete\Deluge")
        => Handler(
            string.Empty,
            LabelOptions(labelMoves, labelFolder),
            RecordedReplies.Deluge("settings").Body("core.get_config_values", ("result.download_location", downloadLocation)),
            labels: Labels());

    /// <summary>The label's recorded options, with the label moving finished downloads to the folder (or not).</summary>
    public static string LabelOptions(bool moves, string folder)
        => RecordedReplies.Deluge("label-set").Body(
            "label.get_options",
            ("result.apply_move_completed", moves), ("result.move_completed", moves), ("result.move_completed_path", moves ? folder : string.Empty));

    /// <summary>The recorded core settings, with Deluge's own move-on-completion switched as given.</summary>
    public static string CoreConfig(bool moves, string folder)
        => RecordedReplies.Deluge("settings").Body(
            "core.get_config_values",
            ("result.move_completed", moves), ("result.move_completed_path", moves ? folder : string.Empty));
}
