using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// GET /api/v1/download-dispatches lists what Deluno sent to the download clients, newest first, and every filter in the query
/// string narrows it: media type, entity, library, client, grab and import status, the grab time window, and archived rows only
/// when asked for.
/// </summary>
public sealed class TheDispatchListFiltersTests
{
    private static async Task<string> LibraryAsync(ApplicationTestHost app, string name, string mediaType)
    {
        var library = await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                name, mediaType, "library", Path.Combine(app.DataRoot, name), Path.Combine(app.DataRoot, name + "-downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 36, 25),
            CancellationToken.None);
        return library.Id;
    }

    private static async Task<string[]> TitlesAsync(ApplicationTestHost app, string query)
    {
        var page = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/download-dispatches" + query);
        return page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("releaseName").GetString()!).ToArray();
    }

    [Fact]
    public async Task Each_filter_in_the_query_narrows_the_list_and_archived_rows_come_only_when_asked_for()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var queue = app.Services.GetRequiredService<IJobQueueRepository>();
        var films = await LibraryAsync(app, "Films", "movies");
        var shows = await LibraryAsync(app, "Shows", "tv");
        await queue.RecordDownloadDispatchAsync(films, "movies", "movie", "m1", "Film.One.2020.1080p-DELUNO", "Idx", "client-a", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        await Task.Delay(15); // reason: the list is newest first by the millisecond a send was made
        await queue.RecordDownloadDispatchAsync(shows, "tv", "series", "s1", "Show.One.S01.1080p-DELUNO", "Idx", "client-b", "SABnzbd", "failed", null, cancellationToken: CancellationToken.None);
        await Task.Delay(15); // reason: as above
        await queue.RecordDownloadDispatchAsync(films, "movies", "movie", "m2", "Film.Two.2021.1080p-DELUNO", "Idx", "client-a", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        const string One = "Film.One.2020.1080p-DELUNO";
        const string Show = "Show.One.S01.1080p-DELUNO";
        const string Two = "Film.Two.2021.1080p-DELUNO";

        Assert.Equal([Two, Show, One], await TitlesAsync(app, ""));
        Assert.Equal([Show], await TitlesAsync(app, "?mediaType=tv"));
        Assert.Equal([Show], await TitlesAsync(app, "?entityType=series"));
        Assert.Equal([One], await TitlesAsync(app, "?entityId=m1"));
        Assert.Equal([Two, One], await TitlesAsync(app, $"?libraryId={films}"));
        Assert.Equal([Show], await TitlesAsync(app, "?clientId=client-b"));
        Assert.Equal([Show], await TitlesAsync(app, "?grabStatus=failed"));
        Assert.Equal([Two, One], await TitlesAsync(app, "?grabStatus=succeeded"));
        Assert.Empty(await TitlesAsync(app, "?importStatus=imported"));

        var past = DateTime.UtcNow.AddHours(-1).ToString("O");
        var future = DateTime.UtcNow.AddHours(1).ToString("O");
        Assert.Equal([Two, Show, One], await TitlesAsync(app, $"?minGrabTime={Uri.EscapeDataString(past)}&maxGrabTime={Uri.EscapeDataString(future)}"));
        Assert.Empty(await TitlesAsync(app, $"?minGrabTime={Uri.EscapeDataString(future)}"));
        Assert.Empty(await TitlesAsync(app, $"?maxGrabTime={Uri.EscapeDataString(past)}"));

        var page = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/download-dispatches?pageSize=2");
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.True(page.GetProperty("hasMore").GetBoolean());

        var archivedId = (await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/download-dispatches?entityId=m1")).GetProperty("items")[0].GetProperty("id").GetString();
        Assert.True((await app.Client.DeleteAsync($"/api/v1/download-dispatches/{archivedId}")).IsSuccessStatusCode);
        Assert.Equal([Two, Show], await TitlesAsync(app, ""));
        Assert.Equal([Two, Show, One], await TitlesAsync(app, "?includeArchived=true"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/v1/download-dispatches/not-a-dispatch")).StatusCode);
    }
}
