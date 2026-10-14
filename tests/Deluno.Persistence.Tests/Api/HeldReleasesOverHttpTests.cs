using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Needs action, "held" releases, over HTTP on the real host: the list, Approve and Dismiss. Approve judges the release again
/// first and answers a plain 409 reason instead of sending a release the title no longer needs; Dismiss only stops the row
/// being listed and changes nothing about the title.
/// </summary>
public sealed class HeldReleasesOverHttpTests
{
    private const string HeldPlan =
        """{"bestCandidate":{"releaseName":"Big.Buck.Bunny.2008.2160p.WEB-DL-GRP","indexerName":"Nyaa","indexerId":"ix1","quality":"WEB 2160p","sizeBytes":8000000000,"seeders":12,"downloadUrl":"https://indexer.example/get/1","decisionStatus":"held","summary":"Held for review by the typed release plan."},"candidates":[{"releaseName":"Big.Buck.Bunny.2008.2160p.WEB-DL-GRP","decisionStatus":"held","downloadUrl":"https://indexer.example/get/1"}]}""";

    [Fact]
    public async Task A_held_search_is_listed_with_its_release_and_a_search_that_sent_something_is_not()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var held = await HeldMovieAsync(app, "Big Buck Bunny", "held");
        await HeldMovieAsync(app, "Grabbed", "matched");

        var rows = await app.Client.GetFromJsonAsync<JsonElement>("/api/attention/held-releases");

        var row = Assert.Single(rows.EnumerateArray());
        Assert.Equal(held, row.GetProperty("id").GetString());
        Assert.Equal("movie", row.GetProperty("mediaType").GetString());
        Assert.Equal("Big.Buck.Bunny.2008.2160p.WEB-DL-GRP", row.GetProperty("releaseName").GetString());
        Assert.Equal("https://indexer.example/get/1", row.GetProperty("downloadUrl").GetString());
    }

    [Fact]
    public async Task Dismiss_stops_the_row_being_listed_and_leaves_the_title_wanted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await HeldMovieAsync(app, "Big Buck Bunny", "held");
        var historyId = await HistoryIdAsync(app);

        using var response = await app.Client.PostAsync($"/api/attention/held-releases/{historyId}/dismiss", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/attention/held-releases")).EnumerateArray());
        var movie = await app.Services.GetRequiredService<IMovieCatalogRepository>().GetByIdAsync(id, CancellationToken.None);
        Assert.NotNull(movie);
        Assert.False(movie!.HasFile);
        Assert.True(movie.Monitored);
    }

    [Fact]
    public async Task Approving_a_film_that_has_its_file_since_is_a_plain_409_and_sends_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await HeldMovieAsync(app, "Big Buck Bunny", "held");
        var historyId = await HistoryIdAsync(app);
        await app.Services.GetRequiredService<IMovieCatalogRepository>().EnsureWantedStateAsync(id, "lib", "covered", "Has its file", hasFile: true, "WEB 2160p", "WEB 1080p", true, CancellationToken.None);

        using var response = await app.Client.PostAsync($"/api/attention/held-releases/{historyId}/approve", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("held-release-refused", body.GetProperty("code").GetString());
        Assert.Contains("no longer needs a release", body.GetProperty("message").GetString());
        var dispatches = await app.Client.GetStringAsync("/api/download-dispatches");
        Assert.DoesNotContain("Big.Buck.Bunny", dispatches);
    }

    [Fact]
    public async Task Approving_a_row_that_is_not_there_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var approve = await app.Client.PostAsync("/api/attention/held-releases/no-such-row/approve", content: null);

        Assert.Equal(HttpStatusCode.NotFound, approve.StatusCode);
    }

    [Fact]
    public async Task The_held_release_routes_need_sign_in_and_a_read_only_key_cannot_approve_or_dismiss()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await HeldMovieAsync(app, "Big Buck Bunny", "held");
        var historyId = await HistoryIdAsync(app);
        var made = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Reader", scopes = "read" });
        var key = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var reader = app.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", key);
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/attention/held-releases")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/attention/held-releases")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/attention/held-releases/{historyId}/dismiss", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/attention/held-releases/{historyId}/approve", content: null)).StatusCode);
        Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/attention/held-releases")).EnumerateArray());
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<string> HistoryIdAsync(ApplicationTestHost app)
        => Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/attention/held-releases")).EnumerateArray()).GetProperty("historyId").GetString()!;

    private static async Task<string> HeldMovieAsync(ApplicationTestHost app, string title, string outcome)
    {
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var added = await movies.AddAsync(new CreateMovieRequest(title, 2008, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(added.Id, "lib", "missing", "Wanted", hasFile: false, null, "WEB 1080p", false, CancellationToken.None);
        await movies.RecordSearchAttemptAsync(
            added.Id, "lib", "automatic", outcome, DateTimeOffset.UtcNow.AddHours(-1), null, "Held.",
            "Big.Buck.Bunny.2008.2160p.WEB-DL-GRP", "Nyaa", HeldPlan, CancellationToken.None);
        return added.Id;
    }
}
