using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1393: the "Check the list" choices on the import list screen and the interval the server accepts come from
/// one limit. Every preset saves and reads back; one hour past the limit is refused with a sentence on the field.
/// </summary>
public sealed class ImportListCheckIntervalTests
{
    [Fact]
    public async Task Every_preset_the_screen_offers_saves_and_reads_back()
    {
        var presets = ScreenPresets();
        Assert.NotEmpty(presets);
        await using var app = await ApplicationTestHost.StartAsync();

        foreach (var hours in presets)
        {
            var created = await app.Client.PostAsJsonAsync("/api/intake-sources", Feed(hours));
            Assert.True(created.IsSuccessStatusCode, $"{hours} hours was refused: {await created.Content.ReadAsStringAsync()}");
            Assert.Equal(hours, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("syncIntervalHours").GetInt32());
        }

        var stored = (await app.Client.GetFromJsonAsync<JsonElement>("/api/intake-sources")).EnumerateArray()
            .Select(item => item.GetProperty("syncIntervalHours").GetInt32()).Order().ToArray();
        Assert.Equal(presets.Order().ToArray(), stored);
    }

    [Fact]
    public async Task The_longest_preset_is_the_servers_limit_and_one_hour_more_is_refused_naming_the_field()
    {
        Assert.Equal(IntakeSyncLimits.MaxHours, ScreenPresets().Max());
        Assert.Equal(IntakeSyncLimits.MaxHours, ScreenLimit());
        await using var app = await ApplicationTestHost.StartAsync();

        var refused = await app.Client.PostAsJsonAsync("/api/intake-sources", Feed(IntakeSyncLimits.MaxHours + 1));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var message = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("syncIntervalHours")[0].GetString()!;
        Assert.Contains("168 hours", message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/intake-sources", Feed(0))).StatusCode);
    }

    private static object Feed(int hours)
        => new { name = $"Feed {hours}", provider = "rss", feedUrl = $"https://example.invalid/{hours}/feed.xml", mediaType = "movies", isEnabled = false, syncIntervalHours = hours };

    private static int[] ScreenPresets()
        => Regex.Matches(File.ReadAllText(ScreenFile()), @"value: ""(?<hours>\d+)""").Select(m => int.Parse(m.Groups["hours"].Value)).ToArray();

    private static int ScreenLimit()
        => int.Parse(Regex.Match(File.ReadAllText(ScreenFile()), @"MAX_INTAKE_SYNC_INTERVAL_HOURS = (?<n>\d+)").Groups["n"].Value);

    private static string ScreenFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "apps", "web", "src", "lib", "intake-sync-interval.ts");
    }
}
