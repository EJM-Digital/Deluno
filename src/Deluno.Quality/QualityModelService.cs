using System.Globalization;
using System.Text.Json;
using Deluno.Infrastructure.Storage;

namespace Deluno.Quality;

public sealed class QualityModelService(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    IEnumerable<IQualityRankSink>? qualityRankSinks = null)
    : IQualityModelService
{
    private const string ModelVersion = "quality-model/v2";
    private const string SettingKey = "quality.model.v2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async Task<QualityModelSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value, updated_utc FROM system_settings WHERE setting_key = @key;";
        AddParameter(command, "@key", SettingKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return BuildDefaultModel();
        }

        var raw = reader.IsDBNull(0) ? null : reader.GetString(0);
        var updatedUtc = reader.IsDBNull(1)
            ? timeProvider.GetUtcNow()
            : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return BuildDefaultModel() with { UpdatedUtc = updatedUtc };
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<QualityModelSnapshot>(raw, JsonOptions);
            if (parsed is null || parsed.Tiers.Count == 0)
            {
                return BuildDefaultModel() with { UpdatedUtc = updatedUtc };
            }

            return MergeMissingTiers(parsed) with { UpdatedUtc = updatedUtc };
        }
        catch
        {
            return BuildDefaultModel() with { UpdatedUtc = updatedUtc };
        }
    }

    public async Task<QualityModelSnapshot> SaveAsync(UpdateQualityModelRequest request, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(cancellationToken);
        var next = new QualityModelSnapshot(
            Version: ModelVersion,
            Tiers: request.Tiers is { Count: > 0 } ? request.Tiers : existing.Tiers,
            UpgradeStop: request.UpgradeStop ?? existing.UpgradeStop,
            UpdatedUtc: timeProvider.GetUtcNow());

        Validate(next);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO system_settings (setting_key, setting_value, updated_utc)
            VALUES (@key, @value, @updatedUtc)
            ON CONFLICT(setting_key) DO UPDATE SET
                setting_value = excluded.setting_value,
                updated_utc = excluded.updated_utc;
            """;
        AddParameter(command, "@key", SettingKey);
        AddParameter(command, "@value", JsonSerializer.Serialize(next with { UpdatedUtc = DateTimeOffset.UnixEpoch }, JsonOptions));
        AddParameter(command, "@updatedUtc", next.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // The catalogues sort a shelf by quality, and an ORDER BY needs a number
        // in its own database. Pushing the ladder to them here is what stops a
        // renamed or re-ranked tier leaving every title sorted by a number this
        // model no longer agrees with.
        //
        // After the commit, deliberately: the model is saved either way, and a
        // catalogue that cannot be reached is a stale sort order, not a lost
        // setting.
        foreach (var sink in qualityRankSinks ?? [])
        {
            await sink.SyncQualityRanksAsync(
                next.Tiers.ToDictionary(tier => tier.Name, tier => tier.Rank, StringComparer.OrdinalIgnoreCase),
                cancellationToken);
        }

        return next;
    }

    /*
      The full release vocabulary users expect to tune, matching the Radarr and
      Sonarr quality set. Ranks of the original twelve tiers are unchanged so
      saved quality profiles keep resolving; additions slot into the gaps.
      Low-grade sources (cams, screeners) are listed so a profile can reject
      them explicitly instead of leaving them unclassified.
    */
    private static QualityModelSnapshot BuildDefaultModel()
    {
        return new QualityModelSnapshot(
            Version: ModelVersion,
            Tiers: DefaultTiers,
            UpgradeStop: new QualityUpgradeStopPolicy(
                StopWhenCutoffMet: true,
                RequireCustomFormatGainForSameQuality: true),
            UpdatedUtc: DateTimeOffset.UtcNow);
    }

    private static readonly IReadOnlyList<QualityTierDefinition> DefaultTiers =
    [
        new("Unknown", 1, 0.1, 2.0, 50, 800, 0),
        new("WORKPRINT", 2, 0.1, 2.0, 50, 800, 0),
        new("CAM", 3, 0.1, 2.0, 50, 800, 0),
        new("TELESYNC", 4, 0.1, 2.5, 50, 900, 0),
        new("TELECINE", 5, 0.2, 3.0, 60, 1000, 0),
        new("REGIONAL", 6, 0.2, 3.0, 60, 1000, 0),
        new("DVDSCR", 7, 0.3, 3.5, 80, 1100, 0),
        new("SDTV", 10, 0.3, 1.8, 120, 900, 0),
        new("DVD", 20, 0.7, 3.5, 180, 1200, 0),
        new("DVD-R", 21, 1.0, 8.5, 220, 2000, 0),
        new("WEB 480p", 22, 0.4, 3.0, 150, 1100, 5),
        new("Bluray 480p", 24, 0.5, 4.0, 170, 1300, 5),
        new("Bluray 576p", 25, 0.6, 5.0, 190, 1500, 5),
        new("HDTV 720p", 30, 0.8, 6.5, 220, 1800, 10),
        new("WEB 720p", 40, 0.8, 8.0, 240, 2200, 20),
        new("Bluray 720p", 50, 1.2, 10.0, 280, 2500, 30),
        new("HDTV 1080p", 60, 1.3, 14.0, 350, 3200, 40),
        new("WEB 1080p", 70, 1.5, 25.0, 420, 3800, 50),
        new("Bluray 1080p", 80, 2.2, 35.0, 480, 4400, 60),
        new("Remux 1080p", 90, 12.0, 60.0, 1500, 8000, 70),
        new("HDTV 2160p", 95, 4.0, 40.0, 900, 9000, 75),
        new("WEB 2160p", 100, 7.0, 60.0, 1600, 12000, 80),
        new("Bluray 2160p", 110, 12.0, 90.0, 2200, 18000, 90),
        new("Remux 2160p", 120, 35.0, 130.0, 6000, 36000, 100),
        new("BR-DISK", 125, 20.0, 130.0, 4000, 36000, 0),
        new("Raw-HD", 126, 4.0, 60.0, 1200, 12000, 0)
    ];

    /*
      An install that already saved a model keeps every value it tuned; tiers
      added to the catalogue since then are merged in at their defaults so the
      list never silently lags behind the release vocabulary.
    */
    private static QualityModelSnapshot MergeMissingTiers(QualityModelSnapshot snapshot)
    {
        var known = snapshot.Tiers
            .Select(tier => tier.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = DefaultTiers.Where(tier => !known.Contains(tier.Name)).ToList();
        if (missing.Count == 0)
        {
            return snapshot;
        }

        return snapshot with { Tiers = snapshot.Tiers.Concat(missing).OrderBy(tier => tier.Rank).ToList() };
    }

    private static void Validate(QualityModelSnapshot model)
    {
        if (model.Tiers.Count == 0)
        {
            throw new InvalidOperationException("At least one quality tier is required.");
        }

        var duplicateNames = model.Tiers
            .GroupBy(t => t.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateNames is not null)
        {
            throw new InvalidOperationException($"Duplicate quality tier name '{duplicateNames.Key}'.");
        }

        foreach (var tier in model.Tiers)
        {
            if (string.IsNullOrWhiteSpace(tier.Name))
            {
                throw new InvalidOperationException("Quality tier names cannot be empty.");
            }

            if (tier.Rank <= 0)
            {
                throw new InvalidOperationException($"Tier '{tier.Name}' has invalid rank.");
            }

            // A maximum of 0 means "no upper limit", the same convention Radarr and
            // Sonarr use for Unlimited. A minimum of 0 means "no lower limit", so
            // 0/0 is the valid "accept any size" band.
            if (tier.MovieMinGb < 0 || tier.MovieMaxGb < 0 || (tier.MovieMaxGb > 0 && tier.MovieMaxGb <= tier.MovieMinGb))
            {
                throw new InvalidOperationException($"Tier '{tier.Name}' has invalid movie size bounds.");
            }

            if (tier.EpisodeMinMb < 0 || tier.EpisodeMaxMb < 0 || (tier.EpisodeMaxMb > 0 && tier.EpisodeMaxMb <= tier.EpisodeMinMb))
            {
                throw new InvalidOperationException($"Tier '{tier.Name}' has invalid episode size bounds.");
            }
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
