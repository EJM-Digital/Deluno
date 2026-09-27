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

            return BackfillPreferred(MergeMissingTiers(parsed)) with { UpdatedUtc = updatedUtc };
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
            await sink.SyncQualityRanksAsync(next.Tiers, cancellationToken);
        }

        return next;
    }

    /// <summary>
    /// Restores every tier's Min/Preferred/Max to Deluno's shipped defaults
    /// (#771). Goes through the same save path as an ordinary edit — same
    /// validation, same catalogue rank re-sync — so a reset behaves exactly
    /// like typing the shipped numbers back in by hand.
    /// </summary>
    public Task<QualityModelSnapshot> ResetToDefaultsAsync(CancellationToken cancellationToken)
        => SaveAsync(
            new UpdateQualityModelRequest(
                Tiers: DefaultTiers,
                UpgradeStop: BuildDefaultModel().UpgradeStop),
            cancellationToken);

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

    // One copy: QualityTypicalSizes is the same fact table release scoring
    // reads for a tier's expected size, so a shipped default here can never
    // quietly drift from the number Size Rules judges a release against.
    private static IReadOnlyList<QualityTierDefinition> DefaultTiers => QualityTypicalSizes.Tiers;

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

    /// <summary>
    /// A model saved before #771 has no Preferred field in its stored JSON, so
    /// deserializing it hands every tier a bare 0 rather than a real number. An
    /// existing install must not silently start scoring every release as
    /// "0 GB is the preferred size" the first time it loads after upgrading —
    /// it backfills the same 60%-of-band default a brand-new tier ships with,
    /// exactly the number "Reset to defaults" would put there, and leaves any
    /// tier that already has a real (non-zero) Preferred value untouched.
    /// </summary>
    private static QualityModelSnapshot BackfillPreferred(QualityModelSnapshot snapshot)
    {
        var backfilled = snapshot.Tiers.Select(tier =>
        {
            var moviePreferred = tier.MoviePreferredGb > 0
                ? tier.MoviePreferredGb
                : QualityTierDefinition.DefaultPreferred(tier.MovieMinGb, tier.MovieMaxGb);
            var episodePreferred = tier.EpisodePreferredMb > 0
                ? tier.EpisodePreferredMb
                : QualityTierDefinition.DefaultPreferred(tier.EpisodeMinMb, tier.EpisodeMaxMb);
            return moviePreferred == tier.MoviePreferredGb && episodePreferred == tier.EpisodePreferredMb
                ? tier
                : tier with { MoviePreferredGb = moviePreferred, EpisodePreferredMb = episodePreferred };
        }).ToList();

        return backfilled.SequenceEqual(snapshot.Tiers) ? snapshot : snapshot with { Tiers = backfilled };
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

            // #771: Preferred has to actually sit inside Min/Max, or "closest
            // to Preferred" would mean nothing to scoring.
            if (tier.MoviePreferredGb < tier.MovieMinGb
                || (tier.MovieMaxGb > 0 && tier.MoviePreferredGb > tier.MovieMaxGb))
            {
                throw new InvalidOperationException($"Tier '{tier.Name}' has a preferred film size outside its Min/Max band.");
            }

            if (tier.EpisodePreferredMb < tier.EpisodeMinMb
                || (tier.EpisodeMaxMb > 0 && tier.EpisodePreferredMb > tier.EpisodeMaxMb))
            {
                throw new InvalidOperationException($"Tier '{tier.Name}' has a preferred episode size outside its Min/Max band.");
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
