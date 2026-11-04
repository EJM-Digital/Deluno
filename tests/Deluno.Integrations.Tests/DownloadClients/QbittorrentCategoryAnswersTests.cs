using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1269: what qBittorrent's category check says in each case, word for word and flag for flag (supported, found), replayed from
/// qBittorrent 5.2.1's recorded replies. The branch tests only looked at the status; the words are what a person reads.
/// </summary>
public sealed class QbittorrentCategoryAnswersTests
{
    private const string Movies = "recorded-movies";

    private static readonly string[] States = ["sign-in", "version", "settings", "idle", "trackers"];

    private static DownloadClientItem Item() => Client("qbittorrent", "deluno", "secret");

    private static HttpResponseMessage Status(int code) => new((HttpStatusCode)code) { Content = new StringContent(string.Empty) };

    private static bool Is(HttpRequestMessage request, string tail) => request.RequestUri!.AbsolutePath.EndsWith(tail, StringComparison.Ordinal);

    private static QbittorrentDownloadClient Rig(Func<HttpRequestMessage, string, HttpResponseMessage?> answer)
    {
        var handler = new Calls("qbittorrent", States.Select(RecordedReplies.Qbittorrent).ToArray(), answer).Build();
        return new QbittorrentDownloadClient(() => handler);
    }

    private static string Categories(params (string Path, object? Value)[] edits)
        => RecordedReplies.Qbittorrent("settings").Body("torrents/categories", [("><scrubbed>-movies", Movies), .. edits]);

    private static QbittorrentDownloadClient WithCategories(Func<HttpRequestMessage, string, HttpResponseMessage?>? more = null, params (string Path, object? Value)[] edits)
    {
        var body = Categories(edits);
        return Rig((request, text) => Is(request, "torrents/categories") ? RecordedReplies.Json(body) : more?.Invoke(request, text));
    }

    [Fact]
    public async Task A_blank_category_and_a_missing_address_are_settings_to_fix_not_a_missing_category()
    {
        var client = Rig((_, _) => null);

        var blank = await client.CheckCategoryAsync(Item(), "  ", CancellationToken.None);
        var noAddress = await client.CheckCategoryAsync(Item() with { Host = null, EndpointUrl = null }, Movies, CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Configuration, blank.Status);
        Assert.Equal("Enter a category before checking it.", blank.Message);
        Assert.True(blank.Supported);
        Assert.False(blank.Found);
        Assert.Equal(DownloadClientCategoryStatuses.Configuration, noAddress.Status);
        Assert.Equal("Add the qBittorrent address before checking a category.", noAddress.Message);
        Assert.True(noAddress.Supported);
        Assert.False(noAddress.Found);
    }

    [Fact]
    public async Task A_client_with_no_category_list_is_unsupported_and_one_that_fails_it_is_unsupported_for_no_reason()
    {
        var unsupported = await Rig((request, _) => Is(request, "torrents/categories") ? Status(404) : null).CheckCategoryAsync(Item(), Movies, CancellationToken.None);
        var failing = await Rig((request, _) => Is(request, "torrents/categories") ? Status(503) : null).CheckCategoryAsync(Item(), Movies, CancellationToken.None);

        Assert.Equal("This qBittorrent version does not expose its category list. You can still use the category, but Deluno cannot verify it.", unsupported.Message);
        Assert.False(unsupported.Supported);
        Assert.False(unsupported.Found);
        Assert.Equal(DownloadClientCategoryStatuses.Unreachable, failing.Status);
        Assert.Equal("qBittorrent returned 503 while checking its categories.", failing.Message);
        Assert.True(failing.Supported);
        Assert.False(failing.Found);
    }

    [Fact]
    public async Task A_category_that_is_not_there_names_itself_and_says_where_to_create_it()
    {
        var result = await WithCategories().CheckCategoryAsync(Item(), "other", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Missing, result.Status);
        Assert.Equal("qBittorrent does not have a category named other. Create it there before using it in Deluno.", result.Message);
        Assert.True(result.Supported);
        Assert.False(result.Found);
    }

    [Fact]
    public async Task A_category_with_a_save_path_says_where_it_saves_and_is_found()
    {
        var absolute = WithCategories(null, ("recorded-movies.savePath", "/data/movies"));
        var result = await absolute.CheckCategoryAsync(Item(), Movies, CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, result.Status);
        Assert.Equal($"qBittorrent has a category named {Movies}, saving to /data/movies.", result.Message);
        Assert.True(result.Supported);
        Assert.True(result.Found);
        Assert.Equal("/data/movies", result.SavePath);
    }

    [Fact]
    public async Task A_category_without_a_save_path_is_found_but_has_no_save_path()
    {
        var result = await WithCategories(null, ("recorded-movies.savePath", "")).CheckCategoryAsync(Item(), Movies, CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Configuration, result.Status);
        Assert.StartsWith($"qBittorrent has a category named {Movies}, but it has no save path, so downloads will go wherever qBittorrent defaults to", result.Message);
        Assert.True(result.Supported);
        Assert.True(result.Found);
        Assert.Null(result.SavePath);
    }

    [Fact]
    public async Task A_relative_save_path_is_joined_to_the_default_one_only_when_the_default_is_read()
    {
        var asked = 0;
        Func<HttpRequestMessage, string, HttpResponseMessage?> defaultPath = (request, _) =>
        {
            if (!Is(request, "app/defaultSavePath")) return null;
            asked++;
            return RecordedReplies.Json("/data/downloads");
        };

        var joined = await WithCategories(defaultPath, ("recorded-movies.savePath", "movies")).CheckCategoryAsync(Item(), Movies, CancellationToken.None);
        // A full path is never joined, so the default is never asked for.
        var before = asked;
        var full = await WithCategories(defaultPath, ("recorded-movies.savePath", "/data/movies")).CheckCategoryAsync(Item(), Movies, CancellationToken.None);

        Assert.Equal(1, before);
        Assert.Equal(before, asked);
        Assert.Equal("/data/downloads/movies", joined.SavePath?.Replace('\\', '/'));
        Assert.Equal("/data/movies", full.SavePath);
        // A default that cannot be read leaves the relative path as it is.
        var unread = await WithCategories((request, _) => Is(request, "app/defaultSavePath") ? Status(500) : null, ("recorded-movies.savePath", "movies"))
            .CheckCategoryAsync(Item(), Movies, CancellationToken.None);
        Assert.Equal("movies", unread.SavePath);
    }

    [Fact]
    public async Task A_client_that_sends_every_torrent_to_one_folder_says_so_whatever_the_category_says()
    {
        var item = Item() with { Location = "  /data/everything  " };

        var result = await WithCategories(null, ("recorded-movies.savePath", "/data/movies")).CheckCategoryAsync(item, Movies, CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, result.Status);
        Assert.Equal($"Deluno sends every torrent to /data/everything in qBittorrent, whatever its {Movies} category says.", result.Message);
        Assert.True(result.Supported);
        Assert.True(result.Found);
        Assert.Equal("/data/everything", result.SavePath);
    }

    [Fact]
    public async Task A_client_that_cannot_be_reached_says_why()
    {
        var down = Rig((request, _) => throw new HttpRequestException("connection refused"));

        var result = await down.CheckCategoryAsync(Item(), Movies, CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Unreachable, result.Status);
        Assert.Equal("Deluno could not reach qBittorrent to check its categories: connection refused", result.Message);
        Assert.True(result.Supported);
        Assert.False(result.Found);
    }
}
