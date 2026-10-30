using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Indexer History (the combined feed) after a real film search, a real hand-picked send and a real "watch indexers for new releases"
/// pass, against Jackett 0.24.2850's and qBittorrent 5.2.1's recorded replies: the search shows as a search and the send as a grab, each
/// with its outcome and release, and the RSS pass writes its own event. Refs #1296.
/// </summary>
public sealed class ASearchAndAGrabShowInTheIndexerHistoryFeedTests
{
    [Fact]
    public async Task A_film_search_shows_as_a_search_a_hand_picked_send_as_a_grab_each_with_its_outcome_and_the_grab_with_its_release()
    {
        await using var rig = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.RigAsync();
        var filmId = (await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.PostOkAsync(rig.App, "/api/movies",
            new { title = "Sintel", releaseYear = 2010, imdbId = "tt1727587", monitored = true, libraryId = rig.FilmLibrary })).GetProperty("id").GetString()!;

        var preview = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.PostOkAsync(rig.App, $"/api/movies/{filmId}/search?mode=preview");
        var candidate = Assert.Single(preview.GetProperty("candidates").EnumerateArray());
        var release = candidate.GetProperty("releaseName").GetString()!;
        await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.PostOkAsync(rig.App, $"/api/movies/{filmId}/search");
        await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.PostOkAsync(rig.App, $"/api/movies/{filmId}/grab", new
        {
            releaseName = release,
            indexerId = candidate.GetProperty("indexerId").GetString(),
            indexerName = candidate.GetProperty("indexerName").GetString(),
            downloadUrl = candidate.GetProperty("downloadUrl").GetString(),
            candidateQuality = candidate.GetProperty("quality").GetString(),
            sizeBytes = 143151596L,
            force = true,
            overrideReason = "You chose this release."
        });

        var feed = await rig.App.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history?pageSize=100");
        var items = feed.GetProperty("items").EnumerateArray().ToArray();

        var searches = items.Where(item => item.GetProperty("eventType").GetString() == "search").ToArray();
        Assert.Equal(2, searches.Length);
        Assert.All(searches, search =>
        {
            Assert.Equal("movie", search.GetProperty("relatedEntityType").GetString());
            Assert.Equal(filmId, search.GetProperty("relatedEntityId").GetString());
            Assert.Equal("checked", search.GetProperty("outcome").GetString());
            Assert.Equal("manual search: checked", search.GetProperty("message").GetString());
        });

        var grab = Assert.Single(items, item => item.GetProperty("eventType").GetString() == "grab");
        Assert.Equal("matched", grab.GetProperty("outcome").GetString());
        Assert.Equal(filmId, grab.GetProperty("relatedEntityId").GetString());
                Assert.Contains(release, grab.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_RSS_pass_over_a_recorded_feed_writes_its_own_event_naming_the_indexer_and_what_it_found()
    {
        await using var jackett = await RecordedIndexerServer.StartAsync(
            Deluno.Tests.Recorded.RecordedReplies.Jackett("search-feed"), Deluno.Tests.Recorded.RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root, autoSearchEnabled = true, missingSearchEnabled = true });
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var connections = app.Services.GetRequiredService<Deluno.Connections.Data.IConnectionsRepository>();
        var indexer = await connections.CreateIndexerAsync(
            new Deluno.Connections.Contracts.CreateIndexerRequest("Jackett YTS", "torznab", "public", jackett.BaseUrl + "/api/v2.0/indexers/yts/results/torznab/api", "k-secret-1", 10, "2000", "", "movies", true),
            CancellationToken.None);
        await connections.UpdateIndexerHealthAsync(indexer.Id, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        (await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new { sources = new[] { new { indexerId = indexer.Id, priority = 1 } }, downloadClients = Array.Empty<object>() })).EnsureSuccessStatusCode();
        (await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Caterpillar", releaseYear = 2026, monitored = true, libraryId, searchForMissingNow = false })).EnsureSuccessStatusCode();

        using (var scope = app.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<RssSyncService>().SyncAsync(CancellationToken.None);
            Assert.Equal(1, result.IndexersRead);
        }

        var feed = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history?eventType=rss-sync");
        var rss = Assert.Single(feed.GetProperty("items").EnumerateArray());
        Assert.Equal("Jackett YTS", rss.GetProperty("indexerName").GetString());
        Assert.Equal("read", rss.GetProperty("outcome").GetString());
        Assert.Matches(@"^Read Jackett YTS's RSS feed\. \d+ new releases?\.$", rss.GetProperty("message").GetString()!);
    }
}
