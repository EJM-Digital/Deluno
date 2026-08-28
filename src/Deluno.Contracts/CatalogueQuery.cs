using System.Text;
using System.Text.Json;

namespace Deluno.Contracts;

/// <summary>
/// A page request for a library-sized list.
///
/// Everything here is applied in SQL. That is the whole point: the library view
/// used to fetch the entire catalogue and then search, filter, sort and count it
/// in the browser, which is fine at 200 titles and impossible at 20,000. Asking
/// the database means the work is bounded by the page, not by the library.
/// </summary>
/// <param name="Search">
/// Free text matched against title and genres. Null or blank matches everything.
/// </param>
/// <param name="Status">
/// One of <see cref="CatalogueStatusFilters"/>. Anything else is read as "all"
/// rather than silently returning nothing.
/// </param>
/// <param name="Monitored">
/// A **separate axis** from <paramref name="Status"/>, and deliberately so.
///
/// Status says what is true of a title — Missing, Upgradable, Quality met,
/// Upcoming. Monitoring says whether Deluno will act on it. They multiply: a
/// missing title Deluno is hunting for and a missing title you have told it to
/// leave alone are the same state and opposite intentions, so no single value
/// can carry both. Monitored and Unmonitored used to be two more
/// <see cref="CatalogueStatusFilters"/> values, which made them mutually
/// exclusive with every real state and meant "missing and unmonitored" could
/// not be asked for at all.
///
/// <c>null</c> is "either".
/// </param>
/// <param name="LibraryId">
/// Optional library identity. When supplied, the query and its facets are
/// limited to media assigned to that library in wanted state.
/// </param>
/// <param name="Sort">One of <see cref="CatalogueSortFields"/>.</param>
/// <param name="Descending">Sort direction.</param>
/// <param name="PageSize">Rows per page, clamped by the repository.</param>
/// <param name="PageToken">
/// A continuation token from a previous page's <c>NextPageToken</c>. Keyset, not
/// offset: it carries the last row's sort key, so page 400 costs the same as
/// page 1 and rows inserted mid-scroll do not shift the window.
/// </param>
/// <param name="Filters">
/// The narrowing beyond status and library — quality, size, genre, year,
/// runtime, rating. Applied in SQL like everything else here, and
/// <see cref="CatalogueFilters.None"/> costs exactly nothing.
/// </param>
public sealed record CatalogueQuery(
    string? Search = null,
    string? Status = null,
    bool? Monitored = null,
    string? LibraryId = null,
    string? Sort = null,
    bool Descending = true,
    int PageSize = 50,
    string? PageToken = null,
    CatalogueFilters? Filters = null);

/// <summary>
/// What a title *is*. Monitoring is not in here — it is whether Deluno will act
/// on the title, which is a different question and travels as
/// <see cref="CatalogueQuery.Monitored"/>. It used to be two values in this
/// list, which made "monitored" mutually exclusive with "missing".
/// </summary>
public static class CatalogueStatusFilters
{
    public const string All = "all";
    public const string Downloaded = "downloaded";
    public const string Missing = "missing";
    public const string Upgrades = "upgrades";

    /// <summary>
    /// Has what the profile asked for. The counterpart to <c>Upgrades</c>: those
    /// two together are what <c>Downloaded</c> could never separate, since a movie
    /// below its target is downloaded too.
    /// </summary>
    public const string Covered = "covered";

    /// <summary>Not out yet, so its absence is not a shortfall.</summary>
    public const string Upcoming = "upcoming";

    /// <summary>
    /// Found and handed to a download client. On its way.
    ///
    /// <para>Worth a chip of its own for the reason the state exists: without
    /// it, the only way to see what Deluno is currently fetching is to open the
    /// queue, which is a different screen answering a different question. This
    /// is the library's own answer to "what is in hand".</para>
    /// </summary>
    public const string Downloading = "downloading";

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Downloaded => Downloaded,
            Missing => Missing,
            Upgrades => Upgrades,
            Covered => Covered,
            Upcoming => Upcoming,
            Downloading => Downloading,
            _ => All
        };
}

/// <summary>
/// The sorts a catalogue page can be ordered by.
///
/// Deliberately short. Each is a real stored column, so ordering a 20,000-item
/// library is a question the database can answer rather than something the
/// browser does to an array it had to download first.
/// </summary>
public static class CatalogueSortFields
{
    /// <summary>Newest first by default — what a user wants after an import.</summary>
    public const string Added = "added";

    public const string Title = "title";
    public const string Year = "year";
    public const string Rating = "rating";

    /// <summary>How long it runs. A real column on both catalogues, and indexed.</summary>
    public const string Runtime = "runtime";

    /// <summary>
    /// How much the wider world is watching it, from the metadata provider.
    /// Indexed alongside runtime since V0011/V0012 and never offered until now.
    /// </summary>
    public const string Popularity = "popularity";

    /// <summary>How big the file is. Media is made of files, so a shelf sorts by them.</summary>
    public const string Size = "size";

    /// <summary>
    /// How good the file is, by the quality ladder's own ranking — so
    /// <c>Remux 2160p</c> outranks <c>WEB 2160p</c> rather than the two sorting
    /// alphabetically, which would be meaningless.
    /// </summary>
    public const string Quality = "quality";

    /// <summary>
    /// Size over runtime — how much file there is per minute.
    ///
    /// Neither Radarr nor Sonarr offers this, and it is the question behind
    /// every "why is this 2160p file only four gigabytes". Size alone says a
    /// file is big; this says whether it is big for what it is.
    /// </summary>
    public const string Bitrate = "bitrate";

    /// <summary>
    /// Every sort here is a stored column on the *entries* table with an index
    /// behind it, which is what keeps page four hundred costing what page one
    /// costs.
    ///
    /// Size and quality describe the file rather than the title, and the file
    /// lives on the wanted state — which the page reaches through a correlated
    /// pick that SQLite cannot index. They are sortable anyway because V0016 and
    /// V0017 keep the picked file's size and quality rank on the entry, updated
    /// by a trigger so no write path can forget them. See those migrations for
    /// the one rule that now exists in two languages, and the test that holds
    /// the two copies together.
    /// </summary>
    /// <summary>
    /// When the next episode airs. A show is the only thing that has a next
    /// anything, so this is the first sort that means nothing on the other
    /// shelf — see <see cref="ForKind"/>.
    /// </summary>
    public const string NextAiring = "nextairing";

    /// <summary>
    /// How far through its aired episodes a show is, as the count it holds.
    ///
    /// <para>Sonarr's most useful sort, and the reason it is a stored column
    /// rather than arithmetic: it changes because time passed, so V0020 stores
    /// the answer beside the moment it stops being true.</para>
    /// </summary>
    public const string EpisodeProgress = "episodeprogress";

    /// <summary>Who broadcasts it. A film has a studio, which arrives with #306.</summary>
    public const string Network = "network";

    public static readonly IReadOnlyList<string> All =
        [Added, Title, Year, Rating, Runtime, Popularity, Size, Quality, Bitrate,
         NextAiring, EpisodeProgress, Network];

    /// <summary>
    /// The sorts one kind of shelf can actually perform.
    ///
    /// <para>#324 split the <i>filter</i> vocabulary per media kind and stopped
    /// there, so both shelves went on being offered one list of nine sorts.
    /// Sonarr is not Radarr with seasons: ordering a film by when its next
    /// episode airs is not a sort with no rows, it is a sort with no
    /// meaning.</para>
    /// </summary>
    public static IReadOnlyList<string> ForKind(MediaKind kind)
        => kind == MediaKind.Series
            ? All
            : [.. All.Where(field => field is not (NextAiring or EpisodeProgress or Network))];

    /// <summary>
    /// The same, but refusing a sort the shelf cannot perform.
    ///
    /// <para>A Movies page asking to be ordered by next airing is not a page
    /// with no rows — it is SQL naming a column that does not exist on
    /// <c>movie_entries</c>, which is an exception at execute time. Falling back
    /// to the default is what every other unrecognised sort already does.</para>
    /// </summary>
    public static string Normalize(string? value, MediaKind kind)
    {
        var normalized = Normalize(value);
        return ForKind(kind).Contains(normalized) ? normalized : Added;
    }

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Title => Title,
            Year => Year,
            Rating => Rating,
            Runtime => Runtime,
            Popularity => Popularity,
            Size => Size,
            Quality => Quality,
            Bitrate => Bitrate,
            NextAiring => NextAiring,
            EpisodeProgress => EpisodeProgress,
            Network => Network,
            _ => Added
        };
}

/// <summary>
/// One page of a catalogue, plus the numbers the surrounding UI needs so it does
/// not have to count anything itself.
/// </summary>
/// <param name="TotalCount">
/// How many rows match the search and status filter, or <c>null</c> on a
/// continuation page. This is what lets a caller tell a complete answer from a
/// truncated one; it is computed once for a given filter rather than on every
/// page, because it is the one part of the request that scans.
/// </param>
/// <param name="Facets">
/// Counts per quick filter, over the current search. <c>null</c> on a
/// continuation page, for the same reason. These used to be computed in the
/// browser from the whole catalogue.
/// </param>
public sealed record CataloguePage<T>(
    IReadOnlyList<T> Items,
    string? NextPageToken,
    bool HasMore,
    int? TotalCount,
    CatalogueFacets? Facets);

public sealed record CatalogueFacets(
    int All,
    int Monitored,
    int Unmonitored,
    /// <summary>
    /// Has a file, whatever its quality. Still a useful filter, but no longer a
    /// number worth printing: a movie below its target is downloaded too, so the
    /// word could never tell you which titles still had work outstanding. The
    /// four below can.
    /// </summary>
    int Downloaded,
    int Missing,
    int Upgrades,
    /// <summary>Has what the profile asked for. Deluno has stopped looking.</summary>
    int Covered = 0,
    /// <summary>Not out yet, so its absence is not a shortfall.</summary>
    int Upcoming = 0,
    /// <summary>Found and handed to a download client. On its way.</summary>
    int Downloading = 0)
{
    /// <summary>
    /// The number to print above a shelf showing this status — which is simply
    /// this status's own facet.
    ///
    /// <para><b>Written here because it was written twice, and wrong in both
    /// copies.</b> Each catalogue had a private <c>SelectFacetTotal</c> that
    /// named three statuses and sent everything else to <c>All</c>. So a shelf
    /// filtered to <i>Quality met</i> printed the size of the whole library
    /// above one row, and <i>Upcoming</i> printed it above none — the exact
    /// thing #322 rule 2 exists to forbid: <i>the counts above the shelf count
    /// the rows on it</i>.</para>
    ///
    /// <para>It went unnoticed because the shape hides it. A catch-all arm
    /// returning a plausible number is invisible until somebody reads the two
    /// numbers side by side, and it silently absorbs every status added
    /// afterwards — <c>Covered</c> and <c>Upcoming</c> had already fallen in
    /// before <c>Downloading</c> did.</para>
    /// </summary>
    public int TotalFor(string status)
        => CatalogueStatusFilters.Normalize(status) switch
        {
            CatalogueStatusFilters.Downloaded => Downloaded,
            CatalogueStatusFilters.Missing => Missing,
            CatalogueStatusFilters.Upgrades => Upgrades,
            CatalogueStatusFilters.Covered => Covered,
            CatalogueStatusFilters.Upcoming => Upcoming,
            CatalogueStatusFilters.Downloading => Downloading,
            // `all` is the only status left, and it genuinely is the whole
            // shelf. Spelled out rather than left to a catch-all so the next
            // status added is a compile-time hole here rather than a wrong
            // number on screen.
            _ => All
        };
}

/// <summary>
/// The continuation token: the sort value of the last row on the page, and its
/// id as a tiebreaker.
///
/// Opaque to callers on purpose. It is not an offset, so it cannot drift when
/// rows are inserted while somebody is scrolling, and resuming from it is an
/// index seek rather than a walk over everything skipped.
/// </summary>
public sealed record CataloguePageToken(string? SortValue, string Id)
{
    public string Encode()
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { SortValue, Id })));

    /// <summary>
    /// Decodes a token, or returns <c>null</c> for anything unreadable. A
    /// malformed token means "start from the beginning", never an error: tokens
    /// travel in URLs and outlive deploys.
    /// </summary>
    public static CataloguePageToken? Decode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var parts = JsonSerializer.Deserialize<string?[]>(
                Encoding.UTF8.GetString(Convert.FromBase64String(token)));

            return parts is { Length: 2 } && !string.IsNullOrEmpty(parts[1])
                ? new CataloguePageToken(parts[0], parts[1]!)
                : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }
}
