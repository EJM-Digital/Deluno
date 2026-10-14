using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Importing a library that already holds films and shows, over a real temp folder with real files and real SQLite
/// catalogues. The import never moves or deletes a file, and it judges each file on its size, so a file too small for
/// the tier its name claims comes in as an upgrade rather than as quality met.
/// </summary>
public sealed class ImportingAnExistingLibraryOnDiskTests
{
    private static string Read(ApplicationTestHost app, string database, string sql)
    {
        var path = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>().GetDatabasePath(database);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static async Task<string> ProfileWithCutoffAsync(ApplicationTestHost app, string mediaType)
    {
        var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "4K " + mediaType, mediaType, cutoffQuality = "WEB 2160p", allowedQualities = "WEB 1080p,WEB 2160p",
            customFormatIds = string.Empty, upgradeUntilCutoff = true, upgradeUnknownItems = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> LibraryAsync(ApplicationTestHost app, string mediaType, string profileId)
    {
        var root = Path.Combine(app.DataRoot, mediaType);
        Directory.CreateDirectory(root);
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = mediaType, mediaType, purpose = "collection", rootPath = root, downloadsPath = (string?)null, qualityProfileId = profileId,
            importWorkflow = "copy", processorName = (string?)null, processorOutputPath = (string?)null, processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null, autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task RunToTheEndAsync(ApplicationTestHost app, string libraryId)
    {
        using var scope = app.Services.CreateScope();
        var import = scope.ServiceProvider.GetRequiredService<IExistingLibraryImportService>();
        var started = await import.StartAsync(libraryId, CancellationToken.None);
        Assert.NotNull(started);
        for (var slice = 0; slice < 20; slice++)
        {
            var outcome = await import.RunSliceAsync(started!.Run.Id, CancellationToken.None);
            if (!outcome.MoreWorkRemains) return;
        }

        Assert.Fail("the import run did not finish");
    }

    [Fact]
    public async Task An_undersized_film_named_as_the_cutoff_tier_is_imported_as_an_upgrade_and_no_file_is_touched()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await LibraryAsync(app, "movies", await ProfileWithCutoffAsync(app, "movies"));
        var file = Path.Combine(app.DataRoot, "movies", "Sintel (2010)", "Sintel.2010.2160p.WEB-DL.x265-GRP.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        var before = File.GetLastWriteTimeUtc(file);

        await RunToTheEndAsync(app, libraryId);

        Assert.Equal("upgrade", Read(app, "movies", "SELECT wanted_status FROM movie_wanted_state WHERE file_path IS NOT NULL;"));
        Assert.Contains("GB", Read(app, "movies", "SELECT wanted_reason FROM movie_wanted_state WHERE file_path IS NOT NULL;"));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(file));
        Assert.Equal(before, File.GetLastWriteTimeUtc(file));
        Assert.Single(Directory.GetFileSystemEntries(Path.Combine(app.DataRoot, "movies"), "*", SearchOption.AllDirectories), path => path.EndsWith(".mkv", StringComparison.Ordinal));
    }
}
