namespace Deluno.Contracts;

/// <summary>
/// What Deluno is doing about a title, in the four words that mean four things.
///
/// This replaces three private copies of the same switch — one each in the
/// movie, series and shared media repositories — and the word that meant
/// whatever the reader assumed. <c>waiting</c> was set by the workflow service
/// on a title that <em>has</em> a file and already meets its target, set by the
/// migration importer when the app being migrated from reported a file, and
/// described by the front end as "not searchable yet — it has not been
/// released", which is the opposite state. Meanwhile the episode paths wrote
/// <c>covered</c> for the same idea, in raw SQL, bypassing the normaliser
/// entirely. One state, two words, and a third meaning read off the screen.
///
/// See <c>DESIGN-001-title-marks.md</c> for the marks these drive.
/// </summary>
public static class WantedStatuses
{
    /// <summary>It is out, and Deluno does not have it. Deluno is looking.</summary>
    public const string Missing = "missing";

    /// <summary>Here and watchable, with room to get better. Deluno is still looking.</summary>
    public const string Upgrade = "upgrade";

    /// <summary>
    /// The quality the profile asked for. Deluno has stopped looking.
    ///
    /// This is what <c>waiting</c> meant everywhere the server set it, and
    /// V0015 renames the stored rows.
    /// </summary>
    public const string Covered = "covered";

    /// <summary>
    /// Not released, or the episode has not aired. There is nothing to find
    /// yet, so failing to find it is not a fact about the library.
    ///
    /// New. Before this, an unreleased movie was stored as <c>Missing</c> and
    /// counted against you from the day you added it.
    /// </summary>
    public const string Upcoming = "upcoming";

    public static readonly IReadOnlyList<string> All = [Missing, Upgrade, Covered, Upcoming];

    /// <summary>
    /// Whether Deluno should be looking for this. <c>Covered</c> has what it
    /// asked for and <c>Upcoming</c> does not exist yet; the other two are the
    /// work list.
    /// </summary>
    public static bool IsSearchable(string? status)
        => Normalize(status) is Missing or Upgrade;

    public static bool IsKnown(string? value)
        => value is not null && All.Contains(value.Trim().ToLowerInvariant());

    /// <summary>
    /// Reads a stored or supplied value, and refuses to guess.
    ///
    /// The old normalisers mapped anything unrecognised to <c>Missing</c>, which
    /// is the most dangerous direction to guess in: it means "go and download
    /// this", so a typo, or a value written by a newer version and read by an
    /// older one, silently became a download. Nothing reported it because every
    /// caller got a valid-looking word back.
    ///
    /// Null or blank still means <c>Missing</c> — a title with no state recorded
    /// genuinely has not been found yet. An unrecognised word is a defect, and
    /// says so.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Missing;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            Missing => Missing,
            Upgrade => Upgrade,
            Covered => Covered,
            Upcoming => Upcoming,
            // The one word this has to keep answering for. Databases written
            // before V0015 hold it, and so does anything mid-flight across the
            // upgrade; the migration renames the rows, this catches the rest.
            "waiting" => Covered,
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"'{value}' is not a wanted status. Expected one of: {string.Join(", ", All)}.")
        };
    }
}
