using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The ranking model's status and train routes answer every field they promise, before and after a model has been trained, and the
/// training data it learns from is read from the real dispatch database (coverage register, engines): only sends with a decision
/// score, not archived, newer than the look-back, newest first, and only those whose outcome is final (imported is a positive;
/// a failed grab or a failed import is a negative; anything else is left out). Refs #1296.
/// </summary>
public sealed class TheRankingModelRoutesAndItsTrainingDataAnswerTheirFieldsTests
{
    private static async Task<string> DispatchAsync(ApplicationTestHost app, string set)
    {
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var id = await jobs.RecordDownloadDispatchAsync(
            "library-1", "movie", "movie", "movie-" + Guid.NewGuid().ToString("n"), "Release." + Guid.NewGuid().ToString("n"),
            "Indexer", "client-1", "Client", "sent", null, cancellationToken: CancellationToken.None);
        await ApiScenes.ExecuteAsync(app, "jobs", $"UPDATE download_dispatches SET {set} WHERE id = '{id}';");
        return id;
    }

    private static string Ago(TimeSpan ago) => DateTimeOffset.UtcNow.Subtract(ago).ToString("O");

    [Fact]
    public async Task Training_data_is_the_scored_unarchived_recent_sends_newest_first_labelled_by_their_final_outcome()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var source = app.Services.GetRequiredService<IReleaseRankingTrainingDataSource>();
        await DispatchAsync(app, $"decision_score = 10, decision_seeders = 11, import_status = 'imported', created_utc = '{Ago(TimeSpan.FromDays(3))}'");
        await DispatchAsync(app, $"decision_score = 20, decision_seeders = 22, grab_status = 'failed', created_utc = '{Ago(TimeSpan.FromDays(2))}'");
        await DispatchAsync(app, $"decision_score = 30, decision_seeders = 33, grab_status = 'succeeded', import_status = 'failed', created_utc = '{Ago(TimeSpan.FromDays(1))}'");
        // Left out: an outcome that is not final yet, no decision score, archived, and older than the look-back.
        await DispatchAsync(app, $"decision_score = 40, grab_status = 'succeeded', import_status = 'pending', created_utc = '{Ago(TimeSpan.FromHours(5))}'");
        await DispatchAsync(app, $"import_status = 'imported', created_utc = '{Ago(TimeSpan.FromHours(4))}'");
        await DispatchAsync(app, $"decision_score = 50, import_status = 'imported', status = 'archived', created_utc = '{Ago(TimeSpan.FromHours(3))}'");
        await DispatchAsync(app, $"decision_score = 60, import_status = 'imported', created_utc = '{Ago(TimeSpan.FromDays(40))}'");

        var recent = await source.ListTrainingRowsAsync(1000, DateTimeOffset.UtcNow.AddDays(-14), CancellationToken.None);
        var everything = await source.ListTrainingRowsAsync(1000, null, CancellationToken.None);

        Assert.Equal([33, 22, 11], recent.Select(row => row.Seeders));
        Assert.Equal([false, false, true], recent.Select(row => row.Label));
        Assert.Equal([30, 20, 10], recent.Select(row => row.DecisionScore));
        Assert.Equal(4, everything.Count);
        Assert.Equal(60, everything[^1].DecisionScore);
        Assert.True(everything[^1].Label);
    }

    [Fact]
    public async Task The_status_and_train_routes_answer_every_field_before_and_after_a_model_is_trained()
    {
        var modelRoot = Path.Combine(Path.GetTempPath(), "deluno-ranking-field-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(modelRoot);
        try
        {
            await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            {
                services.RemoveAll<MlNetReleaseRankingModelService>();
                services.RemoveAll<IReleaseRankingModelService>();
                services.RemoveAll<IReleaseRankingModelAdminService>();
                services.AddSingleton(provider => new MlNetReleaseRankingModelService(
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Deluno:RankingModel:Enabled"] = "true",
                        ["Deluno:RankingModel:AutoDispatchImpactEnabled"] = "false",
                        ["Deluno:RankingModel:MaxAbsoluteBoost"] = "12",
                        ["Deluno:RankingModel:Mode"] = "offline",
                        ["Deluno:RankingModel:MinTrainingSamples"] = "40"
                    }).Build(),
                    new SomeHistory(),
                    Options.Create(new StoragePathOptions { DataRoot = modelRoot }),
                    TimeProvider.System));
                services.AddSingleton<IReleaseRankingModelService>(provider => provider.GetRequiredService<MlNetReleaseRankingModelService>());
                services.AddSingleton<IReleaseRankingModelAdminService>(provider => provider.GetRequiredService<MlNetReleaseRankingModelService>());
            });

            var before = await app.Client.GetFromJsonAsync<JsonElement>("/api/ranking-model/status");
            Assert.True(before.GetProperty("enabled").GetBoolean());
            Assert.False(before.GetProperty("autoDispatchImpactEnabled").GetBoolean());
            Assert.Equal(12, before.GetProperty("maxAbsoluteBoost").GetInt32());
            Assert.Equal("offline", before.GetProperty("mode").GetString());
            Assert.False(string.IsNullOrWhiteSpace(before.GetProperty("notes").GetString()));
            Assert.False(before.GetProperty("modelLoaded").GetBoolean());
            Assert.Equal(JsonValueKind.Null, before.GetProperty("activeModelVersion").ValueKind);
            Assert.Equal(JsonValueKind.Null, before.GetProperty("lastTrainedUtc").ValueKind);
            Assert.Equal(0, before.GetProperty("trainingSampleCount").GetInt32());

            var trained = await app.Client.PostAsync("/api/ranking-model/train", content: null);

            Assert.Equal(HttpStatusCode.OK, trained.StatusCode);
            var result = await trained.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(result.GetProperty("success").GetBoolean());
            var version = result.GetProperty("modelVersion").GetString()!;
            Assert.StartsWith("rm-", version, StringComparison.Ordinal);
            Assert.Equal(120, result.GetProperty("sampleCount").GetInt32());
            Assert.InRange(result.GetProperty("auc").GetDouble(), 0, 1);
            Assert.InRange(result.GetProperty("accuracy").GetDouble(), 0, 1);

            var after = await app.Client.GetFromJsonAsync<JsonElement>("/api/ranking-model/status");
            Assert.True(after.GetProperty("modelLoaded").GetBoolean());
            Assert.Equal(version, after.GetProperty("activeModelVersion").GetString());
            Assert.NotEqual(JsonValueKind.Null, after.GetProperty("lastTrainedUtc").ValueKind);
            Assert.Equal(120, after.GetProperty("trainingSampleCount").GetInt32());
            Assert.Equal(result.GetProperty("auc").GetDouble(), after.GetProperty("lastAuc").GetDouble());
            Assert.Equal(result.GetProperty("accuracy").GetDouble(), after.GetProperty("lastAccuracy").GetDouble());
            Assert.Contains(version, after.GetProperty("availableVersions").EnumerateArray().Select(item => item.GetString()));
        }
        finally
        {
            Directory.Delete(modelRoot, recursive: true);
        }
    }

    private sealed class SomeHistory : IReleaseRankingTrainingDataSource
    {
        public Task<IReadOnlyList<ReleaseRankingTrainingRow>> ListTrainingRowsAsync(int maxRows, DateTimeOffset? sinceUtc, CancellationToken cancellationToken)
        {
            var random = new Random(4210);
            var now = DateTimeOffset.UtcNow;
            var rows = Enumerable.Range(0, 120).Select(_ =>
            {
                var seeders = random.Next(1, 140);
                var delta = random.Next(-1, 4);
                var label = delta >= 1 && seeders >= 20;
                return new ReleaseRankingTrainingRow(
                    seeders, random.NextInt64(700_000_000L, 14_000_000_000L), delta, random.Next(-30, 140), seeders / 2, random.Next(-15, 40),
                    delta * 20 + seeders / 3, label ? "preferred" : "held", "WEB 1080p", label ? "good" : "bad", random.NextDouble() * 10.0,
                    now.AddHours(-random.Next(1, 200)), now.AddHours(-random.Next(1, 160)), false, label);
            }).ToArray();
            return Task.FromResult<IReadOnlyList<ReleaseRankingTrainingRow>>(rows);
        }
    }
}
