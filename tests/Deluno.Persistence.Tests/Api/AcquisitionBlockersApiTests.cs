using Deluno.Contracts;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Asking a title why it will not download gets an answer.
///
/// <para>Through the real route table, because the value of this is that a
/// screen can ask it. A reader that composes good sentences and is not reachable
/// answers nobody.</para>
/// </summary>
public sealed class AcquisitionBlockersApiTests
{
    [Theory]
    [InlineData("movies", "/api/movies/")]
    [InlineData("tv", "/api/series/")]
    public async Task A_title_with_nothing_in_the_way_says_so(string mediaType, string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, route, mediaType);

        var answer = await BlockersAsync(app, route, id);

        Assert.True(answer.GetProperty("nothingIsBlocking").GetBoolean());
        Assert.False(answer.GetProperty("canForce").GetBoolean());
        Assert.Empty(answer.GetProperty("blockers").EnumerateArray().ToArray());
        Assert.Contains("Nothing is stopping", answer.GetProperty("summary").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The commonest reason of all, and the one people most often mistake for a
    /// fault: it is already here.
    /// </summary>
    [Theory]
    [InlineData("movies", "/api/movies/")]
    [InlineData("tv", "/api/series/")]
    public async Task A_title_already_held_at_its_target_explains_itself(string mediaType, string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, route, mediaType);
        var kind = mediaType == "tv" ? MediaKind.Series : MediaKind.Movie;

        await app.Services.GetRequiredService<IMediaStateRepository>().EnsureWantedStateAsync(
            kind,
            id,
            "library-" + mediaType,
            WantedStatuses.Covered,
            "Imported from disk.",
            hasFile: true,
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            CancellationToken.None);

        var answer = await BlockersAsync(app, route, id);

        Assert.False(answer.GetProperty("nothingIsBlocking").GetBoolean());
        var blocker = Assert.Single(answer.GetProperty("blockers").EnumerateArray().ToArray());
        Assert.Equal(AcquisitionBlockerKinds.AlreadyHeld, blocker.GetProperty("kind").GetString());
        // Nothing to override, so no button is offered.
        Assert.False(blocker.GetProperty("canClear").GetBoolean());
        Assert.False(answer.GetProperty("canForce").GetBoolean());
    }

    [Theory]
    [InlineData("/api/movies/")]
    [InlineData("/api/series/")]
    public async Task A_title_that_does_not_exist_is_a_404_not_an_empty_answer(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync($"{route.TrimEnd('/')}/does-not-exist/acquisition-blockers");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Forcing with nothing in the way is honest about having done nothing.
    ///
    /// <para>The failure mode being avoided is a button that always reports
    /// success. If a force says "done" when it cleared nothing, it teaches
    /// people to press it twice and trust it less than the silence it
    /// replaced.</para>
    /// </summary>
    [Theory]
    [InlineData("movies", "/api/movies/")]
    [InlineData("tv", "/api/series/")]
    public async Task Forcing_a_title_with_nothing_to_clear_says_it_cleared_nothing(string mediaType, string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, route, mediaType);

        var response = await app.Client.PostAsync($"{route.TrimEnd('/')}/{id}/force-redownload", null);
        Assert.True(
            response.IsSuccessStatusCode,
            $"POST force-redownload returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = document.RootElement;

        Assert.Empty(result.GetProperty("cleared").EnumerateArray().ToArray());
        Assert.Empty(result.GetProperty("couldNotClear").EnumerateArray().ToArray());
        Assert.Contains("nothing to clear", result.GetProperty("summary").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And a force leaves a record. It reaches into a download client and a
    /// processor, so "who did this and what did it do" has to be answerable
    /// afterwards.
    /// </summary>
    [Theory]
    [InlineData("movies", "/api/movies/")]
    [InlineData("tv", "/api/series/")]
    public async Task Forcing_writes_what_it_did_to_the_activity_feed(string mediaType, string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, route, mediaType);

        await app.Client.PostAsync($"{route.TrimEnd('/')}/{id}/force-redownload", null);

        var activity = await app.Client.GetAsync("/api/activity");
        Assert.True(activity.IsSuccessStatusCode, await activity.Content.ReadAsStringAsync());

        var body = await activity.Content.ReadAsStringAsync();
        Assert.Contains("acquisition.override", body, StringComparison.Ordinal);
        Assert.Contains("forced a re-download", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/api/movies/")]
    [InlineData("/api/series/")]
    public async Task Forcing_a_title_that_does_not_exist_is_a_404(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync($"{route.TrimEnd('/')}/does-not-exist/force-redownload", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// And it goes and looks. Clearing every obstacle and then leaving the
    /// title exactly where it was would make the word "force" a lie — the
    /// person pressed a button that promised a re-download, not a tidy-up.
    /// </summary>
    [Theory]
    [InlineData("movies", "/api/movies/")]
    [InlineData("tv", "/api/series/")]
    public async Task Forcing_starts_a_search_rather_than_only_clearing_the_way(string mediaType, string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateTempSubdirectory("deluno-force-").FullName;
        try
        {
            await app.Client.PostAsJsonAsync("/api/libraries/", NewLibrary(mediaType, root));
            var id = await AddAsync(app, route, mediaType);

            var response = await app.Client.PostAsync($"{route.TrimEnd('/')}/{id}/force-redownload", null);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(
                document.RootElement.GetProperty("searchStarted").GetBoolean(),
                $"the force reported no search: {document.RootElement.GetProperty("summary").GetString()}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<JsonElement> BlockersAsync(ApplicationTestHost app, string route, string id)
    {
        var response = await app.Client.GetAsync($"{route.TrimEnd('/')}/{id}/acquisition-blockers");
        Assert.True(
            response.IsSuccessStatusCode,
            $"GET blockers returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<string> AddAsync(ApplicationTestHost app, string route, string mediaType)
    {
        object body = mediaType == "tv"
            ? new { title = "Severance", startYear = 2022, monitored = true }
            : new { title = "Arrival", releaseYear = 2016, monitored = true };

        var response = await app.Client.PostAsJsonAsync(route, body);
        Assert.True(
            response.IsSuccessStatusCode,
            $"POST {route} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static object NewLibrary(string mediaType, string rootPath) => new
    {
        name = mediaType == "tv" ? "Shows" : "Films",
        mediaType,
        purpose = "collection",
        rootPath,
        downloadsPath = (string?)null,
        qualityProfileId = (string?)null,
        importWorkflow = "copy",
        processorName = (string?)null,
        processorOutputPath = (string?)null,
        processorTimeoutMinutes = (int?)null,
        processorFailureMode = (string?)null,
        autoSearchEnabled = false,
        missingSearchEnabled = false,
        upgradeSearchEnabled = false,
        searchIntervalHours = (int?)null,
        retryDelayHours = (int?)null,
        maxItemsPerRun = (int?)null
    };
}
