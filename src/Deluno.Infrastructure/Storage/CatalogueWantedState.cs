using System.Data.Common;
using System.Globalization;

namespace Deluno.Infrastructure.Storage;

/// <summary>
/// The one wanted-state row a catalogue page speaks for, and the columns it
/// contributes to that page.
///
/// Both catalogues had written the same correlated subquery eight times over —
/// once per field — differing only in table and key name. That cost eight index
/// seeks a row, and it could not keep its own answers together: each subquery
/// took the first row with a non-null value for *its* column, so a title in two
/// libraries could report one library's quality beside another's file path.
///
/// One join, one row, one library. The pick prefers a row that has a file, and
/// among those the one still short of its cutoff, so what the page displays
/// agrees with what the Downloaded and Upgrades filters select — which is the
/// part that would otherwise drift.
/// </summary>
public static class CatalogueWantedState
{
    /// <summary>
    /// The LEFT JOIN that binds one wanted-state row to each catalogue entry.
    /// <paramref name="scopedToLibrary"/> narrows the pick to <c>@libraryId</c>,
    /// which the caller binds whether or not it is used.
    /// </summary>
    public static string Join(string alias, string wantedTable, string foreignKey, bool scopedToLibrary)
    {
        var scope = scopedToLibrary ? $"{Environment.NewLine}                      AND pick.library_id = @libraryId" : string.Empty;
        return $"""
                LEFT JOIN {wantedTable} ws ON ws.rowid = (
                    SELECT pick.rowid
                    FROM {wantedTable} pick
                    WHERE pick.{foreignKey} = {alias}.id{scope}
                    ORDER BY pick.has_file DESC, pick.quality_cutoff_met ASC, pick.library_id ASC
                    LIMIT 1
                )
                """;
    }

    /// <summary>
    /// "This entry has a file." The joined row already carries the answer, and
    /// carries the file's own facts with it — but a title with no wanted state
    /// at all joins to nothing, so the null has to mean <c>false</c> rather than
    /// unknown.
    /// </summary>
    public const string HasFileColumn = "COALESCE(ws.has_file, 0) AS has_file";

    /// <summary>
    /// The search state the grid needs on every card and used to fetch from
    /// <c>/wanted</c>, whose <c>recentItems</c> is capped at 25 — so beyond the
    /// first 25 titles in a library every card silently lost its status, its
    /// reason and its target quality and fell back to "is there a file". These
    /// come with the page, so the 20,000th title says as much as the first.
    ///
    /// Appended after the entry's own columns, so existing ordinals do not move.
    /// Read back with <see cref="Read"/>, which must stay in step with it.
    /// </summary>
    public const string PageColumns =
        """
            ws.library_id,
            ws.wanted_status,
            ws.wanted_reason,
            ws.target_quality,
            ws.quality_cutoff_met,
            ws.last_search_utc,
            ws.next_eligible_search_utc
        """;

    /// <summary>How many ordinals <see cref="PageColumns"/> occupies.</summary>
    public const int PageColumnCount = 7;

    /// <summary>
    /// Reads <see cref="PageColumns"/> back, starting at
    /// <paramref name="firstOrdinal"/>. Every field is nullable because a title
    /// Deluno is not tracking in any library has no wanted state to read, and
    /// saying "unknown" is honest where saying "false" would not be.
    /// </summary>
    public static CatalogueWantedFields Read(DbDataReader reader, int firstOrdinal)
    {
        return new CatalogueWantedFields(
            LibraryId: ReadText(reader, firstOrdinal),
            WantedStatus: ReadText(reader, firstOrdinal + 1),
            WantedReason: ReadText(reader, firstOrdinal + 2),
            TargetQuality: ReadText(reader, firstOrdinal + 3),
            QualityCutoffMet: reader.IsDBNull(firstOrdinal + 4) ? null : reader.GetInt64(firstOrdinal + 4) == 1,
            LastSearchUtc: ReadTimestamp(reader, firstOrdinal + 5),
            NextEligibleSearchUtc: ReadTimestamp(reader, firstOrdinal + 6));
    }

    private static string? ReadText(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? ReadTimestamp(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>
/// What the joined wanted-state row says about a catalogue entry. All-null
/// means Deluno holds no search state for it in any library.
/// </summary>
public readonly record struct CatalogueWantedFields(
    string? LibraryId,
    string? WantedStatus,
    string? WantedReason,
    string? TargetQuality,
    bool? QualityCutoffMet,
    DateTimeOffset? LastSearchUtc,
    DateTimeOffset? NextEligibleSearchUtc);
