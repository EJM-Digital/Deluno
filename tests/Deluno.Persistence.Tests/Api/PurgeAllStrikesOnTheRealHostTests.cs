using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Purge all strikes": every recorded strike for every download is cleared in one call. The count a person is told is only the
/// downloads they can see; the strikes stay cleared in the stored record; nothing in the download folder or the client is
/// touched; and it is said in Activity.
/// </summary>
public sealed class PurgeAllStrikesOnTheRealHostTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Purging_clears_every_recorded_strike_in_the_stored_record_says_how_many_downloads_were_reset_and_leaves_the_files_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads")).FullName;
        var payload = Path.Combine(downloads, "Arrival.2016.1080p.WEB-DL.mkv");
        await File.WriteAllTextAsync(payload, "still downloading");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            "lib-1", "movies", "movie", "movie-1", "Arrival.2016.1080p.WEB-DL", "Fixture", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordDetectionAsync(dispatchId, "queue-1", 1024, completed: false, CancellationToken.None);
        var health = app.Services.GetRequiredService<IDownloadHealthRepository>();
        await health.RecordDownloadHealthObservationsAsync(
            "client-1", ["queue-1", "queue-2"],
            [Observation("queue-1", "Arrival.2016.1080p.WEB-DL"), Observation("queue-2", "Someone.Elses.Download.2020")],
            Window, TimeSpan.FromHours(24), CancellationToken.None);
        Assert.Equal(2, (await health.ListDownloadHealthRecordsAsync(50, CancellationToken.None)).Count);
        Assert.Contains("queue-1", StoredRecords(app));

        using var response = await app.Client.PostAsync("/api/download-health/purge-strikes", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("purgedCount").GetInt32());
        Assert.Empty(await health.ListDownloadHealthRecordsAsync(50, CancellationToken.None));
        Assert.Equal("[]", StoredRecords(app));
        Assert.True(File.Exists(payload), "purging strikes never touches a download");
        Assert.Contains("All recorded download-health strikes were cleared: 1 download reset to zero.", await app.Client.GetStringAsync("/api/activity?take=100"));
    }

    [Fact]
    public async Task Purging_when_there_are_no_strikes_says_there_was_nothing_to_clear()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsync("/api/download-health/purge-strikes", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("purgedCount").GetInt32());
        Assert.Contains("All recorded download-health strikes were cleared. There was nothing to clear.", await app.Client.GetStringAsync("/api/activity?take=100"));
    }

    [Fact]
    public async Task Purging_needs_sign_in_and_changes_nothing_without_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var health = app.Services.GetRequiredService<IDownloadHealthRepository>();
        await health.RecordDownloadHealthObservationsAsync("client-1", ["queue-1"], [Observation("queue-1", "Arrival.2016")], Window, TimeSpan.FromHours(24), CancellationToken.None);
        using var anonymous = app.CreateAnonymousClient();

        using var response = await anonymous.PostAsync("/api/download-health/purge-strikes", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(await health.ListDownloadHealthRecordsAsync(50, CancellationToken.None));
    }

    // ---------------------------------------------------------------- helpers

    private static DownloadHealthObservation Observation(string queueItemId, string release)
        => new("client-1", queueItemId, release, "no-throughput", "warning", "No new data had arrived since the previous check.");

    /// <summary>The strike record exactly as it sits in the platform database file.</summary>
    private static string StoredRecords(ApplicationTestHost app)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataRoot, "platform.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = 'download-health.records.v2';";
        return (string?)command.ExecuteScalar() ?? string.Empty;
    }
}
