using System.Net.Http.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>#1151: an episode imported from a processor's cleaned output says so as its last attempt, as a film does.</summary>
public sealed class AnEpisodeSaysWhereItsFileCameFromTests
{
    [Theory]
    [InlineData("Imported from Weir's cleaned output.", "Imported from Weir's cleaned output.")]
    [InlineData(null, "Episode imported.")]
    public async Task The_episodes_last_attempt_carries_the_origin_note(string? note, string expected)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Shows", mediaType = "tv", purpose = "collection", rootPath = root, downloadsPath = (string?)null,
            qualityProfileId = (string?)null, importWorkflow = "copy", processorName = (string?)null, processorOutputPath = (string?)null,
            processorTimeoutMinutes = (int?)null, processorFailureMode = (string?)null, autoSearchEnabled = false,
            missingSearchEnabled = false, upgradeSearchEnabled = false, searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var libraryId = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        var episodePath = Path.Combine(root, "The Wire", "Season 01", "The Wire S01E01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(episodePath)!);
        await File.WriteAllBytesAsync(episodePath, [1, 2, 3, 4]);

        await app.Services.GetRequiredService<ISeriesCatalogRepository>().ImportExistingAsync(
            libraryId, "The Wire", 2002, "covered", "Imported.", "Bluray-1080p", "Bluray-1080p", true, null, null,
            [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: episodePath, FileSizeBytes: 4)],
            CancellationToken.None, resultNote: note);

        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_search_result FROM episode_wanted_state";
        Assert.Equal(expected, await command.ExecuteScalarAsync(CancellationToken.None));
    }
}
