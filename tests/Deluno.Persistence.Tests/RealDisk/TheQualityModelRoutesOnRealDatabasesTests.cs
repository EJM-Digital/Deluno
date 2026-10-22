using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The quality model (the ladder of tiers with their size rules and the upgrade-stop policy) on the real application and
/// its real database files: what is read when the stored model is unreadable or lags behind the catalogue, what a save
/// keeps and refuses, how the saved ladder lands in the film and show catalogues' rank tables, and what a reset puts back.
/// Refs #1296.
/// </summary>
public sealed class TheQualityModelRoutesOnRealDatabasesTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;
    private const long Megabyte = 1024L * 1024;

    private static async Task<string?> StoredAsync(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task SetStoredModelAsync(ApplicationTestHost app, string json)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO system_settings (setting_key, setting_value, updated_utc) VALUES ('quality.model.v2', @json, @now) ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value;";
        command.Parameters.AddWithValue("@json", json);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<JsonObject> ModelAsync(ApplicationTestHost app)
        => JsonNode.Parse(await app.Client.GetStringAsync("/api/quality-model"))!.AsObject();

    private static JsonObject Tier(JsonObject model, string name)
        => model["tiers"]!.AsArray().Select(node => node!.AsObject()).Single(tier => tier["name"]!.GetValue<string>() == name);

    private static string[] Names(JsonObject model)
        => model["tiers"]!.AsArray().Select(node => node!["name"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task A_stored_model_that_cannot_be_read_falls_back_to_the_shipped_tiers()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var shipped = Names(await ModelAsync(app));

        await SetStoredModelAsync(app, "this is not a model");
        Assert.Equal(shipped, Names(await ModelAsync(app)));

        await SetStoredModelAsync(app, """{"version":"quality-model/v2","tiers":[],"upgradeStop":{"stopWhenCutoffMet":true,"requireCustomFormatGainForSameQuality":true}}""");
        Assert.Equal(shipped, Names(await ModelAsync(app)));
    }

    [Fact]
    public async Task A_saved_model_missing_tiers_added_to_the_catalogue_since_gets_them_at_their_defaults_and_keeps_what_was_tuned()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var shipped = await ModelAsync(app);
        var oneTier = Tier(shipped, "WEB 1080p").DeepClone().AsObject();
        oneTier["movieMaxGb"] = 77.0;
        var stored = new JsonObject
        {
            ["version"] = "quality-model/v2",
            ["tiers"] = new JsonArray(oneTier),
            ["upgradeStop"] = shipped["upgradeStop"]!.DeepClone()
        };
        await SetStoredModelAsync(app, stored.ToJsonString());

        var read = await ModelAsync(app);

        Assert.Equal(Names(shipped).Order().ToArray(), Names(read).Order().ToArray());
        Assert.Equal(77.0, Tier(read, "WEB 1080p")["movieMaxGb"]!.GetValue<double>());
        Assert.Equal(
            Tier(shipped, "Bluray 1080p")["movieMaxGb"]!.GetValue<double>(),
            Tier(read, "Bluray 1080p")["movieMaxGb"]!.GetValue<double>());
    }

    [Fact]
    public async Task A_save_keeps_the_edited_numbers_and_name_and_the_upgrade_stop_and_a_save_that_omits_them_keeps_what_was_there()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var model = await ModelAsync(app);
        var tier = Tier(model, "WEB 1080p");
        tier["movieMinGb"] = 2.0;
        tier["moviePreferredGb"] = 7.0;
        tier["movieMaxGb"] = 15.0;
        tier["episodeMinMb"] = 300.0;
        tier["episodePreferredMb"] = 900.0;
        tier["episodeMaxMb"] = 2500.0;
        model["upgradeStop"] = new JsonObject { ["stopWhenCutoffMet"] = false, ["requireCustomFormatGainForSameQuality"] = false };

        var saved = await app.Client.PutAsJsonAsync("/api/quality-model", new { tiers = model["tiers"], upgradeStop = model["upgradeStop"] });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var read = await ModelAsync(app);
        var readTier = Tier(read, "WEB 1080p");
        Assert.Equal(2.0, readTier["movieMinGb"]!.GetValue<double>());
        Assert.Equal(7.0, readTier["moviePreferredGb"]!.GetValue<double>());
        Assert.Equal(15.0, readTier["movieMaxGb"]!.GetValue<double>());
        Assert.Equal(300.0, readTier["episodeMinMb"]!.GetValue<double>());
        Assert.Equal(900.0, readTier["episodePreferredMb"]!.GetValue<double>());
        Assert.Equal(2500.0, readTier["episodeMaxMb"]!.GetValue<double>());
        Assert.False(read["upgradeStop"]!["stopWhenCutoffMet"]!.GetValue<bool>());
        Assert.False(read["upgradeStop"]!["requireCustomFormatGainForSameQuality"]!.GetValue<bool>());

        // Only the upgrade-stop sent: the tiers stay. Nothing sent: both stay.
        var onlyStop = await app.Client.PutAsJsonAsync("/api/quality-model", new { upgradeStop = new { stopWhenCutoffMet = true, requireCustomFormatGainForSameQuality = true } });
        Assert.Equal(HttpStatusCode.OK, onlyStop.StatusCode);
        var afterStop = await ModelAsync(app);
        Assert.Equal(15.0, Tier(afterStop, "WEB 1080p")["movieMaxGb"]!.GetValue<double>());
        Assert.True(afterStop["upgradeStop"]!["stopWhenCutoffMet"]!.GetValue<bool>());

        var nothing = await app.Client.PutAsJsonAsync("/api/quality-model", new { });
        Assert.Equal(HttpStatusCode.OK, nothing.StatusCode);
        var afterNothing = await ModelAsync(app);
        Assert.Equal(15.0, Tier(afterNothing, "WEB 1080p")["movieMaxGb"]!.GetValue<double>());
        Assert.True(afterNothing["upgradeStop"]!["stopWhenCutoffMet"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_save_that_names_a_tier_twice_is_refused_in_plain_words_and_the_model_is_unchanged()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var model = await ModelAsync(app);
        var tiers = model["tiers"]!.AsArray();
        tiers.Add(Tier(model, "WEB 1080p").DeepClone());

        var saved = await app.Client.PutAsJsonAsync("/api/quality-model", new { tiers });

        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.Contains("Duplicate quality tier name", await saved.Content.ReadAsStringAsync());
        Assert.Equal(Names(model).Length - 1, Names(await ModelAsync(app)).Length);
    }

    [Fact]
    public async Task A_saved_ladder_lands_in_the_film_and_show_rank_tables_with_its_size_rules_in_bytes()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var model = await ModelAsync(app);
        var tier = Tier(model, "WEB 1080p");
        tier["rank"] = 71;
        tier["movieMinGb"] = 2.5;
        tier["moviePreferredGb"] = 6.0;
        tier["movieMaxGb"] = 15.0;
        tier["episodeMinMb"] = 300.0;
        tier["episodePreferredMb"] = 900.0;
        tier["episodeMaxMb"] = 2500.0;

        var saved = await app.Client.PutAsJsonAsync("/api/quality-model", new { tiers = model["tiers"] });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        Assert.Equal("71", await StoredAsync(app, DelunoDatabaseNames.Movies, "SELECT rank FROM quality_ranks WHERE name = 'WEB 1080p'"));
        Assert.Equal((long)(2.5 * Gigabyte), long.Parse((await StoredAsync(app, DelunoDatabaseNames.Movies, "SELECT floor_bytes FROM quality_ranks WHERE name = 'WEB 1080p'"))!));
        Assert.Equal(15 * Gigabyte, long.Parse((await StoredAsync(app, DelunoDatabaseNames.Movies, "SELECT ceiling_bytes FROM quality_ranks WHERE name = 'WEB 1080p'"))!));
        Assert.Equal("71", await StoredAsync(app, DelunoDatabaseNames.Series, "SELECT rank FROM quality_ranks WHERE name = 'WEB 1080p'"));
        Assert.Equal(300 * Megabyte, long.Parse((await StoredAsync(app, DelunoDatabaseNames.Series, "SELECT floor_bytes FROM quality_ranks WHERE name = 'WEB 1080p'"))!));
        Assert.Equal(2500 * Megabyte, long.Parse((await StoredAsync(app, DelunoDatabaseNames.Series, "SELECT ceiling_bytes FROM quality_ranks WHERE name = 'WEB 1080p'"))!));
        Assert.Equal(Names(model).Length.ToString(), await StoredAsync(app, DelunoDatabaseNames.Movies, "SELECT COUNT(*) FROM quality_ranks"));
        Assert.Equal(Names(model).Length.ToString(), await StoredAsync(app, DelunoDatabaseNames.Series, "SELECT COUNT(*) FROM quality_ranks"));
    }

    [Fact]
    public async Task A_reset_puts_every_tier_back_to_the_shipped_numbers_in_the_model_and_both_rank_tables_and_Activity_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var shipped = await ModelAsync(app);
        var model = await ModelAsync(app);
        foreach (var node in model["tiers"]!.AsArray())
        {
            node!["movieMaxGb"] = 123.0;
            node["moviePreferredGb"] = node["movieMinGb"]!.GetValue<double>() + 1;
            node["episodeMaxMb"] = 9999.0;
            node["episodePreferredMb"] = node["episodeMinMb"]!.GetValue<double>() + 1;
        }

        Assert.Equal(HttpStatusCode.OK, (await app.Client.PutAsJsonAsync("/api/quality-model", new { tiers = model["tiers"] })).StatusCode);
        Assert.Equal(123 * Gigabyte, long.Parse((await StoredAsync(app, DelunoDatabaseNames.Movies, "SELECT ceiling_bytes FROM quality_ranks WHERE name = 'WEB 1080p'"))!));

        var reset = await app.Client.PostAsync("/api/quality-model/reset", null);

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var read = await ModelAsync(app);
        foreach (var name in Names(shipped))
        {
            Assert.Equal(Tier(shipped, name)["movieMaxGb"]!.GetValue<double>(), Tier(read, name)["movieMaxGb"]!.GetValue<double>());
            Assert.Equal(Tier(shipped, name)["moviePreferredGb"]!.GetValue<double>(), Tier(read, name)["moviePreferredGb"]!.GetValue<double>());
            Assert.Equal(Tier(shipped, name)["episodeMaxMb"]!.GetValue<double>(), Tier(read, name)["episodeMaxMb"]!.GetValue<double>());
        }

        Assert.Equal(
            (long)Math.Round(Tier(shipped, "WEB 1080p")["movieMaxGb"]!.GetValue<double>() * Gigabyte),
            long.Parse((await StoredAsync(app, DelunoDatabaseNames.Movies, "SELECT ceiling_bytes FROM quality_ranks WHERE name = 'WEB 1080p'"))!));
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, null, null, CancellationToken.None);
        Assert.Contains(activity, item => item.Category == "quality.model.reset");
    }
}
