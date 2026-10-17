using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Coverage register, area show-detail: a show Deluno holds in more than one library (an older add put some there) is searched and
/// force-downloaded through the library the page names, and without a name Deluno asks rather than picking the first row it read.
/// The route that adds a second library to a show refuses (LibraryMembershipRoutesRefuseAndAnnounceTests), so the second row is written
/// the way an older install left it.
/// </summary>
public sealed class AShowInTwoLibrariesAsksWhichTests
{
    private static async Task<(string First, string Second, string SeriesId)> TwoLibraryShowAsync(ApplicationTestHost app)
    {
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var second = await ApiScenes.CreateLibraryAsync(app, "tv", "Anime", "Anime");
        await app.Services.GetRequiredService<IMediaStateRepository>().EnsureWantedStateAsync(
            MediaKind.Series, show.SeriesId, second, WantedStatuses.Missing, "No file yet.", hasFile: false, "WEB 1080p", "WEB 1080p", false, CancellationToken.None);
        return (show.LibraryId, second, show.SeriesId);
    }

    [Fact]
    public async Task A_search_without_a_library_asks_which_one_and_names_both_and_with_a_library_it_goes_ahead()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (first, second, seriesId) = await TwoLibraryShowAsync(app);

        using var unnamed = JsonDocument.Parse(await (await app.Client.PostAsync($"/api/series/{seriesId}/search?mode=preview", null)).Content.ReadAsStringAsync());
        Assert.Equal("choose_library", unnamed.RootElement.GetProperty("reason").GetString());
        var summary = unnamed.RootElement.GetProperty("summary").GetString()!;
        Assert.Contains("Shows", summary, StringComparison.Ordinal);
        Assert.Contains("Anime", summary, StringComparison.Ordinal);

        foreach (var library in new[] { first, second })
        {
            using var named = JsonDocument.Parse(await (await app.Client.PostAsync($"/api/series/{seriesId}/search?mode=preview&libraryId={library}", null)).Content.ReadAsStringAsync());
            Assert.NotEqual("choose_library", named.RootElement.GetProperty("reason").GetString());
        }

        using var notIn = JsonDocument.Parse(await (await app.Client.PostAsync($"/api/series/{seriesId}/search?mode=preview&libraryId=not-a-library-of-this-show", null)).Content.ReadAsStringAsync());
        Assert.Contains("not in that library", notIn.RootElement.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forced_re_download_without_a_library_says_to_choose_one_and_with_one_it_searches_through_that_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, second, seriesId) = await TwoLibraryShowAsync(app);

        var unnamed = await app.Client.PostAsync($"/api/series/{seriesId}/force-redownload", null);
        Assert.Equal(HttpStatusCode.OK, unnamed.StatusCode);
        Assert.Contains("Choose which library to use", await unnamed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var named = await app.Client.PostAsync($"/api/series/{seriesId}/force-redownload?libraryId={second}", null);
        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        Assert.DoesNotContain("Choose which library to use", await named.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_show_in_one_library_needs_no_library_named()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        using var answer = JsonDocument.Parse(await (await app.Client.PostAsync($"/api/series/{show.SeriesId}/search?mode=preview", null)).Content.ReadAsStringAsync());

        Assert.NotEqual("choose_library", answer.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_library_copy_that_holds_no_file_can_be_removed_and_the_one_that_holds_the_file_cannot_and_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (first, second, seriesId) = await TwoLibraryShowAsync(app);

        var held = await app.Client.DeleteAsync($"/api/series/{seriesId}/libraries/{first}");
        Assert.Equal(HttpStatusCode.Conflict, held.StatusCode);
        Assert.Contains("has a file in", await held.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var empty = await app.Client.DeleteAsync($"/api/series/{seriesId}/libraries/{second}");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        var left = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{seriesId}/libraries/");
        Assert.Equal([first], left.EnumerateArray().Select(item => item.GetProperty("libraryId").GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.DeleteAsync($"/api/series/{seriesId}/libraries/{first}")).StatusCode);
    }

    [Fact]
    public async Task Moving_a_show_to_another_library_is_refused_while_the_old_library_holds_a_file_and_leaves_the_file_and_the_library_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var other = await ApiScenes.CreateLibraryAsync(app, "tv", "Archive", "Archive");

        var refused = await app.Client.PutAsJsonAsync($"/api/series/{show.SeriesId}/libraries/{show.LibraryId}", new { libraryId = other });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("has a file in", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(File.Exists(show.FilePath));
        var libraries = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/libraries/");
        Assert.Equal([show.LibraryId], libraries.EnumerateArray().Select(item => item.GetProperty("libraryId").GetString()!).ToArray());
    }
}
