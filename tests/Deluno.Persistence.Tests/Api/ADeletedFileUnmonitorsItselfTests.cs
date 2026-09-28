using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Cleanup sheet row K2 (#931): a title whose file was removed outside Deluno
/// stops being wanted.
///
/// <para>Before this, the library file check marked a vanished file missing and
/// left monitoring on, so a film the user deliberately deleted from disk was
/// searched for and downloaded again — the one thing a library manager must
/// never do to a title its owner got rid of on purpose.</para>
///
/// <para>The other half of the acceptance is what must never trigger this: a
/// library root that is merely offline (an unmounted drive, an asleep NAS) is
/// not a library full of deleted files, and must leave every title exactly as
/// it was (DESIGN-007 decision 12, also guarded by
/// <c>AnUnreachableLibrarySaysSoTests</c>).</para>
///
/// <para>Driven through <c>POST /api/filesystem/file-check</c> — the same
/// route the System screen's "Check library files now" button calls and the
/// six-hourly schedule calls — so this is a guard on both.</para>
/// </summary>
public sealed class ADeletedFileUnmonitorsItselfTests
{
    [Fact]
    public async Task A_film_deleted_outside_Deluno_becomes_unmonitored_and_Activity_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var rootPath = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var libraryId = await CreateLibraryAsync(app, "movies", rootPath);

        var filePath = Path.Combine(rootPath, "Dune (2021)", "Dune (2021) [Bluray-1080p].mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);

        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            libraryId,
            "Dune",
            2021,
            wantedStatus: "covered",
            wantedReason: "Imported from your existing library.",
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            filePath: filePath,
            fileSizeBytes: 4,
            cancellationToken: CancellationToken.None);

        var movieId = Assert.Single(await movies.ListAsync(CancellationToken.None)).Id;
        Assert.True(await IsMonitoredAsync(app, movieId), "the title should start monitored");

        // The user deletes the file themselves, outside Deluno.
        File.Delete(filePath);

        var response = await app.Client.PostAsync("/api/filesystem/file-check", content: null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        Assert.False(
            await IsMonitoredAsync(app, movieId),
            "a film whose file vanished outside Deluno must stop being monitored");
        Assert.False(await HasFileAsync(app, movieId), "the vanished file must be recorded as gone");

        var activity = await ActivityBodyAsync(app);
        Assert.Contains("Dune", activity, StringComparison.Ordinal);
        Assert.Contains("removed outside Deluno", activity, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stopped wanting it", activity, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_episode_deleted_outside_Deluno_becomes_unmonitored_without_touching_the_show()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var rootPath = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var libraryId = await CreateLibraryAsync(app, "tv", rootPath);

        var episodePath = Path.Combine(rootPath, "The Wire", "Season 01", "The Wire S01E01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(episodePath)!);
        await File.WriteAllBytesAsync(episodePath, [1, 2, 3, 4]);

        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        await series.ImportExistingAsync(
            libraryId,
            "The Wire",
            2002,
            wantedStatus: "covered",
            wantedReason: "Imported from your existing library.",
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            filePath: null,
            fileSizeBytes: null,
            episodes:
            [
                new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: episodePath, FileSizeBytes: 4)
            ],
            cancellationToken: CancellationToken.None);

        var seriesId = Assert.Single(await series.ListAsync(CancellationToken.None)).Id;
        Assert.True(await IsSeriesMonitoredAsync(app, seriesId), "the show should start monitored");

        File.Delete(episodePath);

        var response = await app.Client.PostAsync("/api/filesystem/file-check", content: null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        // The episode stops being wanted; the show itself is not touched, only
        // the one episode whose file actually disappeared.
        Assert.True(
            await IsSeriesMonitoredAsync(app, seriesId),
            "one missing episode must not unmonitor the whole show");

        var activity = await ActivityBodyAsync(app);
        Assert.Contains("The Wire", activity, StringComparison.Ordinal);
        Assert.Contains("removed outside Deluno", activity, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_offline_root_never_counts_as_deleted_and_nothing_is_unmonitored()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var rootPath = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var libraryId = await CreateLibraryAsync(app, "movies", rootPath);

        var filePath = Path.Combine(rootPath, "Dune (2021)", "Dune (2021) [Bluray-1080p].mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);

        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            libraryId,
            "Dune",
            2021,
            wantedStatus: "covered",
            wantedReason: "Imported from your existing library.",
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            filePath: filePath,
            fileSizeBytes: 4,
            cancellationToken: CancellationToken.None);

        var movieId = Assert.Single(await movies.ListAsync(CancellationToken.None)).Id;

        // The whole root goes away, as an unmounted drive or an asleep NAS
        // would - not a single tracked file.
        Directory.Delete(rootPath, recursive: true);

        var response = await app.Client.PostAsync("/api/filesystem/file-check", content: null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        Assert.True(
            await IsMonitoredAsync(app, movieId),
            "an offline library root must never be read as its files having been deleted");
        Assert.True(await HasFileAsync(app, movieId), "an offline root must leave the held-file record alone");

        var activity = await ActivityBodyAsync(app);
        Assert.DoesNotContain("stopped wanting it", activity, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #931's other guard: deleting a title in Deluno itself - DELETE
    /// /api/movies/{id}?deleteFiles=true, the real "delete in Deluno" path -
    /// removes the whole catalogue entry through TitleRemovalService rather
    /// than marking a file missing, so it must never write the
    /// reconciliation's "removed outside Deluno" sentence either.
    /// </summary>
    [Fact]
    public async Task Deleting_a_film_in_Deluno_never_writes_the_removed_outside_Deluno_line()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var rootPath = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var libraryId = await CreateLibraryAsync(app, "movies", rootPath);

        var filePath = Path.Combine(rootPath, "Dune (2021)", "Dune (2021) [Bluray-1080p].mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);

        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            libraryId,
            "Dune",
            2021,
            wantedStatus: "covered",
            wantedReason: "Imported from your existing library.",
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            filePath: filePath,
            fileSizeBytes: 4,
            cancellationToken: CancellationToken.None);

        var movieId = Assert.Single(await movies.ListAsync(CancellationToken.None)).Id;

        var response = await app.Client.DeleteAsync($"/api/movies/{movieId}?deleteFiles=true");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var activity = await ActivityBodyAsync(app);
        Assert.DoesNotContain("removed outside Deluno", activity, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string mediaType, string rootPath)
    {
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
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
            searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });

        Assert.True(
            response.IsSuccessStatusCode,
            $"POST /api/libraries/ returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<bool> IsMonitoredAsync(ApplicationTestHost app, string movieId)
    {
        var response = await app.Client.GetAsync($"/api/movies/{movieId}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("monitored").GetBoolean();
    }

    private static async Task<bool> HasFileAsync(ApplicationTestHost app, string movieId)
    {
        var response = await app.Client.GetAsync($"/api/movies/{movieId}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("hasFile").GetBoolean();
    }

    private static async Task<bool> IsSeriesMonitoredAsync(ApplicationTestHost app, string seriesId)
    {
        var response = await app.Client.GetAsync($"/api/series/{seriesId}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("monitored").GetBoolean();
    }

    private static async Task<string> ActivityBodyAsync(ApplicationTestHost app)
    {
        var response = await app.Client.GetAsync("/api/activity?pageSize=100");
        Assert.True(response.IsSuccessStatusCode, $"GET activity returned {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }
}
