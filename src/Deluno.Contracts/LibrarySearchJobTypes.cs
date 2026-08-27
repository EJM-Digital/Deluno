namespace Deluno.Contracts;

/// <summary>
/// The job types a library search runs under, and the one place that decides
/// which of them a piece of work belongs to.
///
/// There used to be a single <c>library.search</c> for both catalogues, sharing
/// one worker lane. That made TV work queue behind movie work for no reason
/// anybody benefited from: the thing searches genuinely contend on is the
/// indexer, and that is already paced per host by
/// <c>FeedMediaSearchPlanner</c>'s outbound throttle, one layer below the lane.
///
/// Split so each catalogue gets its own lane and neither can starve the other —
/// including when movie searches are stuck against an unresponsive indexer,
/// which is the case a shared lane handles worst.
///
/// **One place decides.** Three call sites enqueue a library search, and a
/// fourth reads the type back to keep automation state. If each of them mapped
/// media type to job type itself, they would eventually disagree — which is the
/// defect behind almost everything found this week. Every one of them calls
/// <see cref="For"/> or <see cref="IsLibrarySearch"/>.
/// </summary>
public static class LibrarySearchJobTypes
{
    public const string Movies = "library.search.movies";
    public const string Tv = "library.search.tv";

    /// <summary>
    /// What every library search was called before the split.
    ///
    /// Kept as a constant because rows written by an older build are migrated by
    /// version 28, and because a name that no longer routes anywhere is exactly
    /// how [#303](https://github.com/jampat000/Deluno/issues/303) happened — a
    /// job with no lane never runs, and nothing looks wrong.
    /// </summary>
    public const string Legacy = "library.search";

    /// <summary>
    /// The job type for a media type, from anywhere that enqueues one.
    ///
    /// Unknown reads as movies rather than throwing: an unrecognised media type
    /// is a data problem, and refusing to search is a worse answer than
    /// searching the wrong catalogue, which finds nothing and says so.
    /// </summary>
    public static string For(string? mediaType)
        => IsTelevision(mediaType) ? Tv : Movies;

    /// <summary>
    /// Whether a job type is a library search of either kind.
    ///
    /// Used wherever <c>SqliteJobStore</c> keys automation state off the job
    /// type. Those checks read the type back rather than the payload, so every
    /// one of them has to accept all three names — the two current ones and any
    /// legacy row that outlived its migration.
    /// </summary>
    public static bool IsLibrarySearch(string? jobType)
        => string.Equals(jobType, Movies, StringComparison.OrdinalIgnoreCase)
            || string.Equals(jobType, Tv, StringComparison.OrdinalIgnoreCase)
            || string.Equals(jobType, Legacy, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The media-type vocabulary is not consistent across the codebase —
    /// libraries store <c>tv</c>, some payloads say <c>series</c>, and
    /// <see cref="Deluno.Contracts"/> callers pass either. Recognising all of
    /// them here is cheaper than making every caller normalise first, and
    /// stops a mismatch routing a show's search into the movies lane.
    /// </summary>
    private static bool IsTelevision(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() is "tv" or "series" or "show" or "shows" or "television";
}
