using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// A retention rule removes a title on its day on the real host, over a real library folder and the real databases, with Weir 1.0.0-rc.13
/// and Jellyfin 12.2.0 answering from their recordings on loopback ports. The film's folder goes to the recycle bin, an exclusion keeps it
/// out of every list, the open grab and the hand-off waiting at the processor are ended (Weir is told to drop it with the call it was
/// recorded taking), and the media server is told the film is gone.
/// </summary>
public sealed class RetentionRemovalReachesTheOtherAppsOnRealFilesTests
{
    private const string Release = "Sintel.2010.1080p.WEB-DL-DELUNO";

    [Fact]
    public async Task A_removal_on_the_day_sends_the_files_to_the_bin_keeps_the_title_out_of_every_list_ends_its_grab_and_hand_off_and_tells_both_apps()
    {
        await using var weir = await RecordedAppServer.StartAsync("weir", new[] { "capabilities", "handoff-cancelled" }.Select(RecordedReplies.Weir),
            RecordedAppServer.Answering("DELETE", "/api/v1/intake/handoffs/deluno/", RecordedReplies.Weir("handoff-cancelled").Exchanges[0]));
        await using var jellyfin = await RecordedJellyfinServer.StartAsync();
        await using var world = await RealDiskLibrary.OpenAsync(await ApplicationTestHost.StartAsync());
        var folder = Path.Combine(world.Root, "Sintel (2010)");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Sintel (2010) [WEB 1080p].mkv");
        File.WriteAllBytes(file, Enumerable.Repeat((byte)0x42, 4096).ToArray());
        var added = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title = "Sintel", releaseYear = 2010, imdbId = "tt1727587", monitored = true, libraryId = world.Library.Id, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var movieId = JsonDocument.Parse(await added.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        await world.SqlAsync("movies", $"UPDATE movie_entries SET created_utc = '{DateTimeOffset.UtcNow.AddDays(-365):O}' WHERE id = '{movieId}';");

        var connected = await world.App.Client.PostAsJsonAsync("/api/integrations/processors/connections",
            new { provider = "weir", submissionUrl = weir.BaseUrl + "/api/v1/intake/webhook/deluno", authHeaderName = "X-Webhook-Secret", secret = "a-webhook-secret", isEnabled = true });
        Assert.True(connected.IsSuccessStatusCode, await connected.Content.ReadAsStringAsync());
        var connectionName = JsonDocument.Parse(await connected.Content.ReadAsStringAsync()).RootElement.GetProperty("name").GetString()!;
        var server = await world.App.Client.PostAsJsonAsync("/api/media-servers", new { kind = "jellyfin", name = "Living room", baseUrl = jellyfin.BaseUrl, token = "jf-secret-token-1", isEnabled = true });
        Assert.True(server.IsSuccessStatusCode, await server.Content.ReadAsStringAsync());
        var serverId = JsonDocument.Parse(await server.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        var jobs = world.App.Services.GetRequiredService<IJobQueueRepository>();
        await jobs.RecordDownloadDispatchAsync(world.Library.Id, "movies", "movie", movieId, Release, "Jackett", "client-1", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        var processors = world.App.Services.GetRequiredService<IProcessorRepository>();
        var handoff = await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(world.Library.Id, "movies", "client-1", "queue-item-1", Release, Path.Combine(world.Downloads, Release), connectionName), CancellationToken.None);
        await processors.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);

        var rule = await world.App.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old films", level = "movie", isEnabled = true, steps = new[] { new { type = "condition", op = "keep", condition = "added:beyond:30" } },
            mediaServerIds = new[] { serverId }, removeFromServers = true, waitDays = 1, removalMode = "automatic", checkIntervalHours = 24, expiryAction = "delete"
        });
        Assert.True(rule.IsSuccessStatusCode, await rule.Content.ReadAsStringAsync());
        var ruleId = JsonDocument.Parse(await rule.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        Assert.True((await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null)).IsSuccessStatusCode, "the first check only marks the film");
        Assert.True(File.Exists(file));
        await world.SqlAsync("platform", $"UPDATE retention_marks SET remove_after_utc = '{DateTimeOffset.UtcNow.AddHours(-1):O}';");
        var removed = await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null);
        var summary = await removed.Content.ReadAsStringAsync();
        Assert.True(removed.IsSuccessStatusCode, summary);

        Assert.False(File.Exists(file), "the film left the library folder: " + summary);
        var inBin = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.NotNull(inBin.TitleSnapshotId);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await world.App.Client.GetAsync($"/api/movies/{movieId}")).StatusCode);
        var exclusions = (await world.App.Client.GetFromJsonAsync<JsonElement>("/api/exclusions?mediaType=movies")).EnumerateArray().ToArray();
        Assert.Contains(exclusions, item => item.GetRawText().Contains("Sintel", StringComparison.Ordinal));
        Assert.Contains(weir.Requests, request => request.Method == "DELETE" && request.PathAndQuery.StartsWith("/api/v1/intake/handoffs/deluno/", StringComparison.Ordinal));
        Assert.Empty(weir.Unanswered);
        var ended = await processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.NotEqual(ProcessorHandoffStatuses.Submitted, ended!.Status);
        Assert.Equal(1L, Convert.ToInt64(await world.ScalarAsync("jobs", $"SELECT COUNT(*) FROM download_dispatches WHERE entity_id = '{movieId}' AND archived_utc IS NOT NULL;")));
        var told = Assert.Single(jellyfin.UpdateBodies);
        using (var update = JsonDocument.Parse(told))
        {
            var entry = update.RootElement.GetProperty("Updates").EnumerateArray().Single();
            Assert.Equal("Deleted", entry.GetProperty("UpdateType").GetString());
            Assert.Contains("Sintel", entry.GetProperty("Path").GetString());
        }

        var activity = await world.App.Client.GetStringAsync("/api/activity?take=100");
        Assert.Matches("Sintel was removed from Deluno: [^\"]*retention rule", activity);
    }
}
