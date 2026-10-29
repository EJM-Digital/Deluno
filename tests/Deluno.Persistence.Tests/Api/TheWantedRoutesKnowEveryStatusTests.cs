using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: the wanted summaries over HTTP, with a title in each state a title can be in: still missing, held but below the profile's
/// wish (an upgrade), not out yet (upcoming), and held and satisfied (covered). Each is counted once, and the total is their sum.
/// </summary>
public sealed class TheWantedRoutesKnowEveryStatusTests
{
    [Fact]
    public async Task The_film_wanted_summary_counts_a_missing_an_upgrade_an_upcoming_and_a_covered_film_each_once()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        foreach (var (title, year, status) in new[] { ("Lost Film", 2001, "missing"), ("Weak Copy", 2002, "upgrade"), ("Coming Soon", 2031, "upcoming"), ("Held Film", 2003, "covered") })
        {
            await movies.ImportExistingAsync(libraryId, title, year, wantedStatus: status, wantedReason: $"{status}.", currentQuality: status == "missing" || status == "upcoming" ? null : "WEBDL-720p",
                targetQuality: "Bluray-1080p", qualityCutoffMet: status == "covered", filePath: null, fileSizeBytes: null, cancellationToken: CancellationToken.None);
        }

        var wanted = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/wanted");

        Assert.Equal(1, wanted.GetProperty("missingCount").GetInt32());
        Assert.Equal(1, wanted.GetProperty("upgradeCount").GetInt32());
        Assert.Equal(1, wanted.GetProperty("upcomingCount").GetInt32());
        Assert.Equal(1, wanted.GetProperty("coveredCount").GetInt32());
        Assert.Equal(4, wanted.GetProperty("totalWanted").GetInt32());
        var listed = wanted.GetProperty("recentItems").EnumerateArray().Select(item => item.GetProperty("title").GetString()!).Order().ToArray();
        Assert.Equal(["Coming Soon", "Held Film", "Lost Film", "Weak Copy"], listed);
    }

    [Fact]
    public async Task The_show_wanted_summary_counts_every_show_in_exactly_one_status_and_lists_each()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "tv");
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        foreach (var (title, year, status) in new[] { ("Lost Show", 2001, "missing"), ("Weak Show", 2002, "upgrade"), ("Coming Show", 2031, "upcoming"), ("Held Show", 2003, "covered") })
        {
            await series.ImportExistingAsync(libraryId, title, year, status, $"{status}.", status == "missing" || status == "upcoming" ? null : "WEBDL-720p", "Bluray-1080p", status == "covered", null, null,
                [new ImportedEpisodeItem(1, 1, HasFile: false, FilePath: null, FileSizeBytes: null)], CancellationToken.None);
        }

        var wanted = await app.Client.GetFromJsonAsync<JsonElement>("/api/series/wanted");

        var counted = wanted.GetProperty("missingCount").GetInt32() + wanted.GetProperty("upgradeCount").GetInt32()
            + wanted.GetProperty("upcomingCount").GetInt32() + wanted.GetProperty("coveredCount").GetInt32();
        Assert.Equal(wanted.GetProperty("totalWanted").GetInt32(), counted);
        Assert.Equal(4, counted);
        var listed = wanted.GetProperty("recentItems").EnumerateArray().Select(item => item.GetProperty("title").GetString()!).Order().ToArray();
        Assert.Equal(["Coming Show", "Held Show", "Lost Show", "Weak Show"], listed);
    }

    [Fact]
    public async Task Both_wanted_routes_need_a_signed_in_caller()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/movies/wanted")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/series/wanted")).StatusCode);
    }
}
