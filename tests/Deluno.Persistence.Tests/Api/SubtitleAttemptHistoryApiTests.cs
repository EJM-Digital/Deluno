using Deluno.Contracts;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #840: a title's own subtitle panel reads its append-only search history
/// through the same route table the Wanted screen's status uses underneath.
/// </summary>
public sealed class SubtitleAttemptHistoryApiTests
{
    [Fact]
    public async Task A_movies_history_endpoint_returns_its_recorded_attempts()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var subtitles = app.Services.GetRequiredService<IMediaSubtitleRepository>();

        await movies.ImportExistingBatchAsync(
            "library-movies",
            [
                new ExistingMovieImportRequest(
                    Title: "Dune", ReleaseYear: 2021, WantedStatus: WantedStatuses.Covered,
                    WantedReason: "Imported from your existing library.", CurrentQuality: "WEB 2160p",
                    TargetQuality: "WEB 2160p", QualityCutoffMet: true,
                    FilePath: @"D:\Media\Dune\Dune.mkv", FileSizeBytes: 8_000_000_000)
            ],
            CancellationToken.None);
        var page = await movies.ListPageAsync(new CatalogueQuery(Search: "Dune"), CancellationToken.None);
        var id = page.Items.Single(item => item.Title == "Dune").Id;

        await subtitles.RecordAttemptHistoryAsync(MediaKind.Movie, id, "en", "gestdown", "Downloaded: any release", CancellationToken.None);

        var response = await app.Client.GetAsync($"/api/movies/{id}/subtitles/history");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("en", row.GetProperty("language").GetString());
        Assert.Equal("gestdown", row.GetProperty("provider").GetString());
    }
}
