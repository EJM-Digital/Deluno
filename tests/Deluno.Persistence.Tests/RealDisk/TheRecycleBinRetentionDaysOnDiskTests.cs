using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// System, Recycle bin: the days a removed file is kept. Saving them writes the bin's own state file in the real data folder,
/// is read back by the same route, and tells the open screen at once (the RecycleBinChanged push), so the page shows the
/// new number with no reload.
/// </summary>
public sealed class TheRecycleBinRetentionDaysOnDiskTests
{
    private static string StatePath(ApplicationTestHost app) => Path.Combine(app.DataRoot, "recycle-bin", "state.json");

    [Fact]
    public async Task Saving_the_retention_days_writes_them_to_the_bins_state_file_reads_them_back_and_pushes_RecycleBinChanged()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IRealtimeEventPublisher>(publisher));
        var before = await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin/settings");
        var pushedBefore = publisher.Published.Count(item => item.Type == "RecycleBin");

        var saved = await app.Client.PutAsJsonAsync("/api/recycle-bin/settings", new { retentionDays = 45, maxSizeMb = before.GetProperty("maxSizeMb").GetInt32() });

        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.Equal(45, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("retentionDays").GetInt32());
        Assert.Equal(45, (await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin/settings")).GetProperty("retentionDays").GetInt32());
        // The number is in the bin's own file on disk, which is where a restart reads it from.
        Assert.Equal(45, JsonNode.Parse(await File.ReadAllTextAsync(StatePath(app)))!["settings"]!["retentionDays"]!.GetValue<int>());
        // And the open screen was told: one RecycleBin change for the recycle-bin list.
        Assert.Equal(pushedBefore + 1, publisher.Published.Count(item => item.Type == "RecycleBin" && item.Id == "recycle-bin"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task A_number_of_days_that_cannot_be_kept_is_held_to_the_shortest_the_bin_allows_in_the_file_as_well(int asked, int kept)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var before = await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin/settings");

        var saved = await app.Client.PutAsJsonAsync("/api/recycle-bin/settings", new { retentionDays = asked, maxSizeMb = before.GetProperty("maxSizeMb").GetInt32() });

        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.Equal(kept, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("retentionDays").GetInt32());
        Assert.Equal(kept, JsonNode.Parse(await File.ReadAllTextAsync(StatePath(app)))!["settings"]!["retentionDays"]!.GetValue<int>());
    }
}
