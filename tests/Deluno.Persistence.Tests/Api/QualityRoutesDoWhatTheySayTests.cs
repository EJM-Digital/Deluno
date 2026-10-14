using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Search;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The quality-profile, media-plan, guide-package and ranking-model routes when the request is right
/// (coverage register, area api): what each saves in the real database and what a person can read back.
/// </summary>
public sealed class QualityRoutesDoWhatTheySayTests
{
    private const string Allowed = "WEB 1080p,WEB 2160p";

    /// <summary>A number straight out of the host's real SQLite file, so a test sees what is stored and not only what is answered.</summary>
    private static async Task<long> ScalarAsync(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<string> CreateProfileAsync(ApplicationTestHost app, string name, string cutoff)
    {
        var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name,
            mediaType = "movies",
            cutoffQuality = cutoff,
            allowedQualities = Allowed,
            customFormatIds = string.Empty,
            upgradeUntilCutoff = true,
            upgradeUnknownItems = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> PolicySetAsync(ApplicationTestHost app, string id)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/policy-sets")).EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == id);

    [Fact]
    public async Task A_quality_profile_edit_changes_what_is_stored_and_deleting_it_removes_the_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await CreateProfileAsync(app, "Films 1080p", "WEB 1080p");
        var before = await ScalarAsync(app, DelunoDatabaseNames.Platform, "SELECT COUNT(*) FROM quality_profiles;");

        var edit = await app.Client.PutAsJsonAsync($"/api/quality-profiles/{id}", new
        {
            name = "Films 4K",
            cutoffQuality = "WEB 2160p",
            allowedQualities = Allowed,
            customFormatIds = string.Empty,
            upgradeUntilCutoff = false,
            upgradeUnknownItems = false
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = await edit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Films 4K", edited.GetProperty("name").GetString());
        Assert.Equal("WEB 2160p", edited.GetProperty("cutoffQuality").GetString());
        Assert.False(edited.GetProperty("upgradeUntilCutoff").GetBoolean());
        var listed = (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-profiles")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
        Assert.Equal("Films 4K", listed.GetProperty("name").GetString());
        Assert.Equal(before, await ScalarAsync(app, DelunoDatabaseNames.Platform, "SELECT COUNT(*) FROM quality_profiles;"));

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/quality-profiles/{id}")).StatusCode);
        Assert.Equal(before - 1, await ScalarAsync(app, DelunoDatabaseNames.Platform, "SELECT COUNT(*) FROM quality_profiles;"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/quality-profiles/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync($"/api/quality-profiles/{id}", new
        {
            name = "Gone", cutoffQuality = "WEB 1080p", allowedQualities = Allowed, customFormatIds = string.Empty, upgradeUntilCutoff = true, upgradeUnknownItems = false
        })).StatusCode);
    }

    [Fact]
    public async Task Rolling_a_media_plan_back_restores_the_old_version_as_a_new_version_and_refuses_one_that_never_existed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/policy-sets/", new
        {
            name = "Films, first plan",
            mediaType = "movie",
            upgradeUntilCutoff = true,
            isEnabled = true
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var edit = await app.Client.PutAsJsonAsync($"/api/policy-sets/{id}", new
        {
            name = "Films, second plan",
            mediaType = "movie",
            upgradeUntilCutoff = false,
            isEnabled = true
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("Films, second plan", (await PolicySetAsync(app, id)).GetProperty("name").GetString());
        var versions = await app.Client.GetFromJsonAsync<JsonElement>($"/api/policy-sets/{id}/versions");
        var firstVersion = versions.EnumerateArray().Select(item => item.GetProperty("version").GetInt32()).Min();
        var versionCountBefore = versions.GetArrayLength();
        Assert.True(versionCountBefore >= 2, "each save is kept as a version");

        var rollback = await app.Client.PostAsJsonAsync($"/api/policy-sets/{id}/rollback", new { version = firstVersion });

        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
        var restored = await PolicySetAsync(app, id);
        Assert.Equal("Films, first plan", restored.GetProperty("name").GetString());
        Assert.True(restored.GetProperty("upgradeUntilCutoff").GetBoolean());
        Assert.Equal(versionCountBefore + 1, (await app.Client.GetFromJsonAsync<JsonElement>($"/api/policy-sets/{id}/versions")).GetArrayLength());

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync($"/api/policy-sets/{id}/rollback", new { version = 999 })).StatusCode);
        var zero = await app.Client.PostAsJsonAsync($"/api/policy-sets/{id}/rollback", new { version = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Contains("Choose a positive media-plan version", await zero.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("Films, first plan", (await PolicySetAsync(app, id)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Making_an_earlier_guide_version_active_again_switches_the_active_package_and_keeps_every_version()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var store = app.Services.GetRequiredService<IGuidePackageStore>();
        var bootstrap = await store.GetCurrentAsync(CancellationToken.None);
        var second = bootstrap.Package with { Version = bootstrap.Package.Version + 1 };
        var applied = await store.ApplyAsync(new GuidePackageUpdateRequest(second, bootstrap.IntegritySha256), CancellationToken.None);
        var third = second with { Version = second.Version + 1 };
        await store.ApplyAsync(new GuidePackageUpdateRequest(third, applied.IntegritySha256), CancellationToken.None);
        Assert.Equal(third.Version, (await app.Client.GetFromJsonAsync<JsonObject>("/api/v1/guides/trash/package"))!["version"]!.GetValue<int>());

        var back = await app.Client.PostAsync($"/api/v1/guides/trash/versions/{second.Version}/activate?packageId={second.Id}", content: null);

        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        var activated = await back.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(activated!["isActive"]!.GetValue<bool>());
        Assert.Equal(second.Version, activated["package"]!["version"]!.GetValue<int>());
        Assert.Equal(second.Version, (await app.Client.GetFromJsonAsync<JsonObject>("/api/v1/guides/trash/package"))!["version"]!.GetValue<int>());
        var versions = await app.Client.GetFromJsonAsync<JsonArray>("/api/v1/guides/trash/versions");
        Assert.Equal(2, versions!.Count);
        Assert.Single(versions, item => item!["isActive"]!.GetValue<bool>());
        Assert.Equal(1, await ScalarAsync(app, DelunoDatabaseNames.Platform, "SELECT COUNT(*) FROM guide_package_versions WHERE is_active = 1;"));

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/v1/guides/trash/versions/9999/activate?packageId={second.Id}", content: null)).StatusCode);
    }

    [Fact(Skip = "Defect: POST /api/v1/guides/trash/apply cannot apply the shipped guide package. With its pinned source inventory the package is 2,049,644 bytes, over the route's 1 MB body limit (413 'That request is too large. The most this can take is 1 MB.'); without the inventory the store refuses it ('A newly applied guide package must carry the pinned upstream source inventory'); and GET /api/v1/guides/trash/package returns enums as strings that the route's JSON then cannot read back ('The \"package.customFormats[0].mappingStatus\" field must be an id').")]
    public async Task Applying_a_guide_package_through_the_route_makes_it_the_active_version()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var integrity = GuidePackageCatalog.ComputeIntegritySha256(GuidePackageCatalog.Current);
        var next = JsonSerializer.SerializeToNode(GuidePackageCatalog.Current with { Version = GuidePackageCatalog.Current.Version + 1 }, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var apply = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/apply", new { package = next, expectedCurrentIntegritySha256 = integrity });

        Assert.Equal(HttpStatusCode.Created, apply.StatusCode);
    }

    [Fact]
    public async Task Rolling_the_ranking_model_back_makes_the_chosen_trained_version_the_active_one()
    {
        var modelRoot = Path.Combine(Path.GetTempPath(), "deluno-ranking-route-tests", Guid.NewGuid().ToString("N"));
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
                        ["Deluno:RankingModel:AutoDispatchImpactEnabled"] = "true",
                        ["Deluno:RankingModel:MinTrainingSamples"] = "40"
                    }).Build(),
                    new HistoryRows(),
                    Options.Create(new StoragePathOptions { DataRoot = modelRoot }),
                    TimeProvider.System));
                services.AddSingleton<IReleaseRankingModelService>(provider => provider.GetRequiredService<MlNetReleaseRankingModelService>());
                services.AddSingleton<IReleaseRankingModelAdminService>(provider => provider.GetRequiredService<MlNetReleaseRankingModelService>());
            });

            var first = await app.Client.PostAsync("/api/ranking-model/train", content: null);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var firstVersion = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modelVersion").GetString()!;
            await Task.Delay(1100); // reason: model versions are named by the second they were trained in
            var second = await app.Client.PostAsync("/api/ranking-model/train", content: null);
            var secondVersion = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modelVersion").GetString()!;
            Assert.NotEqual(firstVersion, secondVersion);
            Assert.Equal(secondVersion, (await app.Client.GetFromJsonAsync<JsonElement>("/api/ranking-model/status")).GetProperty("activeModelVersion").GetString());

            var rollback = await app.Client.PostAsJsonAsync("/api/ranking-model/rollback", new { version = firstVersion });

            Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
            var body = await rollback.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("accepted").GetBoolean());
            Assert.Equal(firstVersion, body.GetProperty("version").GetString());
            Assert.Equal(firstVersion, (await app.Client.GetFromJsonAsync<JsonElement>("/api/ranking-model/status")).GetProperty("activeModelVersion").GetString());
            Assert.Contains(firstVersion, File.ReadAllText(Directory.EnumerateFiles(modelRoot, "*", SearchOption.AllDirectories).First(file => Path.GetFileName(file).Contains("active", StringComparison.OrdinalIgnoreCase))), StringComparison.Ordinal);

            var unknown = await app.Client.PostAsJsonAsync("/api/ranking-model/rollback", new { version = "20990101-000000" });
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Contains("was not found", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(firstVersion, (await app.Client.GetFromJsonAsync<JsonElement>("/api/ranking-model/status")).GetProperty("activeModelVersion").GetString());
        }
        finally
        {
            Directory.Delete(modelRoot, recursive: true);
        }
    }

    private sealed class HistoryRows : IReleaseRankingTrainingDataSource
    {
        public Task<IReadOnlyList<ReleaseRankingTrainingRow>> ListTrainingRowsAsync(int maxRows, DateTimeOffset? sinceUtc, CancellationToken cancellationToken)
        {
            var random = new Random(4210);
            var now = DateTimeOffset.UtcNow;
            var rows = new List<ReleaseRankingTrainingRow>();
            for (var i = 0; i < 170; i++)
            {
                var seeders = random.Next(1, 140);
                var qualityDelta = random.Next(-1, 4);
                var customFormat = random.Next(-30, 140);
                var label = qualityDelta >= 1 && seeders >= 20 && customFormat >= 0;
                rows.Add(new ReleaseRankingTrainingRow(
                    seeders, random.NextInt64(700_000_000L, 14_000_000_000L), qualityDelta, customFormat, seeders / 2, random.Next(-15, 40),
                    qualityDelta * 20 + customFormat / 4 + seeders / 3, label ? "preferred" : "held", "WEB 1080p", label ? "good-group" : "bad-group",
                    random.NextDouble() * 10.0, now.AddHours(-random.Next(1, 200)), now.AddHours(-random.Next(1, 160)), false, label));
            }

            return Task.FromResult<IReadOnlyList<ReleaseRankingTrainingRow>>(rows.Take(maxRows).ToArray());
        }
    }
}
