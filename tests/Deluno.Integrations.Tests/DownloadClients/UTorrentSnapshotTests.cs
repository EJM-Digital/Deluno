using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// uTorrent's WebUI `list` status bitfield had never been read at all
/// (#728): status was guessed from progress/speed alone, `ErrorMessage` was a
/// hard-coded null, TV grabs defaulted to "movies" because nothing ever set a
/// label, and neither `SourcePath` nor `Ratio`/`SeedingMinutes` were ever
/// populated. These tests use recorded-shape `list`/`getprops` payloads for
/// each bit combination the audit found broken.
/// </summary>
public sealed class UTorrentSnapshotTests
{
    private const int Started = 0x1;
    private const int Checking = 0x2;
    private const int Error = 0x10;
    private const int Paused = 0x20;
    private const int Queued = 0x40;

    [Fact]
    public async Task Errored_torrent_is_stalled_not_queued()
    {
        // speed 0, matching a queued torrent exactly, except for the error bit.
        var item = await Snapshot(statusBits: Error, progress: 0, speed: 0, statusMessage: "Access is denied.");

        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
    }

    [Fact]
    public async Task Errored_torrent_carries_the_status_message_as_ErrorMessage()
    {
        var item = await Snapshot(statusBits: Error, progress: 0, speed: 0, statusMessage: "Access is denied.");

        Assert.Equal("Access is denied.", item.ErrorMessage);
    }

    [Fact]
    public async Task Non_errored_torrent_never_carries_an_error_message()
    {
        var item = await Snapshot(statusBits: Started, progress: 50, speed: 1_000_000, statusMessage: string.Empty);

        Assert.Null(item.ErrorMessage);
    }

    [Fact]
    public async Task Paused_torrent_reads_as_queued()
    {
        var item = await Snapshot(statusBits: Paused, progress: 40, speed: 0, statusMessage: string.Empty);

        Assert.Equal(DownloadQueueStatuses.Queued, item.Status);
    }

    [Fact]
    public async Task Queued_torrent_reads_as_queued()
    {
        var item = await Snapshot(statusBits: Queued, progress: 0, speed: 0, statusMessage: string.Empty);

        Assert.Equal(DownloadQueueStatuses.Queued, item.Status);
    }

    [Fact]
    public async Task Checking_torrent_reads_as_downloading_not_queued()
    {
        var item = await Snapshot(statusBits: Checking, progress: 30, speed: 0, statusMessage: string.Empty);

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    [Fact]
    public async Task Actively_downloading_torrent_reads_as_downloading()
    {
        var item = await Snapshot(statusBits: Started, progress: 30, speed: 2_000_000, statusMessage: string.Empty);

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    [Fact]
    public async Task Finished_torrent_reads_as_import_ready()
    {
        var item = await Snapshot(statusBits: Started, progress: 100, speed: 0, statusMessage: string.Empty);

        Assert.Equal(DownloadQueueStatuses.ImportReady, item.Status);
    }

    [Fact]
    public async Task Tv_label_infers_tv_media_type()
    {
        var item = await Snapshot(statusBits: Started, progress: 50, speed: 1_000_000, statusMessage: string.Empty, label: "tv");

        Assert.Equal("tv", item.MediaType);
    }

    [Fact]
    public async Task Movies_label_infers_movies_media_type()
    {
        var item = await Snapshot(statusBits: Started, progress: 50, speed: 1_000_000, statusMessage: string.Empty, label: "movies");

        Assert.Equal("movies", item.MediaType);
    }

    [Fact]
    public async Task Source_path_is_read_from_the_save_path_index()
    {
        var item = await Snapshot(statusBits: Started, progress: 50, speed: 1_000_000, statusMessage: string.Empty, savePath: "D:\\Downloads\\release");

        Assert.Equal("D:\\Downloads\\release", item.SourcePath);
    }

    [Fact]
    public async Task Ratio_is_read_from_the_per_mille_index()
    {
        var item = await Snapshot(statusBits: Started, progress: 100, speed: 0, statusMessage: string.Empty, ratioPerMille: 1500);

        Assert.Equal(1.5, item.Ratio);
    }

    [Fact]
    public async Task Seeding_minutes_are_read_from_getprops()
    {
        var item = await Snapshot(statusBits: Started, progress: 100, speed: 0, statusMessage: string.Empty, seedTimeSeconds: 3_600);

        Assert.Equal(60, item.SeedingMinutes);
    }

    private static async Task<DownloadQueueItem> Snapshot(
        int statusBits,
        double progress,
        double speed,
        string statusMessage,
        string label = "movies",
        string savePath = "D:\\Downloads\\release",
        int ratioPerMille = 0,
        long seedTimeSeconds = 0)
    {
        var hash = "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555";
        // uTorrent's list array is positional. Index: 0 hash, 1 status, 2 name,
        // 3 size, 4 percent*10, 7 ratio (per mille), 8 upload rate, 9 download
        // rate, 10 eta, 11 label, 12 peers, 21 status message, 23 added date,
        // 26 save path.
        var list = $$"""
        {"torrents":[[
            "{{hash}}", {{statusBits}}, "The.Show.S01E01.2023.720p.WEB-DL", 1000000, {{(progress * 10).ToString(System.Globalization.CultureInfo.InvariantCulture)}},
            0, 0, {{ratioPerMille}}, 1000000, {{speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, 60, "{{label}}", 3,
            0, 0, 0, 0, 0, 0, 0, 0,
            "{{statusMessage}}", 0, 1700000000, 0, 0, "{{savePath.Replace("\\", "\\\\")}}"
        ]]}
        """;
        var getprops = $$"""{"props":[{"hash":"{{hash}}","seed_time":{{seedTimeSeconds}}}]}""";

        var handler = new UTorrentHandler(list, getprops);
        var client = new UTorrentDownloadClient(() => handler);
        var snapshot = await client.GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Client()
        => new("utorrent-1", "uTorrent", "utorrent", "localhost", 8081, null, null, "http://localhost:8081/", "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class UTorrentHandler(string listResponse, string getPropsResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            var reply = uri.Contains("token.html", StringComparison.Ordinal)
                ? "<div id='token'>test-token</div>"
                : uri.Contains("action=getprops", StringComparison.Ordinal)
                    ? getPropsResponse
                    : uri.Contains("list=1", StringComparison.Ordinal)
                        ? listResponse
                        : "{}";
            var contentType = uri.Contains("token.html", StringComparison.Ordinal) ? "text/html" : "application/json";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, contentType) });
        }
    }
}
