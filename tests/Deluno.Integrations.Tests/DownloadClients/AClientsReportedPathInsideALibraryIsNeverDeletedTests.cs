using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Tests.Recorded;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1328: when Deluno finishes a client's remove-with-data itself (SABnzbd and NZBGet leave the finished folder,
/// Transmission leaves empty folders, rTorrent has no delete-with-data at all), the folder or file the client reports is
/// never deleted when it is, holds or sits inside any library root. A client category folder pointed at a library, or a
/// save path inside one, must not cost the person their media. Each test is real-disk: a real library folder holding a
/// real file, the client's own recorded reply with only the reported path swapped in, and the file still there after.
/// A control per client shows the same removal still happens when the path is outside every library.
/// (rTorrent's tests sit in RtorrentDownloadClientTests, beside its hand-built reply.)
/// </summary>
public sealed class AClientsReportedPathInsideALibraryIsNeverDeletedTests
{
    private const string Job = "Some.Film.2020.1080p";

    // ---- the shared rule, for the folder is, holds, or sits inside a library root ----

    [Theory]
    [InlineData("inside")]
    [InlineData("is")]
    [InlineData("holds")]
    public void A_path_that_is_holds_or_sits_inside_a_library_root_is_never_removed(string relation)
    {
        using var scratch = new Scratch();
        var library = Directory.CreateDirectory(Path.Combine(scratch.Path, "Media", "Movies")).FullName;
        var jobFolder = Directory.CreateDirectory(Path.Combine(library, Job)).FullName;
        var film = Path.Combine(jobFolder, Job + ".mkv");
        File.WriteAllText(film, "film");
        var reported = relation switch { "inside" => jobFolder, "is" => library, _ => Path.GetDirectoryName(library)! };

        var problem = DownloadFolderRemoval.Remove(reported, Job, onThisMachine: true, [Library(library)]);

        Assert.True(File.Exists(film), "the film in the library is untouched");
        Assert.Contains("inside a library, or holds one", problem);
    }

    [Fact]
    public void Libraries_that_could_not_be_read_mean_nothing_is_deleted()
    {
        using var scratch = new Scratch();
        var folder = Directory.CreateDirectory(Path.Combine(scratch.Path, "Completed", Job)).FullName;
        File.WriteAllText(Path.Combine(folder, Job + ".mkv"), "film");

        var problem = DownloadFolderRemoval.Remove(folder, Job, onThisMachine: true, libraries: null);

        Assert.True(Directory.Exists(folder));
        Assert.Contains("could not check", problem);
    }

    [Fact]
    public async Task A_library_list_that_throws_is_read_as_unreadable_not_as_empty()
    {
        var repository = new Mock<ILibrariesRepository>();
        repository.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));

        Assert.Null(await DownloadFolderRemoval.ReadLibrariesAsync(repository.Object, CancellationToken.None));
    }

    // ---- SABnzbd ----

    [Fact]
    public async Task Sabnzbd_a_finished_folder_inside_a_library_is_left_with_its_film_and_said_plainly()
    {
        using var scratch = new Scratch();
        var library = Directory.CreateDirectory(Path.Combine(scratch.Path, "Media", "Movies")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(library, Job)).FullName;
        var film = Path.Combine(folder, Job + ".mkv");
        File.WriteAllText(film, "film");

        var result = await SabnzbdRemove(film, Libraries(Library(library)));

        Assert.True(File.Exists(film));
        Assert.Contains("inside a library, or holds one", result.FilesNote);
        Assert.Contains("its files are still on disk", result.Message);
    }

    [Fact]
    public async Task Sabnzbd_a_category_folder_that_is_the_library_root_keeps_everything_in_it()
    {
        using var scratch = new Scratch();
        var library = Directory.CreateDirectory(Path.Combine(scratch.Path, "Media", "Movies")).FullName;
        var film = Path.Combine(library, Job + ".mkv");
        File.WriteAllText(film, "film");

        var result = await SabnzbdRemove(film, Libraries(Library(library)));

        Assert.True(File.Exists(film));
        Assert.Contains("inside a library, or holds one", result.FilesNote);
    }

    [Fact]
    public async Task Sabnzbd_control_the_same_folder_outside_every_library_is_still_removed()
    {
        using var scratch = new Scratch();
        var library = Directory.CreateDirectory(Path.Combine(scratch.Path, "Media", "Movies")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(scratch.Path, "Completed", "Movies", Job)).FullName;
        var film = Path.Combine(folder, Job + ".mkv");
        File.WriteAllText(film, "film");

        var result = await SabnzbdRemove(film, Libraries(Library(library)));

        Assert.False(Directory.Exists(folder));
        Assert.Null(result.FilesNote);
    }

    [Fact]
    public async Task Sabnzbd_libraries_that_cannot_be_read_mean_the_folder_is_left()
    {
        using var scratch = new Scratch();
        var folder = Directory.CreateDirectory(Path.Combine(scratch.Path, "Completed", "Movies", Job)).FullName;
        var film = Path.Combine(folder, Job + ".mkv");
        File.WriteAllText(film, "film");
        var broken = new Mock<ILibrariesRepository>();
        broken.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));

        var result = await SabnzbdRemove(film, broken.Object);

        Assert.True(File.Exists(film));
        Assert.Contains("could not check", result.FilesNote);
    }

    private static async Task<DownloadClientActionResult> SabnzbdRemove(string storage, ILibrariesRepository libraries)
    {
        var history = RecordedReplies.Sabnzbd("finished-single-file").Body(
            "mode=history&output",
            ("^history.slots", 1), ("history.slots.0.nzo_id", "nzo1"), ("history.slots.0.name", Job), ("history.slots.0.storage", storage), ("history.slots.0.path", "/downloads/incomplete/" + Job));
        var emptyQueue = RecordedReplies.Sabnzbd("removed-without-data").Body("mode=queue&output", ("queue.slots", new System.Text.Json.Nodes.JsonArray()));
        var emptyHistory = RecordedReplies.Sabnzbd("removed-with-data").Body("mode=history&output", ("history.slots", new System.Text.Json.Nodes.JsonArray()));
        var current = history;
        var handler = new Handler(request =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("name=delete", StringComparison.Ordinal))
            {
                if (query.Contains("mode=queue", StringComparison.Ordinal)) return RecordedReplies.Sabnzbd("unknown-id").Response("mode=queue&name=delete");
                current = emptyHistory;
                return RecordedReplies.Sabnzbd("removed-with-data").Response("mode=history&name=delete");
            }

            return Json(query.Contains("mode=history", StringComparison.Ordinal) ? current : emptyQueue);
        });

        return await new SabnzbdDownloadClient(new Factory(handler), libraries: libraries)
            .ExecuteActionAsync(ClientItem("sabnzbd", "SABnzbd", "key"), DownloadClientActions.DeleteWithData, "nzo1", CancellationToken.None);
    }

    // ---- NZBGet ----

    [Fact]
    public async Task Nzbget_a_final_folder_inside_a_library_is_left_with_its_film_and_said_plainly()
    {
        using var scratch = new Scratch();
        var library = Directory.CreateDirectory(Path.Combine(scratch.Path, "Media", "Movies")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(library, "Recorded.Film")).FullName;
        var film = Path.Combine(folder, "Recorded.Film.mkv");
        File.WriteAllText(film, "film");

        var result = await NzbgetRemove(folder, Libraries(Library(library)));

        Assert.True(File.Exists(film));
        Assert.Contains("inside a library, or holds one", result.FilesNote);
    }

    [Fact]
    public async Task Nzbget_control_the_same_folder_outside_every_library_is_still_removed()
    {
        using var scratch = new Scratch();
        var library = Directory.CreateDirectory(Path.Combine(scratch.Path, "Media", "Movies")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(scratch.Path, "Completed", "Movies", "Recorded.Film")).FullName;
        File.WriteAllText(Path.Combine(folder, "Recorded.Film.mkv"), "film");

        var result = await NzbgetRemove(folder, Libraries(Library(library)));

        Assert.False(Directory.Exists(folder));
        Assert.Null(result.FilesNote);
    }

    private static async Task<DownloadClientActionResult> NzbgetRemove(string finalDir, ILibrariesRepository libraries)
    {
        var deleted = false;
        var handler = new Handler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            if (body.Contains("\"editqueue\"", StringComparison.Ordinal))
            {
                deleted = true;
                return RecordedReplies.Nzbget("removed-without-data").Response("editqueue");
            }

            if (body.Contains("\"listgroups\"", StringComparison.Ordinal)) return RecordedReplies.Nzbget("removed-without-data").Response("listgroups");
            if (body.Contains("\"history\"", StringComparison.Ordinal))
            {
                return deleted
                    ? RecordedReplies.Nzbget("removed-with-data").Response("history\"")
                    : RecordedReplies.Nzbget("finished-single-file").Response("history\"", ("result.0.FinalDir", finalDir));
            }

            throw new InvalidOperationException("NZBGet was asked something the recordings do not answer: " + body);
        });

        return await new NzbGetDownloadClient(new Factory(handler), TimeSpan.Zero, libraries: libraries)
            .ExecuteActionAsync(ClientItem("nzbget", "NZBGet", null), DownloadClientActions.DeleteWithData, "26", CancellationToken.None);
    }

    // ---- Transmission ----

    [Fact]
    public async Task Transmission_a_download_folder_that_is_a_library_keeps_even_its_empty_folders()
    {
        using var scratch = new Scratch();
        var library = Directory.CreateDirectory(Path.Combine(scratch.Path, "Media", "Movies")).FullName;
        var leftover = Directory.CreateDirectory(Path.Combine(library, "Twicefilm.2020.1080p.mkv__qeVvY9")).FullName;

        var result = await TransmissionRemove(library, Libraries(Library(library)));

        Assert.True(Directory.Exists(leftover));
        Assert.Contains("inside a library, or holds one", result.FilesNote);
    }

    [Fact]
    public async Task Transmission_control_an_empty_leftover_folder_outside_every_library_still_goes()
    {
        using var scratch = new Scratch();
        var library = Directory.CreateDirectory(Path.Combine(scratch.Path, "Media", "Movies")).FullName;
        var movies = Path.Combine(scratch.Path, "Completed", "Movies");
        var leftover = Directory.CreateDirectory(Path.Combine(movies, "Twicefilm.2020.1080p.mkv__qeVvY9")).FullName;

        var result = await TransmissionRemove(movies, Libraries(Library(library)));

        Assert.False(Directory.Exists(leftover));
        Assert.Null(result.FilesNote);
    }

    private static async Task<DownloadClientActionResult> TransmissionRemove(string downloadDir, ILibrariesRepository libraries)
    {
        var removed = false;
        var handler = new AsyncHandler(async request =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
            if (body.Contains("\"torrent-remove\"", StringComparison.Ordinal))
            {
                removed = true;
                return RecordedReplies.Transmission("removed-with-data").Response("torrent-remove");
            }

            return body.Contains("\"torrent-get\"", StringComparison.Ordinal) && !removed
                ? RecordedReplies.Transmission("finished-single-file").Response("torrent-get", ("arguments.torrents.0.id", 7), ("arguments.torrents.0.name", "Twicefilm.2020.1080p.mkv"), ("arguments.torrents.0.downloadDir", downloadDir))
                : RecordedReplies.Transmission("removed-with-data").Response("torrent-get");
        });

        return await new TransmissionDownloadClient(new Factory(handler), libraries)
            .ExecuteActionAsync(ClientItem("transmission", "Transmission", null), DownloadClientActions.DeleteWithData, "7", CancellationToken.None);
    }

    // ---- helpers ----

    private static LibraryItem Library(string root)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            "library-1", "Movies", "movies", "main", root, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    private static ILibrariesRepository Libraries(params LibraryItem[] libraries)
    {
        var repository = new Mock<ILibrariesRepository>();
        repository.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(libraries);
        return repository.Object;
    }

    private static DownloadClientItem ClientItem(string protocol, string name, string? secret)
        => new("client-1", name, protocol, "localhost", 8080, null, secret, "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deluno-1328-" + Guid.NewGuid().ToString("n"));

        public Scratch() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
