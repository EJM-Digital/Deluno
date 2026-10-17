using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A second copy of a film the library already holds, sitting in a processor's output folder, on System > Health: "Keep" snoozes
/// the row for 30 days and leaves the file; "Remove" sends the spare copy to the recycle bin and never touches the library's film.
/// Real files in a real data folder, the real routes the two buttons call, the real health store.
/// </summary>
public sealed class SpareCopyKeepAndRemoveOnRealFilesTests
{
    private sealed record World(string LibraryId, string Spare, string LibraryFilm, string SpareId);

    private static async Task<(World World, HealthCheckOutcome Row)> SetUpAsync(ApplicationTestHost app)
    {
        var root = Path.Combine(app.DataRoot, "movies");
        var output = Path.Combine(app.DataRoot, "refined");
        var libraryFilm = Path.Combine(root, "Pulp Fiction (1994)", "Pulp.Fiction.1994.mkv");
        var spare = Path.Combine(output, "Pulp.Fiction.1994.1080p.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(libraryFilm)!);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(libraryFilm, "the film the library holds");
        await File.WriteAllTextAsync(spare, "a second copy of it");
        File.SetLastWriteTimeUtc(spare, DateTime.UtcNow.AddHours(-3));

        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Films", mediaType = "movies", purpose = "collection", rootPath = root, downloadsPath = (string?)null, qualityProfileId = (string?)null,
            importWorkflow = "refine-before-import", processorName = "Weir", processorOutputPath = output, processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null, autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var libraryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var movie = await movies.AddAsync(new CreateMovieRequest("Pulp Fiction", 1994, null, true, null, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(movie.Id, libraryId, WantedStatuses.Covered, "Already in the library.", true, null, null, true, CancellationToken.None);

        var row = await RunCheckAsync(app);
        return (new World(libraryId, spare, libraryFilm, row.Id), row);
    }

    private static async Task<HealthCheckOutcome> RunCheckAsync(ApplicationTestHost app)
    {
        var check = new ProcessorOutputSafetyNetHealthCheck(
            app.Services.GetRequiredService<ILibrariesRepository>(),
            app.Services.GetRequiredService<IProcessorRepository>(),
            app.Services.GetRequiredService<IJobQueueRepository>(),
            app.Services.GetRequiredService<IMovieCatalogRepository>());
        var outcomes = await check.RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<IHealthCheckRepository>().UpsertResultsAsync(outcomes, CancellationToken.None);
        return Assert.Single(outcomes);
    }

    [Fact]
    public async Task Keep_snoozes_the_spare_copy_row_for_thirty_days_and_leaves_both_files()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (world, row) = await SetUpAsync(app);
        Assert.Contains("\"spareCopyOf\":\"Pulp Fiction (1994)\"", row.DetailsJson);

        // The button posts 24 * 30 hours to the row's snooze route.
        using var response = await app.Client.PostAsJsonAsync($"/api/system/health/{Uri.EscapeDataString(world.SpareId)}/snooze", new { hours = 24 * 30 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snoozedUntil = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("snoozedUntilUtc").GetDateTimeOffset();
        Assert.InRange(snoozedUntil - DateTimeOffset.UtcNow, TimeSpan.FromDays(29.9), TimeSpan.FromDays(30.1));
        var stored = await app.Services.GetRequiredService<IHealthCheckRepository>().GetAsync(world.SpareId, CancellationToken.None);
        Assert.True(stored!.IsSnoozed(DateTimeOffset.UtcNow));
        Assert.True(File.Exists(world.Spare), "Keep leaves the spare copy where it is");
        Assert.True(File.Exists(world.LibraryFilm));
    }

    [Fact]
    public async Task Remove_sends_the_spare_copy_to_the_recycle_bin_and_the_library_film_is_not_touched()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (world, _) = await SetUpAsync(app);

        // The button posts the row's library and path to the same route Clear uses.
        using var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/output/delete", new { libraryId = world.LibraryId, path = world.Spare });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(File.Exists(world.Spare), "the spare copy is gone from the output folder");
        Assert.Equal("the film the library holds", await File.ReadAllTextAsync(world.LibraryFilm));
        var activity = await app.Client.GetStringAsync("/api/activity?take=100");
        Assert.Contains("Moved Pulp.Fiction.1994.1080p.mkv from the Films library's processor output folder to the recycle bin.", activity);
        var after = await new ProcessorOutputSafetyNetHealthCheck(
            app.Services.GetRequiredService<ILibrariesRepository>(),
            app.Services.GetRequiredService<IProcessorRepository>(),
            app.Services.GetRequiredService<IJobQueueRepository>(),
            app.Services.GetRequiredService<IMovieCatalogRepository>()).RunAsync(CancellationToken.None);
        Assert.Empty(after);
    }
}
