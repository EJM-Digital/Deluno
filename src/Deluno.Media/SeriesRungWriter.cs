using System.Data.Common;
using Deluno.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Media;

/// <summary>
/// Re-decides a show's stored rung from its episodes and writes it.
///
/// <para>This lived inside the series catalogue until a quality profile change
/// needed it from outside: raising a cutoff re-decides every episode, and the
/// rung the chips and the status filter read is a stored column that has to
/// follow in the same breath, or the show page and the shelf disagree.</para>
/// </summary>
public static class SeriesRungWriter
{
    /// <summary>
    /// "This episode has a file below what its profile asks for", as a SQL
    /// predicate over an episode row and its (left-joined) wanted-state row.
    ///
    /// <para>The wanted-state row is the one that is kept current: an import
    /// decides it, a profile change re-decides it. The flag on the episode row
    /// itself is only ever written as 0 when the episode is catalogued, so
    /// reading it made every show with a file look upgradable. It is the
    /// fallback only for an episode with no wanted-state row at all.</para>
    /// </summary>
    public static string EpisodeIsUpgradable(string episodeAlias, string wantedAlias)
        => $"(CASE WHEN {wantedAlias}.episode_id IS NULL THEN {episodeAlias}.quality_cutoff_met = 0 ELSE {wantedAlias}.wanted_status = '{WantedStatuses.Upgrade}' END)";

    public static async Task RefreshAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string seriesId,
        string libraryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var aired = 0;
        var airedWithFile = 0;
        var airedUpgradable = 0;
        var hasFutureAirDate = false;
        var hasUndated = false;
        var nothingWanted = false;

        using (var rollup = connection.CreateCommand())
        {
            rollup.Transaction = transaction;
            rollup.CommandText =
                $"""
                SELECT
                    COALESCE(SUM(CASE WHEN e.monitored = 1 AND e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN e.monitored = 1 AND e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now AND e.has_file = 1 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN e.monitored = 1 AND e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now AND e.has_file = 1 AND {EpisodeIsUpgradable("e", "ews")} THEN 1 ELSE 0 END), 0),
                    COALESCE(MAX(CASE WHEN e.monitored = 1 AND e.air_date_utc > @now THEN 1 ELSE 0 END), 0),
                    COALESCE(MAX(CASE WHEN e.monitored = 1 AND e.air_date_utc IS NULL THEN 1 ELSE 0 END), 0),
                    COUNT(*),
                    COALESCE(SUM(CASE WHEN e.monitored = 1 THEN 1 ELSE 0 END), 0)
                FROM episode_entries e
                LEFT JOIN episode_wanted_state ews ON ews.episode_id = e.id
                WHERE e.series_id = @seriesId;
                """;
            AddParameter(rollup, "@seriesId", seriesId);
            AddParameter(rollup, "@now", now.ToString("O"));

            using var reader = await rollup.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                aired = reader.GetInt32(0);
                airedWithFile = reader.GetInt32(1);
                airedUpgradable = reader.GetInt32(2);
                hasFutureAirDate = reader.GetInt32(3) == 1;
                hasUndated = reader.GetInt32(4) == 1;
                nothingWanted = reader.GetInt32(5) > 0 && reader.GetInt32(6) == 0;
            }
        }

        var status = SeriesRung.From(aired, airedWithFile, airedUpgradable, hasFutureAirDate, hasUndated, nothingWanted);

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE series_wanted_state
            SET wanted_status = @wantedStatus,
                wanted_reason = @wantedReason,
                quality_cutoff_met = @qualityCutoffMet,
                updated_utc = @updatedUtc
            WHERE series_id = @seriesId
              AND library_id = @libraryId;
            """;
        AddParameter(update, "@seriesId", seriesId);
        AddParameter(update, "@libraryId", libraryId);
        AddParameter(update, "@wantedStatus", status);
        AddParameter(update, "@wantedReason", Reason(status, aired, airedWithFile));
        AddParameter(update, "@qualityCutoffMet", status == WantedStatuses.Covered ? 1 : 0);
        AddParameter(update, "@updatedUtc", now.ToString("O"));
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Why a show is on the rung it is on, in the words Activity and the detail
    /// page print. Counts, because "missing" on a show is only useful with the
    /// number beside it.
    /// </summary>
    private static string Reason(string status, int aired, int airedWithFile) => status switch
    {
        WantedStatuses.Upcoming => "Nothing has aired yet.",
        WantedStatuses.Covered => $"All {aired} aired episode{(aired == 1 ? "" : "s")} are here at the quality you asked for.",
        WantedStatuses.Upgrade => $"All {aired} aired episode{(aired == 1 ? "" : "s")} are here, and some could be better.",
        _ => aired == 0
            ? "Deluno has not catalogued this show's episodes yet."
            : $"{airedWithFile} of {aired} aired episodes are here."
    };
}
