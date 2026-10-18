using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// POST /api/intelligent-routing/recommend-release over the real host and the real dispatch database: the score always lands between 0
/// and 100, its label says what the score says, and the indexer and the download client the release comes from move it by the record
/// they have earned (the recent sends that were imported against those that failed in the client), while one with too short a record
/// moves nothing.
/// </summary>
public sealed class TheRecommendReleaseRouteWeighsTheModelAndTheSourcesRecordTests
{
    private static object Ask(string? indexer = null, string? client = null, int? seeders = 40, int qualityDelta = 0, int customFormat = 0)
        => new
        {
            releaseName = "Sintel.2010.1080p.BluRay.x264-GRP", seeders, sizeBytes = 8_000_000_000L, qualityDelta, customFormatScore = customFormat,
            sourcePriorityScore = 100, indexerName = indexer, downloadClientId = client, estimatedBitrateMbps = 20.0, releaseAgeHours = 12.0
        };

    private static async Task<JsonElement> RecommendAsync(ApplicationTestHost app, object request)
    {
        var response = await app.Client.PostAsJsonAsync("/api/intelligent-routing/recommend-release", request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task SendsAsync(ApplicationTestHost app, string clientId, string indexerName, string outcome, int count)
    {
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        for (var i = 0; i < count; i++)
        {
            var id = await jobs.RecordDownloadDispatchAsync(
                "library-1", "movie", "movie", "movie-" + Guid.NewGuid().ToString("n"), "Release." + Guid.NewGuid().ToString("n"),
                indexerName, clientId, clientId, "sent", null, cancellationToken: CancellationToken.None);
            var importStatus = outcome == "imported" ? "imported" : "failed";
            var code = outcome == "imported" ? "NULL" : "'client-reported-failure'";
            await ApiScenes.ExecuteAsync(app, "jobs",
                $"UPDATE download_dispatches SET grab_status = 'succeeded', import_status = '{importStatus}', import_failure_code = {code}, decision_score = 50, " +
                $"grab_attempted_utc = '{DateTimeOffset.UtcNow:O}', import_completed_utc = '{DateTimeOffset.UtcNow:O}' WHERE id = '{id}';");
        }
    }

    [Fact]
    public async Task The_score_is_between_nought_and_a_hundred_whatever_is_asked_and_the_label_says_what_the_score_says()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var asks = new[]
        {
            Ask(seeders: 5000, qualityDelta: 5, customFormat: 500), Ask(seeders: 0, qualityDelta: -5, customFormat: -500),
            Ask(seeders: null), Ask(), Ask(seeders: 1, qualityDelta: -1)
        };

        foreach (var ask in asks)
        {
            var answer = await RecommendAsync(app, ask);
            var score = answer.GetProperty("recommendationScore").GetInt32();
            Assert.InRange(score, 0, 100);
            Assert.Equal(score >= 75 ? "strong" : score >= 55 ? "review" : "avoid", answer.GetProperty("recommendationLabel").GetString());
            Assert.Contains($"{score}/100", answer.GetProperty("summary").GetString());
            Assert.Contains("Sintel.2010.1080p.BluRay.x264-GRP", answer.GetProperty("summary").GetString());
        }
    }

    [Fact]
    public async Task A_better_quality_than_the_file_held_scores_higher_than_the_same_release_that_is_no_better()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var better = (await RecommendAsync(app, Ask(qualityDelta: 2))).GetProperty("recommendationScore").GetInt32();
        var same = (await RecommendAsync(app, Ask(qualityDelta: 0))).GetProperty("recommendationScore").GetInt32();

        Assert.True(better > same, $"better {better}, same {same}");
    }

    [Fact]
    public async Task A_client_and_an_indexer_with_a_good_record_lift_the_score_and_ones_with_a_bad_record_lower_it_and_a_short_record_moves_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SendsAsync(app, "client-good", "Nebula", "imported", 4);
        await SendsAsync(app, "client-bad", "Murky", "failed", 4);
        await SendsAsync(app, "client-new", "Newcomer", "imported", 2);

        var good = await RecommendAsync(app, Ask("Nebula", "client-good"));
        var bad = await RecommendAsync(app, Ask("Murky", "client-bad"));
        var unknown = await RecommendAsync(app, Ask("Nobody", "client-unknown"));
        var tooFew = await RecommendAsync(app, Ask("Newcomer", "client-new"));

        Assert.Equal(1.0, good.GetProperty("downloadClientSuccessRate").GetDouble());
        Assert.Equal(1.0, good.GetProperty("indexerSuccessRate").GetDouble());
        Assert.Equal(0.0, bad.GetProperty("downloadClientSuccessRate").GetDouble());
        Assert.Equal(0.0, bad.GetProperty("indexerSuccessRate").GetDouble());
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("downloadClientSuccessRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("indexerSuccessRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, tooFew.GetProperty("downloadClientSuccessRate").ValueKind);
        var goodScore = good.GetProperty("recommendationScore").GetInt32();
        var badScore = bad.GetProperty("recommendationScore").GetInt32();
        var unknownScore = unknown.GetProperty("recommendationScore").GetInt32();
        Assert.True(goodScore > unknownScore && unknownScore > badScore, $"good {goodScore}, unknown {unknownScore}, bad {badScore}");
        Assert.Equal(unknownScore, tooFew.GetProperty("recommendationScore").GetInt32());
    }
}
