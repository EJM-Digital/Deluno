using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Jobs.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: dismissing a "no acceptable release" title from the bell also takes it out of the attention summary the bell counts from,
/// the dismissal is kept (not held in memory), and a show is dismissed by the same route as a film.
/// </summary>
public sealed class ANoReleaseDismissalReachesTheSummaryAndShowsTests
{
    private static async Task<string> WantedFilmAsync(ApplicationTestHost app)
    {
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var added = await movies.AddAsync(new CreateMovieRequest("Nothing Found", 2020, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(added.Id, "lib", "missing", "Wanted", hasFile: false, null, "WEB 1080p", false, CancellationToken.None);
        await movies.RecordSearchAttemptAsync(added.Id, "lib", "automatic", "checked", DateTimeOffset.UtcNow, null, "Searched and found nothing.", null, null, null, CancellationToken.None);
        return added.Id;
    }

    private static async Task<string[]> SummaryRowIdsAsync(ApplicationTestHost app)
    {
        var summary = await app.Client.GetFromJsonAsync<JsonElement>("/api/attention/summary");
        return summary.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetString()!).ToArray();
    }

    [Fact]
    public async Task Dismissing_a_title_with_no_release_takes_it_out_of_the_summary_and_lowers_its_count()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await WantedFilmAsync(app);
        var before = await app.Client.GetFromJsonAsync<JsonElement>("/api/attention/summary");
        Assert.Contains($"no-release:movie:{id}", await SummaryRowIdsAsync(app));

        var dismissed = await app.Client.PostAsync($"/api/attention/no-release/movie/{id}/dismiss", content: null);

        Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        Assert.DoesNotContain($"no-release:movie:{id}", await SummaryRowIdsAsync(app));
        var after = await app.Client.GetFromJsonAsync<JsonElement>("/api/attention/summary");
        Assert.Equal(before.GetProperty("count").GetInt32() - 1, after.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task The_dismissal_is_kept_in_the_dismissal_store_and_dismissing_twice_is_harmless()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await WantedFilmAsync(app);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.PostAsync($"/api/attention/no-release/movie/{id}/dismiss", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.PostAsync($"/api/attention/no-release/movie/{id}/dismiss", content: null)).StatusCode);

        var kept = await app.Services.GetRequiredService<AttentionDismissalStore>().ListAsync(CancellationToken.None);
        Assert.Single(kept, key => key.Contains(id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_show_is_dismissed_by_the_same_route_as_a_film_and_a_made_up_kind_of_title_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var show = await app.Client.PostAsync("/api/attention/no-release/series/some-show/dismiss", content: null);
        var made = await app.Client.PostAsync("/api/attention/no-release/tv/some-show/dismiss", content: null);

        Assert.Equal(HttpStatusCode.NoContent, show.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, made.StatusCode);
        var kept = await app.Services.GetRequiredService<AttentionDismissalStore>().ListAsync(CancellationToken.None);
        Assert.Single(kept, key => key.Contains("some-show", StringComparison.Ordinal) && key.Contains("series", StringComparison.OrdinalIgnoreCase));
    }
}
