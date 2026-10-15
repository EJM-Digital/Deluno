using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Finishing the guided setup, on the real host and its real database: the guide says what it made, Deluno writes one line in
/// Activity in plain words with those details, and the guide is marked completed so the Dashboard stops offering it.
/// </summary>
public sealed class TheGuidedSetupIsRecordedWhenItFinishesTests
{
    [Fact]
    public async Task Completion_writes_Guided_setup_completed_to_Activity_with_what_was_made_and_marks_the_guide_completed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var before = await app.Client.GetFromJsonAsync<JsonElement>("/api/setup/progress");
        Assert.False(before.GetProperty("isCompleted").GetBoolean());

        var posted = await app.Client.PostAsJsonAsync("/api/setup/completed", new
        {
            libraries = new[] { "Movies", "TV Shows" },
            qualityProfiles = new[] { "Balanced 1080p - Balanced library" },
            customFormatCount = 12,
            indexerName = "Torznab",
            clientName = "qBittorrent",
            firstTitle = "Sintel"
        });

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        var progress = await app.Client.GetFromJsonAsync<JsonElement>("/api/setup/progress");
        Assert.True(progress.GetProperty("isCompleted").GetBoolean());
        var activity = await app.Client.GetStringAsync("/api/activity");
        Assert.Contains("Guided setup completed.", activity, StringComparison.Ordinal);
        foreach (var detail in new[] { "Movies", "TV Shows", "Balanced 1080p - Balanced library", "Torznab", "qBittorrent", "Sintel" })
        {
            Assert.Contains(detail, activity, StringComparison.Ordinal);
        }
        Assert.Contains("12", activity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completion_with_nothing_to_say_still_records_the_line_and_completes_the_guide()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var posted = await app.Client.PostAsJsonAsync("/api/setup/completed", new { });

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>("/api/setup/progress")).GetProperty("isCompleted").GetBoolean());
        Assert.Contains("Guided setup completed.", await app.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);
    }
}
