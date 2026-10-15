using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The "Watch indexers for new releases" pass (RssSyncService) against what Jackett 0.24.2850 really listed for the public tracker
/// YTS (tests/fixtures/recorded/jackett: its newest releases, and its refusal of a wrong key), over a real loopback port, on the real
/// databases: a new release for a film that is wanted searches for that film at once, a release for nothing wanted searches for
/// nothing, a release is acted on once, and an indexer that could not be read keeps its place for the next pass.
/// </summary>
public sealed class RssSyncAgainstRecordedJackettTests
{
    private const string JackettPath = "/api/v2.0/indexers/yts/results/torznab/api";

    private sealed record Scene(string LibraryId, string CaterpillarId, string SintelId);

    private static async Task<Scene> SeedAsync(ApplicationTestHost app, string indexerUrl)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root, autoSearchEnabled = true, missingSearchEnabled = true });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var indexer = await connections.CreateIndexerAsync(
            new CreateIndexerRequest("Jackett YTS", "torznab", "public", indexerUrl, "k-secret-1", 10, "2000", "", "movies", true),
            CancellationToken.None);
        await connections.UpdateIndexerHealthAsync(indexer.Id, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = new[] { new { indexerId = indexer.Id, priority = 1 } },
            downloadClients = Array.Empty<object>()
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());

        async Task<string> FilmAsync(string title, int year)
        {
            var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true, libraryId, searchForMissingNow = false });
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
            return (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        return new Scene(libraryId, await FilmAsync("Caterpillar", 2026), await FilmAsync("Sintel", 2010));
    }

    private static async Task<RssSyncResult> SyncAsync(ApplicationTestHost app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RssSyncService>().SyncAsync(CancellationToken.None);
    }

    private static async Task<string[]> QueuedSearchPayloadsAsync(ApplicationTestHost app)
    {
        using var scope = app.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<Deluno.Jobs.Data.IJobQueueRepository>().ListAsync(200, CancellationToken.None))
            .Where(job => job.JobType.Contains("search", StringComparison.OrdinalIgnoreCase))
            .Select(job => job.PayloadJson ?? string.Empty)
            .ToArray();
    }

    [Fact]
    public async Task A_new_release_for_a_wanted_film_searches_for_that_film_now_and_a_release_for_nothing_wanted_searches_for_nothing()
    {
        await using var jackett = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-feed"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SeedAsync(app, jackett.BaseUrl + JackettPath);

        var result = await SyncAsync(app);

        Assert.Equal(1, result.IndexersRead);
        Assert.True(result.NewReleases >= 1, "the recorded feed lists releases the pass has not seen");
        var search = Assert.Single(result.SearchesQueued);
        Assert.Contains("Caterpillar", search, StringComparison.Ordinal);
        Assert.DoesNotContain(result.SearchesQueued, queued => queued.Contains("Sintel", StringComparison.Ordinal));
        var payloads = await QueuedSearchPayloadsAsync(app);
        Assert.Contains(payloads, payload => payload.Contains(scene.CaterpillarId, StringComparison.Ordinal));
        Assert.DoesNotContain(payloads, payload => payload.Contains(scene.SintelId, StringComparison.Ordinal));
        var asked = Assert.Single(jackett.Requests, request => request.Contains("t=search", StringComparison.Ordinal));
        Assert.StartsWith(JackettPath + "?", asked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_release_is_acted_on_once_not_on_every_pass()
    {
        await using var jackett = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-feed"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app, jackett.BaseUrl + JackettPath);
        Assert.Single((await SyncAsync(app)).SearchesQueued);
        var queuedAfterFirst = (await QueuedSearchPayloadsAsync(app)).Length;

        var second = await SyncAsync(app);

        Assert.Equal(1, second.IndexersRead);
        Assert.Equal(0, second.NewReleases);
        Assert.Empty(second.SearchesQueued);
        Assert.Equal(queuedAfterFirst, (await QueuedSearchPayloadsAsync(app)).Length);
    }

    [Fact]
    public async Task An_indexer_that_refuses_the_key_is_not_read_and_keeps_its_place_so_the_next_pass_picks_up_what_this_one_could_not_see()
    {
        await using var jackett = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-feed"), RecordedReplies.Jackett("caps"));
        jackett.AnswerEverythingWith = RecordedReplies.Jackett("caps-wrong-api-key").Single.Reply;
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app, jackett.BaseUrl + JackettPath);

        var refused = await SyncAsync(app);

        Assert.Equal(0, refused.IndexersRead);
        Assert.Empty(refused.SearchesQueued);
        Assert.Empty(await QueuedSearchPayloadsAsync(app));

        jackett.AnswerEverythingWith = null;
        var later = await SyncAsync(app);

        Assert.Equal(1, later.IndexersRead);
        Assert.Contains("Caterpillar", Assert.Single(later.SearchesQueued), StringComparison.Ordinal);
    }
}
