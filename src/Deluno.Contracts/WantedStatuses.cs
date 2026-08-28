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

    /// <summary>
    /// Every episode that has aired is here, and more are still to come.
    ///
    /// <para><b>The one state a film can never be in</b>, and the reason TV
    /// needed something Movies does not have. <c>Covered</c> on a show used to
    /// mean both "finished, and you hold all of it" and "up to date, with three
    /// more arriving next month", which are different enough that Sonarr spends
    /// two of its five colours telling them apart.</para>
    ///
    /// <para><b>Decided from whether an episode is actually scheduled</b>, not
    /// from the provider's status string. A show TMDb still calls
    /// <i>Returning Series</i> with nothing on the calendar is not airing, and a
    /// show it calls <i>Ended</i> that has a special dated next month is. The
    /// air date is the fact; the status is somebody's label for it — which is
    /// why status stays a filter and a sort and does not decide the mark.</para>
    ///
    /// <para>Searchable, unlike <c>Covered</c>: the episodes still to come will
    /// need finding when they air.</para>
    /// </summary>
    public const string Airing = "airing";

    /// <summary>
    /// Found, and handed to a download client. It is on its way.
    ///
    /// <para><b>The browser has drawn this since before anything could produce
    /// it.</b> <c>TITLE_MARK_PRESENTATION</c> has carried a <c>downloading</c>
    /// mark — blue dot, <i>"Coming down, processing, or importing"</i> — sitting
    /// in the ladder between Missing and Upgradable, and no server path ever set
    /// one. Declared, never populated, and invisible because a state that never
    /// happens looks exactly like a state that does not exist.</para>
    ///
    /// <para>Until now a title Deluno had already grabbed still read
    /// <c>Missing</c>, identical to one nothing had been found for — so the
    /// shelf told you to go and get something already on its way.</para>
    ///
    /// <para><b>Deliberately not searchable</b>, which is the whole point: it is
    /// what stops the cycle grabbing the same release twice. See
    /// <see cref="IsSearchable"/> for the safety net that stops that becoming a
    /// trap.</para>
    /// </summary>
    public const string Downloading = "downloading";

    public static readonly IReadOnlyList<string> All =
        [Missing, Upgrade, Covered, Upcoming, Airing, Downloading];

    /// <summary>
    /// The work list, as data, because SQL cannot ask C# what is searchable.
    ///
    /// <para>The eligibility query used to spell <c>IN ('missing', 'upgrade')</c>
    /// into its own SQL, which is the same rule as <see cref="IsSearchable"/>
    /// written a second time in a language that could not check itself against
    /// the first. Adding a status to one and not the other would have been
    /// silent — and the direction it fails in is the bad one: a title nobody
    /// ever searches for again.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> Searchable = [Missing, Upgrade, Airing];

    /// <summary>
    /// Whether Deluno should be looking for this. <c>Covered</c> has what it
    /// asked for and <c>Upcoming</c> does not exist yet; the other two are the
    /// work list.
    /// </summary>
    /// <summary>
    /// Whether Deluno should be looking for this.
    ///
    /// <para><c>Covered</c> has what it asked for, <c>Upcoming</c> does not
    /// exist yet, and <c>Downloading</c> is already on its way — looking for it
    /// again is how you grab the same release twice.</para>
    ///
    /// <para><b>And that is why a download cannot be trusted to end.</b> If a
    /// dispatch dies and nothing rewrites the status, this returns false for
    /// ever and the title is never searched again, in silence. The stored
    /// <c>downloading_since_utc</c> is what stops that: past
    /// <see cref="StuckDownloadAfter"/> the state is treated as expired and the
    /// title goes back on the list. The poll should have cleared it long before;
    /// this is what happens when the poll never comes.</para>
    /// </summary>
    public static bool IsSearchable(string? status)
        => Searchable.Contains(Normalize(status));

    /// <summary>
    /// How long a title may claim to be downloading before Deluno stops
    /// believing it.
    ///
    /// <para>Seven days, and it is a backstop rather than a policy: a large
    /// torrent on a slow line can genuinely take days, so this must never fire
    /// during normal operation. It exists for the case where nothing ever tells
    /// Deluno the download ended — the client was removed, the dispatch row was
    /// lost, the process died mid-flight — because the alternative is a title
    /// that silently drops out of the library's work list and stays out.</para>
    ///
    /// <para>The cost of it firing early is one duplicate search. The cost of
    /// not having it is a film nobody ever notices is missing.</para>
    /// </summary>
    public static readonly TimeSpan StuckDownloadAfter = TimeSpan.FromDays(7);

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

        // Read from All rather than a switch listing every status a second
        // time. That switch is how  came to throw here the moment it was
        // added: All knew about it, this did not, and the failure surfaced on
        // the search path rather than anywhere near the edit. One list now, and
        // adding a status cannot half-land.
        if (All.Contains(normalized))
        {
            return normalized;
        }

        return normalized switch
        {
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
