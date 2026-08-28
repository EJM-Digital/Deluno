namespace Deluno.Contracts;

/// <summary>
/// Which rung a <b>show</b> sits on, decided from its episodes.
///
/// <para><b>The rule this replaces was written twice and the two copies
/// disagreed.</b> The server stored a show's status on <c>series_wanted_state</c>
/// from the title-level row — <c>has_file</c> and <c>current_quality</c> of one
/// arbitrary file the import happened to find — while the browser recomputed it
/// from the episode counts. On the lab rig Severance, with three of twenty
/// episodes, was <i>Quality met</i> to the server and <i>Missing</i> on its own
/// poster. The chips counted one thing and the shelf showed another, and
/// clicking "Quality met" returned a title whose poster said "Missing".</para>
///
/// <para><b>A collection has no title-level file, so it can have no title-level
/// quality.</b> That is the whole error: the question "is this the quality you
/// asked for" is answerable about a movie and a category mistake about a show.
/// The answerable question is about its episodes, and this is the one place that
/// answers it.</para>
///
/// <para>The rungs are the same four every title uses — no new word for TV. What
/// carries "three of twenty" is the dot itself, drawn as a ring filled by what
/// the show holds, the way a half-drawn dot already means "not monitored".</para>
/// </summary>
public static class SeriesRung
{
    /// <summary>
    /// The rung, from the numbers the catalogue page already computes.
    /// </summary>
    /// <param name="aired">Episodes whose air date has passed.</param>
    /// <param name="airedWithFile">Of those, how many are on disk.</param>
    /// <param name="airedUpgradable">Of those on disk, how many are below the cutoff.</param>
    /// <param name="hasFutureAirDate">Whether any episode is still to come.</param>
    public static string From(int aired, int airedWithFile, int airedUpgradable, bool hasFutureAirDate)
    {
        if (aired <= 0)
        {
            // Nothing has aired. Not having it is not a fact about the library,
            // which is exactly what Upcoming exists to say — and a show with no
            // catalogued episodes at all is not upcoming, it is unknown, so it
            // reads Missing and gets searched for.
            return hasFutureAirDate ? WantedStatuses.Upcoming : WantedStatuses.Missing;
        }

        if (airedWithFile < aired)
        {
            return WantedStatuses.Missing;
        }

        // Every aired episode is here. Whether Deluno keeps looking depends on
        // the same thing it depends on for a film: is any of it below cutoff.
        if (airedUpgradable > 0)
        {
            return WantedStatuses.Upgrade;
        }

        // And then the split a film never needs. Holding everything that has
        // aired is not the same as being finished: one show is done for ever,
        // the other has three more episodes arriving next month. Sonarr spends
        // two of its five colours on exactly this distinction, and before now
        // Deluno could not draw it at all.
        //
        // Read from whether an episode is actually scheduled rather than from
        // the provider's status string, because the air date is the fact and
        // the status is somebody's label for it.
        return hasFutureAirDate ? WantedStatuses.Airing : WantedStatuses.Covered;
    }

    /// <summary>
    /// How much of the show is on disk, 0 to 1 — what the dot's ring is filled
    /// to.
    ///
    /// <para>This is the number that stops <i>three of twenty</i> looking
    /// identical to <i>none of eighty-seven</i>. Both are Missing and both are
    /// red; only one of them is nearly done, and before this nothing on the
    /// shelf said which.</para>
    ///
    /// <para>Nothing aired is a full ring rather than an empty one: an Upcoming
    /// show is not missing anything, and drawing it as empty would read as the
    /// worst possible state when it is simply early.</para>
    /// </summary>
    public static double Progress(int aired, int airedWithFile)
        => aired <= 0 ? 1 : Math.Clamp((double)airedWithFile / aired, 0, 1);
}
