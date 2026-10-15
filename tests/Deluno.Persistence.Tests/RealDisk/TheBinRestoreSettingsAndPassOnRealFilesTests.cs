using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Platform BIN-04, BIN-06, BIN-09 and BIN-12 on a real temp folder: the recycle bin's restore when the stored item has gone from disk,
/// its settings (default, clamped, kept in its state file), the scheduled pass saying what it removed, and the list showing only the bin.
/// </summary>
public sealed class TheBinRestoreSettingsAndPassOnRealFilesTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static string LibraryFile(RealDiskLibrary world, string name, byte fill = 0x11, int length = 4096)
    {
        var folder = Path.Combine(world.Root, Path.GetFileNameWithoutExtension(name));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, length).ToArray());
        return path;
    }

    private static string StatePath(RealDiskLibrary world) => Path.Combine(world.App.DataRoot, "recycle-bin", "state.json");

    [Fact]
    public async Task Restoring_an_item_whose_stored_copy_has_gone_from_disk_says_so_and_drops_the_entry_so_it_is_not_offered_again()
    {
        await using var world = await StartWorldAsync();
        var file = LibraryFile(world, "Sintel (2010).mkv");
        var item = (await world.Bin.StoreReplacementAsync(world.Library, file, file, CancellationToken.None))!;
        File.Delete(item.RecyclePath);

        var response = await world.App.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("The stored recycle-bin item is no longer on disk.", body, StringComparison.Ordinal);
        Assert.False(File.Exists(file), "nothing was put back, because there was nothing to put back");
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        Assert.DoesNotContain(item.Id, File.ReadAllText(StatePath(world)), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await world.App.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task Restoring_where_the_way_back_cannot_be_made_keeps_the_item_in_the_bin_with_the_reason_and_the_file_safe()
    {
        await using var world = await StartWorldAsync();
        var file = LibraryFile(world, "Dune (2021).mkv", fill: 0x22);
        var item = (await world.Bin.StoreReplacementAsync(world.Library, file, file, CancellationToken.None))!;
        // The place it came from is now somewhere nothing can be created (a file stands where its folder was): the same refusal a missing drive gives.
        var folder = Path.GetDirectoryName(file)!;
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        File.WriteAllText(folder, "in the way");

        var response = await world.App.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Restore failed", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.All(File.ReadAllBytes(item.RecyclePath), value => Assert.Equal(0x22, value));
        Assert.Equal(item.Id, Assert.Single(await world.Bin.ListAsync(CancellationToken.None)).Id);
    }

    [Fact]
    public async Task The_settings_start_at_30_days_and_100_GB_are_clamped_and_kept_in_the_bins_own_state_file()
    {
        await using var world = await StartWorldAsync();

        var defaults = await world.App.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin/settings");
        Assert.Equal(30, defaults.GetProperty("retentionDays").GetInt32());
        Assert.Equal(102_400, defaults.GetProperty("maxSizeMb").GetInt64());

        var saved = await world.App.Client.PutAsJsonAsync("/api/recycle-bin/settings", new { retentionDays = 14, maxSizeMb = 2048 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var read = await world.App.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin/settings");
        Assert.Equal(14, read.GetProperty("retentionDays").GetInt32());
        Assert.Equal(2048, read.GetProperty("maxSizeMb").GetInt64());
        var onDisk = JsonNode.Parse(File.ReadAllText(StatePath(world)))!["settings"]!;
        Assert.Equal(14, onDisk["retentionDays"]!.GetValue<int>());
        Assert.Equal(2048, onDisk["maxSizeMb"]!.GetValue<long>());

        var tooSmall = await (await world.App.Client.PutAsJsonAsync("/api/recycle-bin/settings", new { retentionDays = 0, maxSizeMb = 0 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, tooSmall.GetProperty("retentionDays").GetInt32());
        Assert.Equal(1, tooSmall.GetProperty("maxSizeMb").GetInt64());
        var tooBig = await (await world.App.Client.PutAsJsonAsync("/api/recycle-bin/settings", new { retentionDays = 99999, maxSizeMb = 99_999_999_999L })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(365, tooBig.GetProperty("retentionDays").GetInt32());
        Assert.Equal(10_000_000, tooBig.GetProperty("maxSizeMb").GetInt64());
    }

    [Fact]
    public async Task The_scheduled_pass_says_in_the_log_how_many_items_it_removed_and_says_nothing_when_it_removed_none()
    {
        await using var world = await StartWorldAsync();
        var oldFile = LibraryFile(world, "Old (2001).mkv", length: 5000);
        var old = (await world.Bin.StoreReplacementAsync(world.Library, oldFile, oldFile, CancellationToken.None))!;
        var state = JsonNode.Parse(File.ReadAllText(StatePath(world)))!;
        foreach (var item in state["items"]!.AsArray())
        {
            if (item!["id"]!.GetValue<string>() == old.Id) item["expiresUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        }

        File.WriteAllText(StatePath(world), state.ToJsonString());
        using var scope = world.App.Services.CreateScope();
        var log = new ListLogger<WorkPlanner>();
        var planner = new WorkPlanner(log, scope.ServiceProvider.GetRequiredService<IJobQueueRepository>(), new ConfigurationBuilder().Build(), TimeProvider.System);

        await planner.RunRecycleBinCleanupAsync(world.Bin, CancellationToken.None);

        Assert.False(File.Exists(old.RecyclePath));
        Assert.Contains(log.Lines, line => line.Contains("permanently removed 1 expired or over-capacity item(s)", StringComparison.Ordinal));
        var recorded = (await scope.ServiceProvider.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None)).Single(item => item.ScheduleKey == SystemTasks.RecycleBinCleanup);
        Assert.Equal("completed", recorded.LastResult);

        log.Lines.Clear();
        await planner.RunRecycleBinCleanupAsync(world.Bin, CancellationToken.None);
        Assert.DoesNotContain(log.Lines, line => line.Contains("permanently removed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_bin_list_holds_only_what_was_put_in_the_bin_and_not_the_library_files_beside_it()
    {
        await using var world = await StartWorldAsync();
        var kept = LibraryFile(world, "Kept (2005).mkv");
        var binned = LibraryFile(world, "Binned (2006).mkv");
        var item = (await world.Bin.StoreReplacementAsync(world.Library, binned, binned, CancellationToken.None))!;

        var listed = await world.App.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin");

        var only = Assert.Single(listed.EnumerateArray());
        Assert.Equal(item.Id, only.GetProperty("id").GetString());
        Assert.Equal(binned, only.GetProperty("originalPath").GetString());
        Assert.True(File.Exists(kept), "a file that was not removed is not in the bin and not touched");
        Assert.DoesNotContain("Kept (2005)", listed.ToString(), StringComparison.Ordinal);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
