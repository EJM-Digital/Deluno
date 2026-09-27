using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #771: the Quality Definitions table gains a Preferred size per tier and a
/// Reset to defaults action. These tests cover the service layer only — the
/// reset endpoint's Activity write lives in Deluno.Platform (it needs
/// IActivityFeedRepository, which Deluno.Quality must not reference), and is
/// exercised by PlatformEndpointRouteBuilderExtensions instead.
/// </summary>
public sealed class QualityModelServiceTests
{
    private static async Task<QualityModelService> CreateServiceAsync(TestStorage storage, FakeTimeProvider time)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new QualityModelService(storage.Factory, time);
    }

    [Fact]
    public async Task Saving_a_tier_preserves_its_preferred_value_on_the_next_read()
    {
        using var storage = TestStorage.Create();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-27T00:00:00Z"));
        var service = await CreateServiceAsync(storage, time);

        var initial = await service.GetAsync(CancellationToken.None);
        var edited = initial.Tiers.Select(tier =>
            string.Equals(tier.Name, "WEB 1080p", StringComparison.OrdinalIgnoreCase)
                ? tier with { MoviePreferredGb = 9.5 }
                : tier).ToList();

        await service.SaveAsync(new UpdateQualityModelRequest(edited, null), CancellationToken.None);
        var reloaded = await service.GetAsync(CancellationToken.None);

        Assert.Equal(9.5, reloaded.Tiers.Single(t => t.Name == "WEB 1080p").MoviePreferredGb);
    }

    [Fact]
    public async Task Reset_to_defaults_restores_a_hand_edited_preferred_value()
    {
        using var storage = TestStorage.Create();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-27T00:00:00Z"));
        var service = await CreateServiceAsync(storage, time);

        var initial = await service.GetAsync(CancellationToken.None);
        var shippedPreferred = initial.Tiers.Single(t => t.Name == "WEB 1080p").MoviePreferredGb;
        var edited = initial.Tiers.Select(tier =>
            string.Equals(tier.Name, "WEB 1080p", StringComparison.OrdinalIgnoreCase)
                ? tier with { MoviePreferredGb = 0.5, MovieMinGb = 0.1, MovieMaxGb = 1.0 }
                : tier).ToList();
        await service.SaveAsync(new UpdateQualityModelRequest(edited, null), CancellationToken.None);

        var reset = await service.ResetToDefaultsAsync(CancellationToken.None);

        Assert.Equal(shippedPreferred, reset.Tiers.Single(t => t.Name == "WEB 1080p").MoviePreferredGb);
    }

    /// <summary>
    /// A model saved before #771 has no "moviePreferredGb"/"episodePreferredMb"
    /// keys in its stored JSON, so a bare deserialize hands every tier a 0. An
    /// existing install must see the same 60%-of-band default a fresh install
    /// or a reset would give it, not "0 GB is preferred" for every tier.
    /// </summary>
    [Fact]
    public async Task A_model_saved_before_preferred_existed_backfills_it_on_read()
    {
        using var storage = TestStorage.Create();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-27T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        const string preferredNaiveJson =
            """
            {"version":"quality-model/v2","tiers":[{"name":"WEB 1080p","rank":70,"movieMinGb":1.5,"movieMaxGb":25.0,"episodeMinMb":420,"episodeMaxMb":3800,"scoreCeiling":50}],"upgradeStop":{"stopWhenCutoffMet":true,"requireCustomFormatGainForSameQuality":true},"updatedUtc":"2026-01-01T00:00:00Z"}
            """;
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO system_settings (setting_key, setting_value, updated_utc) VALUES ('quality.model.v2', @value, @updated);";
            var valueParam = command.CreateParameter();
            valueParam.ParameterName = "@value";
            valueParam.Value = preferredNaiveJson;
            command.Parameters.Add(valueParam);
            var updatedParam = command.CreateParameter();
            updatedParam.ParameterName = "@updated";
            updatedParam.Value = "2026-01-01T00:00:00Z";
            command.Parameters.Add(updatedParam);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var service = new QualityModelService(storage.Factory, time);
        var loaded = await service.GetAsync(CancellationToken.None);
        var tier = loaded.Tiers.Single(t => t.Name == "WEB 1080p");

        Assert.Equal(QualityTierDefinition.DefaultPreferred(1.5, 25.0), tier.MoviePreferredGb);
        Assert.Equal(QualityTierDefinition.DefaultPreferred(420, 3800), tier.EpisodePreferredMb);
    }
}
