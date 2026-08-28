namespace Deluno.Contracts;

/// <summary>
/// The rating sources Deluno keeps as columns, so a shelf can be ordered and
/// filtered by each of them separately.
///
/// <para><b>Why not just the blended score.</b> The four disagree, and the
/// disagreement is the information (#319). A film at IMDb 8.1 and Rotten
/// Tomatoes 41% is a different proposition from one at 8.1 and 94%; averaging
/// them destroys exactly the signal somebody was looking for.</para>
///
/// <para><b>Why columns and not the blob.</b> Every score is already stored
/// inside <c>metadata_json</c> and read back by <c>BuildRatings</c>. That is
/// fine for drawing one title and useless for ordering five thousand: a filter
/// or a sort has to be a WHERE or an ORDER BY on an indexed column, or the page
/// stops being a seek. Same reasoning as V0016/V0017.</para>
///
/// <para><b>Two of the four carry a vote count and two do not.</b> TMDb and
/// IMDb report how many people voted; OMDb's Rotten Tomatoes and Metacritic
/// figures arrive as a bare percentage. Storing a votes column for those two
/// would be a filter that can only ever match nothing — the failure #324 was
/// opened about. So the count exists where the number exists, and the interface
/// only offers it there.</para>
///
/// <para><b>Trakt is not here, and that is not an oversight.</b> #319 lists it
/// because Radarr shows it; Deluno has no Trakt integration and no key for one,
/// so there is nothing to put in the column. Adding it would be a fifth control
/// that always reads empty.</para>
/// </summary>
public static class RatingSources
{
    public const string Tmdb = "tmdb";
    public const string Imdb = "imdb";
    public const string RottenTomatoes = "rotten_tomatoes";
    public const string Metacritic = "metacritic";

    /// <summary>
    /// One source as the catalogue stores it.
    /// </summary>
    /// <param name="Source">The key <c>MetadataRatingItem.Source</c> uses.</param>
    /// <param name="Label">What a person calls it.</param>
    /// <param name="ScoreColumn">The column the score lands in, on both catalogues.</param>
    /// <param name="VotesColumn">
    /// The column the vote count lands in, or <c>null</c> where the provider
    /// does not report one.
    /// </param>
    /// <param name="MaxScore">
    /// Ten for the community scores, a hundred for the critic percentages. The
    /// stored value is the provider's own number, not a normalised one — an
    /// IMDb 8.1 is written as 8.1, because a person filtering on IMDb types the
    /// number they have seen on IMDb.
    /// </param>
    public sealed record RatingSource(
        string Source,
        string Label,
        string ScoreColumn,
        string? VotesColumn,
        double MaxScore);

    /// <summary>
    /// The list everything else is generated from: the columns in the
    /// migrations, the filter fields, the sorts, the poster options and the
    /// write path. One list, so a source cannot be added in one place and
    /// missed in another.
    /// </summary>
    public static readonly IReadOnlyList<RatingSource> All =
    [
        new(Tmdb, "TMDb", "rating_tmdb", "votes_tmdb", 10),
        new(Imdb, "IMDb", "rating_imdb", "votes_imdb", 10),
        new(RottenTomatoes, "Rotten Tomatoes", "rating_rotten_tomatoes", null, 100),
        new(Metacritic, "Metacritic", "rating_metacritic", null, 100)
    ];

    public static RatingSource? Find(string? source)
        => All.FirstOrDefault(candidate =>
            string.Equals(candidate.Source, source?.Trim(), StringComparison.OrdinalIgnoreCase));
}
