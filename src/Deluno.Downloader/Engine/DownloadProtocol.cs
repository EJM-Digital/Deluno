namespace Deluno.Downloader.Engine;

/// <summary>
/// The two protocols the built-in engine handles. Each is a separate
/// implementation in <c>Deluno.Downloader.Nzb</c> and
/// <c>Deluno.Downloader.Torrent</c>, but share lifecycle, persistence,
/// extraction, and post-processing.
///
/// String values match the <c>jobs.protocol</c> CHECK constraint in the
/// SQLite schema and the <c>protocol</c> field used by the Integrations
/// adapters ("deluno-nzb" / "deluno-torrent" externally; "nzb" / "torrent"
/// in our own DB column).
/// </summary>
public enum DownloadProtocol
{
    Nzb,
    Torrent,
}

public static class DownloadProtocolExtensions
{
    public static string ToDbValue(this DownloadProtocol protocol) => protocol switch
    {
        DownloadProtocol.Nzb => "nzb",
        DownloadProtocol.Torrent => "torrent",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null)
    };

    public static DownloadProtocol FromDbValue(string value) => value switch
    {
        "nzb" => DownloadProtocol.Nzb,
        "torrent" => DownloadProtocol.Torrent,
        _ => throw new ArgumentException($"Unknown protocol value '{value}'.", nameof(value))
    };
}
