using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// A title marked "downloading" is put back on the work list when the download it waited on is no longer happening, and only then:
/// a title with a download still going is left alone, and so is one marked in the last ten minutes (a grab writes the title's status
/// and the download's record in that order, so a title read between the two looks abandoned and is not). Real catalogue, real
/// dispatch store, real reconciler.
/// </summary>
public sealed class ATitleWaitingOnAGoneDownloadGoesBackOnTheWorkListTests
{
    [Fact]
    public async Task Only_the_title_whose_download_is_gone_and_was_marked_long_enough_ago_goes_back_on_the_work_list()
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        var libraryId = await LibraryAsync(app);
        var state = app.Services.GetRequiredService<IMediaStateRepository>();
        var stillGoing = await DownloadingFilmAsync(app, libraryId, "Still Going", now.AddHours(-2));
        var gone = await DownloadingFilmAsync(app, libraryId, "Gone", now.AddHours(-2));
        var justMarked = await DownloadingFilmAsync(app, libraryId, "Just Marked", now.AddMinutes(-2));
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            libraryId, "movies", "movie", stillGoing, "Still.Going.2024.1080p.WEB-GRP", "Fixture", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);
        var reconciler = new DownloadStateReconciler(
            state, app.Services.GetRequiredService<ILiveDownloadLookup>(), clock, NullLogger<DownloadStateReconciler>.Instance);

        var cleared = await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, cleared);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        Assert.Equal(WantedStatuses.Downloading, (await movies.GetMovieWantedStateAsync(stillGoing, libraryId, CancellationToken.None))!.WantedStatus);
        Assert.Equal(WantedStatuses.Downloading, (await movies.GetMovieWantedStateAsync(justMarked, libraryId, CancellationToken.None))!.WantedStatus);
        Assert.NotEqual(WantedStatuses.Downloading, (await movies.GetMovieWantedStateAsync(gone, libraryId, CancellationToken.None))!.WantedStatus);
        Assert.Equal(0, await reconciler.ReconcileAsync(CancellationToken.None));
    }

    private static async Task<string> DownloadingFilmAsync(ApplicationTestHost app, string libraryId, string title, DateTimeOffset markedAt)
    {
        using var added = await app.Client.PostAsJsonAsync("/api/movies", new { title, releaseYear = 2024, monitored = true, libraryId });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await app.Services.GetRequiredService<IMediaStateRepository>().SetDownloadingAsync(MediaKind.Movie, id, downloading: true, markedAt, CancellationToken.None);
        return id;
    }

    private static async Task<string> LibraryAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        using var response = await app.Client.PostAsJsonAsync("/api/libraries/", new { name = "Films", mediaType = "movies", rootPath = root });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
}
