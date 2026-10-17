using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Coverage register, area show-detail: the routes a show's page leans on, over HTTP against a real host with real SQLite files and
/// a real library folder. The metadata notice's two endpoints, and "Rescan the folder" for a show.
/// </summary>
public sealed class AShowPageRoutesDoWhatThePageAsksTests
{
    private static MetadataProviderIssue Gone(DateTimeOffset at) =>
        new("provider-record-missing", "tmdb", "1438", "tmdb:tv:1438:missing", at, null);

    [Fact]
    public async Task The_metadata_notice_reads_as_nothing_until_a_provider_record_is_gone_then_as_the_evidence_and_is_acknowledged_once()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();

        var none = await app.Client.GetAsync($"/api/series/{show.SeriesId}/metadata/issue");
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.True(string.IsNullOrWhiteSpace(await none.Content.ReadAsStringAsync()) || (await none.Content.ReadAsStringAsync()) == "null", "no issue reads as an empty answer");
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.PostAsync($"/api/series/{show.SeriesId}/metadata/issue/acknowledge", null)).StatusCode);

        Assert.True(await series.RecordMetadataProviderIssueAsync(show.SeriesId, Gone(DateTimeOffset.UtcNow), CancellationToken.None));
        var issue = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/metadata/issue");
        Assert.Equal("provider-record-missing", issue.GetProperty("kind").GetString());
        Assert.Equal("1438", issue.GetProperty("providerId").GetString());
        Assert.Equal(JsonValueKind.Null, issue.GetProperty("acknowledgedUtc").ValueKind);

        var acknowledged = await app.Client.PostAsync($"/api/series/{show.SeriesId}/metadata/issue/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await acknowledged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("acknowledgedUtc").ValueKind);
        var after = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/metadata/issue");
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("acknowledgedUtc").ValueKind);

        // A second acknowledgement of the same evidence says nothing more in Activity.
        await app.Client.PostAsync($"/api/series/{show.SeriesId}/metadata/issue/acknowledge", null);
        var activity = await app.Client.GetStringAsync("/api/activity?take=100");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(activity, "metadata.series.provider-record-missing.acknowledged"));
        Assert.True(File.Exists(show.FilePath), "acknowledging a notice never touches the show's files");
    }

    /// <summary>A metadata service that has dropped the record: the lookup says Missing and nothing else is asked.</summary>
    private sealed class DroppedRecordProvider : IMetadataProvider
    {
        public Task<MetadataProviderRecordLookup> ResolveProviderRecordAsync(MetadataLookupRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new MetadataProviderRecordLookup(MetadataProviderRecordStatus.Missing, "tmdb", request.ProviderId ?? "1438"));

        public Task<IReadOnlyList<MetadataSeason>> GetSeriesCatalogueAsync(string providerId, MetadataCatalogueSource source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataReleaseDates> GetMovieReleaseDatesAsync(string providerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataProviderStatus> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataProviderStatus> CheckReachabilityAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MetadataLookupRequest request, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
        public Task<IReadOnlyList<MetadataEpisodeGroup>> GetSeriesEpisodeGroupsAsync(string providerId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetadataEpisodeGroup>>([]);
        public Task<MetadataCollection?> GetMovieCollectionAsync(string providerId, CancellationToken cancellationToken) => Task.FromResult<MetadataCollection?>(null);
        public Task<IReadOnlyList<MetadataSearchResult>> GetPopularAsync(string mediaType, int page, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
    }

    [Fact]
    public async Task Refreshing_a_show_whose_provider_record_is_gone_answers_409_keeps_the_show_and_its_files_and_leaves_a_notice_that_reads_back_over_HTTP()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IMetadataProvider>();
            services.AddSingleton<IMetadataProvider>(new DroppedRecordProvider());
        });
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        await ApiScenes.ExecuteAsync(app, "series", $"UPDATE series_entries SET metadata_provider = 'tmdb', metadata_provider_id = '1438' WHERE id = '{show.SeriesId}';");

        var refresh = await app.Client.PostAsync($"/api/series/{show.SeriesId}/metadata/refresh", null);

        Assert.Equal(HttpStatusCode.Conflict, refresh.StatusCode);
        var body = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("metadata-provider-record-missing", body.GetProperty("code").GetString());
        Assert.Contains("The Wire was kept", body.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(show.FilePath));
        Assert.Equal("The Wire", (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}")).GetProperty("title").GetString());

        var issue = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/metadata/issue");
        Assert.Equal("provider-record-missing", issue.GetProperty("kind").GetString());
        Assert.Equal("1438", issue.GetProperty("providerId").GetString());

        // The same evidence again is the same notice, and Activity says it once.
        await app.Client.PostAsync($"/api/series/{show.SeriesId}/metadata/refresh", null);
        var activity = await app.Client.GetStringAsync("/api/activity?take=100");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(activity, "metadata.series.provider-record-missing\""));
    }

    [Fact]
    public async Task The_metadata_notice_routes_answer_404_for_a_show_that_is_not_there()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/series/no-such-show/metadata/issue")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/series/no-such-show/metadata/issue/acknowledge", null)).StatusCode);
    }

    [Fact]
    public async Task Rescanning_a_show_marks_a_vanished_episode_file_missing_and_lists_an_untracked_video_without_touching_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var other = await ApiScenes.ShowOnDiskAsync(app, "Deadwood", 2004, show.LibraryId);
        var stray = Path.Combine(show.FolderPath, "Season 01", "The Wire S01E02.mkv");
        await File.WriteAllBytesAsync(stray, [9, 9]);
        File.Delete(show.FilePath);

        var result = await PostOkAsync(app, "/api/filesystem/rescan-title", new { mediaType = "tv", titleId = show.SeriesId });

        Assert.Equal("changed", result.GetProperty("outcome").GetString());
        Assert.Equal(1, result.GetProperty("filesChecked").GetInt32());
        Assert.True(Assert.Single(result.GetProperty("missing").EnumerateArray()).GetProperty("markedMissing").GetBoolean());
        Assert.Equal(stray, Assert.Single(result.GetProperty("untracked").EnumerateArray()).GetProperty("path").GetString());
        Assert.True(File.Exists(stray), "an untracked video is only listed, never moved or deleted");
        Assert.True(File.Exists(other.FilePath), "other shows are not touched");
        var inventory = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/inventory");
        Assert.False(Assert.Single(inventory.GetProperty("episodes").EnumerateArray()).GetProperty("hasFile").GetBoolean());
        var untouched = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{other.SeriesId}/inventory");
        Assert.True(Assert.Single(untouched.GetProperty("episodes").EnumerateArray()).GetProperty("hasFile").GetBoolean());
    }

    [Fact]
    public async Task Rescanning_a_show_whose_library_folder_is_not_there_changes_nothing_and_says_the_drive_may_not_be_mounted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var root = await ApiScenes.LibraryRootAsync(app, show.LibraryId);
        Directory.Delete(root, recursive: true);

        var result = await PostOkAsync(app, "/api/filesystem/rescan-title", new { mediaType = "tv", titleId = show.SeriesId });

        Assert.Equal("library-unreachable", result.GetProperty("outcome").GetString());
        Assert.Empty(result.GetProperty("missing").EnumerateArray());
        Assert.Contains("changed nothing", result.GetProperty("summary").GetString(), StringComparison.Ordinal);
        var inventory = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/inventory");
        Assert.True(Assert.Single(inventory.GetProperty("episodes").EnumerateArray()).GetProperty("hasFile").GetBoolean(), "an unmounted library marks nothing missing");
    }

    [Fact]
    public async Task Rescanning_a_show_that_is_not_there_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/filesystem/rescan-title", new { mediaType = "tv", titleId = "no-such-show" })).StatusCode);
    }

    private static async Task<JsonElement> PostOkAsync(ApplicationTestHost app, string url, object body)
    {
        var response = await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
