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
/// <param name="Sort">One of <see cref="CatalogueSortFields"/>.</param>
/// <param name="Descending">Sort direction.</param>
/// <param name="PageSize">Rows per page, clamped by the repository.</param>
/// <param name="PageToken">
/// A continuation token from a previous page's <c>NextPageToken</c>. Keyset, not
/// offset: it carries the last row's sort key, so page 400 costs the same as
/// page 1 and rows inserted mid-scroll do not shift the window.
/// </param>
public sealed record CatalogueQuery(
    string? Search = null,
    string? Status = null,
    string? Sort = null,
    bool Descending = true,
    int PageSize = 50,
    string? PageToken = null);

public static class CatalogueStatusFilters
{
    public const string All = "all";
    public const string Monitored = "monitored";
    public const string Unmonitored = "unmonitored";
    public const string Downloaded = "downloaded";
    public const string Missing = "missing";

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Monitored => Monitored,
            Unmonitored => Unmonitored,
            Downloaded => Downloaded,
            Missing => Missing,
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

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Title => Title,
            Year => Year,
            Rating => Rating,
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
    int? TotalCount,
    CatalogueFacets? Facets);

public sealed record CatalogueFacets(
    int All,
    int Monitored,
    int Unmonitored,
    int Downloaded,
    int Missing);

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
