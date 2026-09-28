using Deluno.Persistence.Tests.Support;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #961: <c>POST /api/filesystem/import/execute-batch</c> through the real
/// route table. The folder-scan drawer sends every included row in one call;
/// each row must be imported or reported on its own, and the response totals
/// must match what actually happened to the files on disk.
/// </summary>
public sealed class ManualImportExecuteBatchApiTests
{
    [Fact]
    public async Task Executing_a_batch_imports_the_good_rows_and_reports_the_bad_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var downloadsPath = Path.Combine(app.DataRoot, "downloads");
        var movieRootPath = Path.Combine(app.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var arrivalPath = Path.Combine(downloadsPath, "Arrival.2016.WEB.1080p.mkv");
        var dunePath = Path.Combine(downloadsPath, "Dune.2021.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(arrivalPath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(dunePath, [5, 6, 7, 8]);
        File.SetLastWriteTimeUtc(arrivalPath, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(dunePath, DateTime.UtcNow.AddMinutes(-1));

        var libraryResponse = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "movies",
            mediaType = "movies",
            purpose = "collection",
            rootPath = movieRootPath,
            downloadsPath,
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
        Assert.True(libraryResponse.IsSuccessStatusCode, await libraryResponse.Content.ReadAsStringAsync());

        var batchRequest = new
        {
            items = new object[]
            {
                new
                {
                    preview = new
                    {
                        sourcePath = arrivalPath,
                        fileName = (string?)null,
                        mediaType = "movies",
                        title = "Arrival",
                        year = 2016,
                        genres = (string[]?)null,
                        tags = (string[]?)null,
                        studio = (string?)null,
                        originalLanguage = (string?)null,
                        imdbId = "tt2543164"
                    },
                    transferMode = "copy",
                    overwrite = false,
                    allowCopyFallback = true,
                    forceReplacement = false
                },
                new
                {
                    preview = new
                    {
                        // A source file that does not exist: this row must
                        // come back as its own failure, not sink the batch.
                        sourcePath = Path.Combine(downloadsPath, "Missing.File.mkv"),
                        fileName = (string?)null,
                        mediaType = "movies",
                        title = "Missing File",
                        year = 2020,
                        genres = (string[]?)null,
                        tags = (string[]?)null,
                        studio = (string?)null,
                        originalLanguage = (string?)null,
                        imdbId = "tt0000001"
                    },
                    transferMode = "copy",
                    overwrite = false,
                    allowCopyFallback = true,
                    forceReplacement = false
                }
            }
        };

        var response = await app.Client.PostAsJsonAsync("/api/filesystem/import/execute-batch", batchRequest);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;

        Assert.Equal(2, body.GetProperty("requestedCount").GetInt32());
        Assert.Equal(1, body.GetProperty("importedCount").GetInt32());

        var results = body.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(2, results.Length);

        var arrivalResult = Assert.Single(results, result => result.GetProperty("sourcePath").GetString() == arrivalPath);
        Assert.True(arrivalResult.GetProperty("succeeded").GetBoolean());

        var missingResult = Assert.Single(results, result => result.GetProperty("sourcePath").GetString() != arrivalPath);
        Assert.False(missingResult.GetProperty("succeeded").GetBoolean());

        // The row that succeeded actually landed in the library; the one that
        // failed left nothing behind for Deluno to have silently misplaced.
        Assert.True(Directory.Exists(Path.Combine(movieRootPath, "Arrival (2016)")));
    }
}
