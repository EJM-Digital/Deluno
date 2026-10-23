using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// GET /api/intelligent-routing/snapshot and /anomalies over the real host and the real dispatch database (coverage register,
/// engines): the snapshot names the quality, the average rule score and the release groups behind the imports Deluno has done, the
/// share of recent counted sends imported per indexer (by its id) and per client, and each client's record; the anomalies report a
/// grab-failure spike only when the last day has at least ten grabs, the week before at least twenty and the failure rate has at least
/// doubled, and a downgrade pattern only when at least eight recent dispatches had 15 percent or more with a negative quality delta.
/// Refs #1296.
/// </summary>
public sealed class TheRoutingSnapshotAndAnomalyRoutesReadTheDispatchRecordTests
{
    private static async Task<string> DispatchAsync(ApplicationTestHost app, string clientId, string indexerName, string set)
    {
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var id = await jobs.RecordDownloadDispatchAsync(
            "library-1", "movie", "movie", "movie-" + Guid.NewGuid().ToString("n"), "Release." + Guid.NewGuid().ToString("n"),
            indexerName, clientId, clientId, "sent", null, cancellationToken: CancellationToken.None);
        await ApiScenes.ExecuteAsync(app, "jobs", $"UPDATE download_dispatches SET {set} WHERE id = '{id}';");
        return id;
    }

    private static string Utc(TimeSpan ago) => DateTimeOffset.UtcNow.Subtract(ago).ToString("O");

    private static async Task GrabsAsync(ApplicationTestHost app, int total, int failed, TimeSpan ago)
    {
        for (var i = 0; i < total; i++)
        {
            await DispatchAsync(app, "client-x", "Indexer X", $"grab_status = '{(i < failed ? "failed" : "succeeded")}', grab_attempted_utc = '{Utc(ago)}'");
        }
    }

    private static async Task<JsonElement> AnomaliesAsync(ApplicationTestHost app)
        => await app.Client.GetFromJsonAsync<JsonElement>("/api/intelligent-routing/anomalies");

    private static string[] Codes(JsonElement body)
        => [.. body.GetProperty("anomalies").EnumerateArray().Select(item => item.GetProperty("code").GetString()!)];

    [Fact]
    public async Task A_grab_failure_spike_is_reported_only_when_the_last_day_and_the_week_before_have_enough_grabs_and_the_rate_has_at_least_doubled()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.Equal(0, (await AnomaliesAsync(app)).GetProperty("count").GetInt32());

        // The week before: 20 grabs, 2 failed (10 percent). The last day: 10 grabs, 5 failed (50 percent).
        await GrabsAsync(app, 20, 2, TimeSpan.FromDays(3));
        await GrabsAsync(app, 9, 5, TimeSpan.FromHours(2));
        Assert.Empty(Codes(await AnomaliesAsync(app)));

        await GrabsAsync(app, 1, 0, TimeSpan.FromHours(2));
        var spike = await AnomaliesAsync(app);
        Assert.Equal(["grab-failure-spike"], Codes(spike));
        var alert = Assert.Single(spike.GetProperty("anomalies").EnumerateArray());
        Assert.Equal("warning", alert.GetProperty("severity").GetString());
        Assert.Contains("50.0 %", alert.GetProperty("details").GetString());
        Assert.Contains("10.0 %", alert.GetProperty("details").GetString());
    }

    [Fact]
    public async Task A_failure_rate_that_has_not_doubled_or_a_week_with_no_failures_or_too_few_prior_grabs_is_no_spike()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        // Prior 4 of 20 failed (20 percent); last day 3 of 10 (30 percent): only one and a half times as many.
        await GrabsAsync(app, 20, 4, TimeSpan.FromDays(3));
        await GrabsAsync(app, 10, 3, TimeSpan.FromHours(2));
        Assert.Empty(Codes(await AnomaliesAsync(app)));

        // A quiet week before (no failures) has nothing to compare with.
        await ApiScenes.ExecuteAsync(app, "jobs", "UPDATE download_dispatches SET grab_status = 'succeeded' WHERE grab_attempted_utc < '" + Utc(TimeSpan.FromDays(1)) + "';");
        await ApiScenes.ExecuteAsync(app, "jobs", "UPDATE download_dispatches SET grab_status = 'failed' WHERE grab_attempted_utc >= '" + Utc(TimeSpan.FromDays(1)) + "';");
        Assert.Empty(Codes(await AnomaliesAsync(app)));

        // Too few grabs in the week before to judge.
        await ApiScenes.ExecuteAsync(app, "jobs", "UPDATE download_dispatches SET status = 'archived' WHERE grab_attempted_utc < '" + Utc(TimeSpan.FromDays(1)) + "';");
        await GrabsAsync(app, 19, 5, TimeSpan.FromDays(3));
        Assert.Empty(Codes(await AnomaliesAsync(app)));
    }

    [Fact]
    public async Task A_downgrade_pattern_needs_at_least_eight_recent_dispatches_and_15_percent_of_them_with_a_negative_quality_delta()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        for (var i = 0; i < 6; i++)
        {
            await DispatchAsync(app, "client-x", "Indexer X", "decision_score = 50, decision_quality_delta = -1");
        }

        // Seven dispatches, all downgrades: too few to judge.
        await DispatchAsync(app, "client-x", "Indexer X", "decision_score = 50, decision_quality_delta = -1");
        Assert.Empty(Codes(await AnomaliesAsync(app)));

        // Eight, one downgrade: 12.5 percent is under the line.
        await ApiScenes.ExecuteAsync(app, "jobs", "UPDATE download_dispatches SET decision_quality_delta = 1;");
        await DispatchAsync(app, "client-x", "Indexer X", "decision_score = 50, decision_quality_delta = -1");
        Assert.Empty(Codes(await AnomaliesAsync(app)));

        // Eight, two downgrades: 25 percent.
        await ApiScenes.ExecuteAsync(app, "jobs", "UPDATE download_dispatches SET decision_quality_delta = -1 WHERE id IN (SELECT id FROM download_dispatches WHERE decision_quality_delta = 1 LIMIT 1);");
        var found = await AnomaliesAsync(app);
        Assert.Equal(["quality-downgrade-pattern"], Codes(found));
        Assert.Contains("2/8", Assert.Single(found.GetProperty("anomalies").EnumerateArray()).GetProperty("details").GetString());

        // Archived dispatches no longer count.
        await ApiScenes.ExecuteAsync(app, "jobs", "UPDATE download_dispatches SET status = 'archived' WHERE decision_quality_delta = 1;");
        Assert.Empty(Codes(await AnomaliesAsync(app)));
    }

    [Fact]
    public async Task The_snapshot_names_the_usual_quality_rule_score_and_release_groups_and_each_sources_record_of_imports()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        string Imported(string quality, int score, string group) =>
            $"grab_status = 'succeeded', import_status = 'imported', import_completed_utc = '{Utc(TimeSpan.FromDays(1))}', decision_score = 50, decision_quality = '{quality}', decision_custom_format_score = {score}, decision_release_group = '{group}'";
        for (var i = 0; i < 3; i++)
        {
            await DispatchAsync(app, "client-good", "Nebula", Imported("Bluray 1080p", 100, "GRP-A") + ", indexer_id = 'ix-nebula'");
        }

        await DispatchAsync(app, "client-good", "Nebula", Imported("WEB 1080p", 200, "GRP-B") + ", indexer_id = 'ix-nebula'");
        // A failure in the client counts against its client and indexer; a dispatch with no outcome yet counts for nothing.
        await DispatchAsync(app, "client-good", "Nebula", $"grab_status = 'succeeded', import_status = 'failed', import_failure_code = 'client-reported-failure', import_completed_utc = '{Utc(TimeSpan.FromDays(1))}', decision_score = 50, indexer_id = 'ix-nebula'");
        await DispatchAsync(app, "client-good", "Nebula", "decision_score = 50, indexer_id = 'ix-nebula'");
        // Two counted sends are no record at all.
        for (var i = 0; i < 2; i++)
        {
            await DispatchAsync(app, "client-new", "Newcomer", Imported("Bluray 1080p", 0, "GRP-C"));
        }

        var snapshot = await app.Client.GetFromJsonAsync<JsonElement>("/api/intelligent-routing/snapshot");

        var preferences = snapshot.GetProperty("preferences");
        Assert.Equal("Bluray 1080p", preferences.GetProperty("preferredQuality").GetString());
        Assert.Equal(Math.Round((3 * 100 + 200 + 0 + 0) / 6.0, 2), preferences.GetProperty("averageCustomFormatScore").GetDouble());
        Assert.Equal("GRP-A", preferences.GetProperty("preferredReleaseGroups")[0].GetString());
        Assert.Equal(0.8, snapshot.GetProperty("downloadClientSuccessRates").GetProperty("client-good").GetDouble());
        Assert.Equal(0.8, snapshot.GetProperty("indexerSuccessRates").GetProperty("ix-nebula").GetDouble());
        Assert.False(snapshot.GetProperty("downloadClientSuccessRates").TryGetProperty("client-new", out _));
        var record = snapshot.GetProperty("downloadClientRecords").GetProperty("client-good");
        Assert.Equal(5, record.GetProperty("counted").GetInt32());
        Assert.Equal(4, record.GetProperty("imported").GetInt32());
        Assert.Equal(30, record.GetProperty("windowDays").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, record.GetProperty("lastSendUtc").ValueKind);
        Assert.False(snapshot.GetProperty("downloadClientRecords").TryGetProperty("client-new", out _));
    }
}
