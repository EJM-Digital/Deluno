using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1224: SABnzbd 5.1.3 names the finished FILE as a job's <c>storage</c>, so removing a job must take the job's own
/// folder, and Transmission's empty <c>name__xxxxxx</c> folders go with a removed download.
/// #1225: a SABnzbd or NZBGet job with any data left never reads 100%.
/// </summary>
public sealed class FinishedFilesAndSmallSizesAreReadTests
{
    // The empty lists are SABnzbd's own replies when it holds nothing (recorded, #1237).
    private static string EmptyQueue => RecordedReplies.Sabnzbd("removed-without-data").Body("mode=queue&output", ("queue.slots", new System.Text.Json.Nodes.JsonArray()));
    private static string EmptyHistory => RecordedReplies.Sabnzbd("removed-with-data").Body("mode=history&output", ("history.slots", new System.Text.Json.Nodes.JsonArray()));

    /// <summary>SABnzbd's own queue reply with one job in it; the sizes and speed are the test's.</summary>
    private static string Queue(string filename, string mb, string mbLeft, string? speed = null)
        => RecordedReplies.Sabnzbd("downloading").Body(
            "mode=queue&output",
            ("^queue.slots", 1), ("queue.slots.0.nzo_id", "nzo1"), ("queue.slots.0.filename", filename), ("queue.slots.0.status", "Downloading"),
            ("queue.slots.0.mb", mb), ("queue.slots.0.mbleft", mbLeft), ("queue.speed", speed ?? "0 "));

    // ---- #1224 ----

    [Fact]
    public async Task A_sabnzbd_storage_that_is_the_finished_file_takes_the_whole_job_folder_with_it()
    {
        using var scratch = new Scratch();
        var folder = Path.Combine(scratch.Path, "Completed", "Movies", "Some.Film.2020.1080p");
        Directory.CreateDirectory(Path.Combine(folder, "Sample"));
        var film = Path.Combine(folder, "Some.Film.2020.1080p.mkv");
        File.WriteAllText(film, "film");
        File.WriteAllText(Path.Combine(folder, "Some.Film.2020.1080p.nfo"), "nfo");
        File.WriteAllText(Path.Combine(folder, "Poster.jpg"), "poster");
        File.WriteAllText(Path.Combine(folder, "Sample", "sample.mkv"), "sample");
        var sab = new FakeSab(History("Some.Film.2020.1080p", film));

        var result = await new SabnzbdDownloadClient(new Factory(new Handler(sab.Answer)))
            .ExecuteActionAsync(Client("sabnzbd", "SABnzbd", "key"), DownloadClientActions.DeleteWithData, "nzo1", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(Directory.Exists(folder));
        Assert.Null(result.FilesNote);
        Assert.Contains("along with its files", result.Message);
        Assert.True(Directory.Exists(Path.Combine(scratch.Path, "Completed", "Movies")), "never the category folder");
    }

    [Fact]
    public async Task A_finished_file_in_the_category_folder_is_not_a_job_folder_and_the_category_stays()
    {
        using var scratch = new Scratch();
        var category = Path.Combine(scratch.Path, "Completed", "Movies");
        Directory.CreateDirectory(category);
        var other = Path.Combine(category, "Another.Film.mkv");
        File.WriteAllText(other, "someone else's film");
        var sab = new FakeSab(History("Some.Film.2020.1080p", other));

        var result = await new SabnzbdDownloadClient(new Factory(new Handler(sab.Answer)))
            .ExecuteActionAsync(Client("sabnzbd", "SABnzbd", "key"), DownloadClientActions.DeleteWithData, "nzo1", CancellationToken.None);

        Assert.True(File.Exists(other));
        Assert.True(Directory.Exists(category));
        Assert.Contains("not named after this download", result.FilesNote);
        Assert.DoesNotContain("along with its files", result.Message);
    }

    [Fact]
    public void A_job_folder_is_one_named_after_the_job_below_a_completed_area_never_a_root_or_the_category()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var job = "Some.Film.2020.1080p";

        Assert.Equal(Path.Combine(root, "Done", "Movies", job),
            DownloadFolderRemoval.JobOwnFolder(Path.Combine(root, "Done", "Movies", job, job + ".mkv"), job));
        Assert.Null(DownloadFolderRemoval.JobOwnFolder(Path.Combine(root, "Done", "Movies", "other.mkv"), job));
        Assert.Null(DownloadFolderRemoval.JobOwnFolder(Path.Combine(root, job, job + ".mkv"), job));
        Assert.Null(DownloadFolderRemoval.JobOwnFolder(Path.Combine(root, job + ".mkv"), job));
        Assert.Null(DownloadFolderRemoval.JobOwnFolder(Path.Combine(root, "Done", "Movies", job, job + ".mkv"), null));
        Assert.Null(DownloadFolderRemoval.JobOwnFolder(Path.Combine(root, "Done", "Movies", job, job + ".mkv"), "  "));
    }

    [Fact]
    public void A_removal_that_leaves_something_on_disk_says_so_instead_of_reporting_nothing_left()
    {
        using var scratch = new Scratch();
        var folder = Path.Combine(scratch.Path, "Completed", "Some.Film.2020.1080p");
        Directory.CreateDirectory(folder);
        var film = Path.Combine(folder, "Some.Film.2020.1080p.mkv");
        File.WriteAllText(film, "film");

        // Holding the file open stops the delete on Windows; elsewhere the delete simply works. Either way the
        // answer must match what is on disk.
        using var hold = OperatingSystem.IsWindows() ? new FileStream(film, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var problem = DownloadFolderRemoval.Remove(film, "Some.Film.2020.1080p", onThisMachine: true);

        Assert.Equal(problem is null, !Directory.Exists(folder) && !File.Exists(film));
        if (hold is not null)
        {
            Assert.NotNull(problem);
        }
    }

    [Fact]
    public async Task Transmissions_empty_leftover_folders_go_when_a_download_is_removed_with_its_data()
    {
        using var scratch = new Scratch();
        var movies = Path.Combine(scratch.Path, "Completed", "Movies");
        var leftover = Path.Combine(movies, "Twicefilm.2020.1080p.mkv__qeVvY9");
        var notEmpty = Path.Combine(movies, "Twicefilm.2020.1080p.mkv__abcdef");
        var otherJob = Path.Combine(movies, "Another.Film.mkv__ZZZZZZ");
        Directory.CreateDirectory(leftover);
        Directory.CreateDirectory(notEmpty);
        Directory.CreateDirectory(otherJob);
        File.WriteAllText(Path.Combine(notEmpty, "keep.txt"), "x");

        var result = await new TransmissionDownloadClient(new Factory(new TransmissionFake(movies, "Twicefilm.2020.1080p.mkv")))
            .ExecuteActionAsync(Client("transmission", "Transmission", null), DownloadClientActions.DeleteWithData, "7", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(Directory.Exists(leftover));
        Assert.True(Directory.Exists(notEmpty), "a folder holding a file is not a leftover");
        Assert.True(Directory.Exists(otherJob), "another download's folder stays");
        Assert.True(Directory.Exists(movies));
    }

    [Fact]
    public async Task Transmission_removing_without_data_touches_no_folders()
    {
        using var scratch = new Scratch();
        var movies = Path.Combine(scratch.Path, "Movies");
        var leftover = Path.Combine(movies, "Film.mkv__qeVvY9");
        Directory.CreateDirectory(leftover);

        await new TransmissionDownloadClient(new Factory(new TransmissionFake(movies, "Film.mkv")))
            .ExecuteActionAsync(Client("transmission", "Transmission", null), DownloadClientActions.Delete, "7", CancellationToken.None);

        Assert.True(Directory.Exists(leftover));
    }

    // ---- #1225 ----

    [Theory]
    [InlineData("0.14", 140_000L)]
    [InlineData("0.49", 490_000L)]
    [InlineData("0.5", 500_000L)]
    [InlineData("12.75", 12_750_000L)]
    public async Task A_sabnzbd_job_with_a_fraction_of_a_megabyte_left_keeps_that_fraction_and_never_reads_100(string mbLeft, long leftBytes)
    {
        var queue = Queue("Some.Film", "1500.00", mbLeft);

        var item = Assert.Single((await Snapshot(queue)).Queue);

        Assert.Equal(1_500_000_000L, item.SizeBytes);
        Assert.Equal(1_500_000_000L - leftBytes, item.DownloadedBytes);
        Assert.True(item.Progress < 100d, $"read {item.Progress}");
        Assert.NotEqual(DownloadQueueStatuses.ImportReady, item.Status);
    }

    [Fact]
    public async Task A_sabnzbd_job_with_data_left_never_reads_100_even_when_the_job_is_tiny()
    {
        var queue = Queue("Tiny", "1000.0", "0.000002");

        var item = Assert.Single((await Snapshot(queue)).Queue);

        Assert.True(item.Progress < 100d);
    }

    [Theory]
    [InlineData("0.5 M", 0.5)]
    [InlineData("0.25 M", 0.25)]
    [InlineData("750 K", 0.75)]
    [InlineData("1.5 M", 1.5)]
    public async Task A_sabnzbd_speed_under_one_megabyte_a_second_is_not_rounded_away(string speed, double expectedMbps)
    {
        var queue = Queue("Some.Film", "100", "50", speed);

        var item = Assert.Single((await Snapshot(queue)).Queue);

        Assert.Equal(expectedMbps, item.SpeedMbps, 6);
    }

    [Fact]
    public async Task An_nzbget_job_is_sized_from_its_32_bit_halves_with_nothing_rounded()
    {
        // 5,000,000,000 bytes is 1 * 2^32 + 705,032,704; 100 bytes are left.
        // NZBGet's own queue listing, with the sizes set to the halves under test.
        var list = RecordedReplies.Nzbget("downloading").Body(
            "listgroups",
            ("^result", 1), ("result.0.NZBID", 42), ("result.0.NZBName", "Some.Film"), ("result.0.FileSizeHi", 1), ("result.0.FileSizeLo", 705032704L), ("result.0.FileSizeMB", 4768),
            ("result.0.RemainingSizeHi", 0), ("result.0.RemainingSizeLo", 100), ("result.0.RemainingSizeMB", 0));
        var handler = new Handler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return body.Contains("\"listgroups\"", StringComparison.Ordinal) ? Json(list)
                : body.Contains("\"history\"", StringComparison.Ordinal) ? RecordedReplies.Nzbget("removed-with-data").Response("\"history\"", ("result", new System.Text.Json.Nodes.JsonArray()))
                : RecordedReplies.Nzbget("settings").Response("\"status\"");
        });

        var snapshot = await new NzbGetDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("nzbget", "NZBGet", null), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(5_000_000_000L, item.SizeBytes);
        Assert.Equal(4_999_999_900L, item.DownloadedBytes);
        Assert.True(item.Progress < 100d);
    }

    [Theory]
    [InlineData(1L, 705032704L, null, 5_000_000_000L)]
    [InlineData(0L, null, 100L, 104_857_600L)]
    [InlineData(0L, 0L, null, 0L)]
    public void An_nzbget_size_reads_its_halves_first_then_whole_megabytes(long high, long? low, long? megabytes, long expected)
        => Assert.Equal(expected, NzbGetDownloadClient.SizeBytes(high, low, megabytes));

    // ---- helpers ----

    private static async Task<DownloadClientTelemetrySnapshot> Snapshot(string queueJson)
    {
        var handler = new Handler(request => Json(request.RequestUri!.Query.Contains("mode=history", StringComparison.Ordinal) ? EmptyHistory : queueJson));
        var snapshot = await new SabnzbdDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("sabnzbd", "SABnzbd", "key"), DateTimeOffset.UtcNow, CancellationToken.None);
        return snapshot!;
    }

    private static string History(string name, string storage)
        => RecordedReplies.Sabnzbd("finished-single-file").Body(
            "mode=history&output",
            ("^history.slots", 1), ("history.slots.0.nzo_id", "nzo1"), ("history.slots.0.name", name), ("history.slots.0.storage", storage), ("history.slots.0.path", "/downloads/incomplete/" + name));

    private sealed class FakeSab(string history)
    {
        private string _history = history;

        public HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("name=delete", StringComparison.Ordinal))
            {
                if (query.Contains("mode=queue", StringComparison.Ordinal)) return RecordedReplies.Sabnzbd("unknown-id").Response("mode=queue&name=delete");
                _history = EmptyHistory;
                return RecordedReplies.Sabnzbd("removed-with-data").Response("mode=history&name=delete");
            }

            return Json(query.Contains("mode=history", StringComparison.Ordinal) ? _history : EmptyQueue);
        }
    }

    /// <summary>Transmission holding one torrent until it is told to remove it.</summary>
    private sealed class TransmissionFake(string downloadDir, string name) : HttpMessageHandler
    {
        private bool _removed;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body.Contains("\"torrent-remove\"", StringComparison.Ordinal)) _removed = true;
            // Transmission's own replies: the torrent list with one torrent (only its id, name and folder are the test's), the list
            // once it is gone, and its answer to a removal.
            if (body.Contains("\"torrent-remove\"", StringComparison.Ordinal))
            {
                return RecordedReplies.Transmission("removed-with-data").Response("torrent-remove");
            }

            return body.Contains("\"torrent-get\"", StringComparison.Ordinal) && !_removed
                ? RecordedReplies.Transmission("finished-single-file").Response("torrent-get", ("arguments.torrents.0.id", 7), ("arguments.torrents.0.name", name), ("arguments.torrents.0.downloadDir", downloadDir))
                : RecordedReplies.Transmission("removed-with-data").Response("torrent-get");
        }
    }

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deluno-1224-" + Guid.NewGuid().ToString("n"));

        public Scratch() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    private static DownloadClientItem Client(string protocol, string name, string? secret)
        => new("client-1", name, protocol, "localhost", 8080, null, secret, "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
