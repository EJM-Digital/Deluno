namespace Deluno.Contracts;

/// <summary>
/// One filter field as the browser receives it.
///
/// <para>The enums are projected to their <b>query-string tokens</b> rather than
/// serialised by name, so the interface speaks exactly the vocabulary a URL
/// does. Serving "Includes" while the URL says "in" would be two names for one
/// operator, which is where this codebase's defects come from.</para>
/// </summary>
public sealed record CatalogueFilterFieldView(
    string Id,
    string Label,
    string Hint,
    string Group,
    string ValueKind,
    IReadOnlyList<string> Operators,
    IReadOnlyList<string>? Options);

/// <summary>One order a shelf can be arranged in, with the line that says what it measures.</summary>
/// <param name="Id">A value of <see cref="CatalogueSortFields"/>. The two lists are held together by a test.</param>
public sealed record CatalogueSortOption(string Id, string Label, string Hint);

/// <summary>
/// One thing a poster may carry beyond the artwork.
/// </summary>
/// <param name="DefaultOn">
/// The essentials are on; the extras are off. They are the answer to "show me
/// more", and a card that arrives already carrying everything has nothing left
/// to ask for.
/// </param>
/// <param name="Line">
/// Whether it draws on the card's own row or joins the single truncated line
/// under the title. One row each would bury the artwork the grid exists to show.
/// </param>
public sealed record CataloguePosterOption(
    string Id,
    string Label,
    string Description,
    bool DefaultOn,
    bool Line = false,
    /// <summary>
    /// The provider this switch has nothing to draw without, or <c>null</c>
    /// when it stands on its own.
    ///
    /// <para>IMDb, Rotten Tomatoes and Metacritic all come from OMDb. With no
    /// key configured they can only ever draw a dash, and three switches that
    /// do nothing are indistinguishable from three broken ones — which is
    /// exactly how they read on the rig. The interface says so instead.</para>
    /// </summary>
    string? Requires = null);

/// <summary>
/// Everything the library toolbar offers for one media kind: what it can filter
/// by, what it can order by, and what a poster may carry.
///
/// <para><b>Served, not copied.</b> The browser fetches this from
/// <c>GET /api/{movies|series}/controls</c> and renders the lists it is given.
/// Before #324 the browser kept its own <c>sortFieldOptions</c> beside the
/// server's <c>CatalogueSortFields</c> and its own <c>DisplayOptions</c> beside
/// nothing at all, which is the shape every defect in this codebase has had —
/// one rule written twice in places that cannot check each other. A shelf can no
/// longer offer an order the query cannot perform, because there is only one
/// list.</para>
/// </summary>
public sealed record CatalogueControls(
    string Kind,
    IReadOnlyList<CatalogueFilterFieldView> FilterFields,
    IReadOnlyList<CatalogueSortOption> SortFields,
    IReadOnlyList<CataloguePosterOption> PosterOptions)
{
    private static IReadOnlyList<CatalogueSortOption> SharedSorts =>
    [
        new(CatalogueSortFields.Title, "Title", "A to Z"),
        new(CatalogueSortFields.Year, "Year", "When it came out"),
        new(CatalogueSortFields.Added, "Added", "When you added it"),
        new(CatalogueSortFields.Size, "Size", "How big the file is"),
        new(CatalogueSortFields.Quality, "Quality", "By the ladder, not the alphabet"),
        new(CatalogueSortFields.Bitrate, "Bitrate", "How much file there is per minute"),
        new(CatalogueSortFields.Runtime, "Runtime", "How long it runs"),
        new(CatalogueSortFields.Rating, "Rating", "The metadata score"),
        new(CatalogueSortFields.Popularity, "Popularity", "How much the world is watching"),
        .. RatingSorts,

        // #310: "a new axis does not excuse a smaller number on an old one".
        // Being the only tool that can sort by bitrate is no comfort to
        // somebody who cannot sort by studio.
        new(CatalogueSortFields.Monitored, "Monitored", "What you have told Deluno to leave alone, and what you have not"),
        new(CatalogueSortFields.Certification, "Certification", "By classification — PG-13, 15, MA15+"),
        new(CatalogueSortFields.OriginalTitle, "Original title", "The name in its own language"),
        new(CatalogueSortFields.OriginalLanguage, "Original language", "Group what was made in the same language"),

        // Both shelves, because both have files. Radarr offers it on one.
        new(CatalogueSortFields.Path, "Path", "Where it sits on disk")
    ];

    /// <summary>Orders only a film can be put in.</summary>
    private static IReadOnlyList<CatalogueSortOption> MovieOnlySorts =>
    [
        new(CatalogueSortFields.Studio, "Studio", "Who made it"),
        new(CatalogueSortFields.InCinemas, "In cinemas", "The theatrical date"),
        new(CatalogueSortFields.DigitalRelease, "Digital release", "When it became buyable"),
        new(CatalogueSortFields.PhysicalRelease, "Physical release", "Disc")
    ];

    /// <summary>
    /// One order per rating source, beside the blended one.
    ///
    /// <para>Radarr offers four and Deluno offered a single average of them.
    /// Averaging is the one thing you cannot undo afterwards: a title at IMDb
    /// 8.1 and Rotten Tomatoes 41% and one at 8.1 and 94% blend to numbers a
    /// person cannot tell apart, and telling them apart is the whole reason for
    /// looking (#319).</para>
    /// </summary>
    private static IEnumerable<CatalogueSortOption> RatingSorts
        => RatingSources.All.Select(source => new CatalogueSortOption(
            CatalogueSortFields.ForRating(source.Source),
            $"{source.Label} score",
            source.MaxScore == 100
                ? $"Out of a hundred, as {source.Label} reports it"
                : $"Out of ten, as {source.Label} reports it"));

    /// <summary>What a card can show once you want a particular source's number.</summary>
    private static IEnumerable<CataloguePosterOption> RatingPosterOptions
        => RatingSources.All.Select(source => new CataloguePosterOption(
            $"showRating{source.Source.Replace("_", string.Empty)}",
            source.Label,
            $"The {source.Label} score, whether or not it is the preferred one",
            DefaultOn: false,
            Line: true,
            // TMDb comes with the metadata lookup Deluno already does. The
            // other three are OMDb's, and OMDb is optional.
            Requires: source.Source == RatingSources.Tmdb ? null : "omdb"));

    /// <summary>
    /// Sorts that only mean something for a show.
    ///
    /// <para>A film has no next episode and no network, and is never partway
    /// through anything. Offering these on the Movies shelf would be three
    /// controls that can only ever do nothing — which is the failure #324 was
    /// opened about, one layer along.</para>
    /// </summary>
    private static IReadOnlyList<CatalogueSortOption> SeriesOnlySorts =>
    [
        new(CatalogueSortFields.NextAiring, "Next airing", "When the next episode is due"),
        new(CatalogueSortFields.EpisodeProgress, "Episode progress", "How many aired episodes you hold"),
        new(CatalogueSortFields.Network, "Network", "Who broadcasts it")
    ];

    /// <summary>
    /// What only a film's card can show.
    ///
    /// <para>Three dates rather than one, because they answer three different
    /// questions and a film can be in cinemas months before it is obtainable.
    /// A show has an air date instead, and offering "physical release" on the
    /// TV shelf would be a switch that can only ever do nothing.</para>
    /// </summary>
    private static IReadOnlyList<CataloguePosterOption> MovieOnlyPosterOptions =>
    [
        new("showInCinemas", "In cinemas", "The theatrical date", DefaultOn: false, Line: true),
        new("showDigitalRelease", "Digital release", "When it became buyable or streamable", DefaultOn: false, Line: true),
        new("showPhysicalRelease", "Physical release", "Disc", DefaultOn: false, Line: true),
        // ── DESIGN-006, the film card ──────────────────────────────────────
        //
        // These are on the movie shelf ONLY, and deliberately so. James: "they
        // should be independant of each other, tv and movie" — the film card is
        // settled and the show card is not, so TV keeps what it has until it is
        // decided on its own terms. The TV equivalent is "Episode count on the
        // bar" and it arrives with that decision, not with this one.
        //
        // Named for what they put on the card rather than for the widget. Sonarr
        // and Radarr both call this "Detailed Progress Bar — show text on
        // progress bar", which names the control and says nothing about what
        // appears; a reader of the movie shelf wants to know it is the quality.
        new("showQualityOnBar", "Quality on the bar", "The quality you hold, written across the top bar", DefaultOn: true),
        new("showSubtitleCountOnBar", "Subtitle count on the bar", "How many of the languages you asked for are here", DefaultOn: true)
    ];

    /// <summary>What only a show's card can show.</summary>
    private static IReadOnlyList<CataloguePosterOption> SeriesOnlyPosterOptions =>
    [
        new("showNextAiring", "Next airing", "When the next episode is due", DefaultOn: false, Line: true),
        // ── Shared until DESIGN-006 reaches the TV shelf ───────────────────
        //
        // This was a shared option. It is show-only now, not because a film has
        // no monitoring, but because the film card says it WITHOUT words: an
        // unmonitored title's bars go flat grey, overriding whatever rung it
        // sits on. A line reading "Not monitored" beneath bars that have already
        // gone grey is the same fact twice — the rule that removed the Quality
        // option the moment the bar carried the quality.
        //
        // The show card still draws the shield line, because the show card has
        // not adopted DESIGN-006 yet. When it does, this leaves with it.
        new("showMonitored", "Monitoring", "Whether Deluno is watching for this title", DefaultOn: true, Line: true)
        // Episode progress is not here either, and for the opposite reason: it
        // is now on every show's card whether you ask for it or not, in the
        // corner. A switch for a line reading "3/20 episodes" underneath would
        // be the same fact twice on one card.
    ];

    /// <summary>
    /// What every card can show, whichever shelf it is on.
    ///
    /// <para>The first five are the essentials and are on. The rest share one
    /// truncated line under the title.</para>
    /// </summary>
    private static IReadOnlyList<CataloguePosterOption> SharedPosterOptions =>
    [
        new("showTitle", "Title", "The movie or series name", DefaultOn: true),
        // One switch, one fact. It used to draw the release year *and* the
        // monitored state, so neither could be turned off without the other —
        // James: "year should be removed as a not required option and it should
        // not be aligned to monitored or not monitored". The year is gone and
        // this does the one thing its name says.

        // **The status mark is not here, and that is deliberate.** It was a
        // switch — "Status mark", on by default — and James: *"I think status
        // mark should not be an option, its mandatory"*. He is right, and the
        // switch had a second cost beyond the obvious one: the chip row above
        // the shelf is the legend for these colours, and it does not know what
        // a reader has switched off. A shelf with the mark hidden left a legend
        // explaining five colours that were nowhere on the page.
        //
        // Everything else here is a detail about a title. The mark is what the
        // shelf is *for*.
        // **Quality is not a poster option any more.** It was the tier printed
        // on the state bar, and that label is what three rounds of wash-out
        // were about: a word whose ground changes with the episode count cannot
        // be given a colour that works on every card. James, after four
        // rendered treatments, chose the one with no words on artwork at all.
        // The tier is on the list row, in the drawer and on the detail page.
        // A line of its own, like every other switch. "Year and monitoring" is
        // one switch and gets one row; the rating is a second switch and shared
        // that row until James pointed out the rule — nothing shares a row.
        new("showRating", "Rating", "The preferred metadata score", DefaultOn: true, Line: true),
        new("showSize", "Size", "What the file takes on disk", DefaultOn: false, Line: true),
        new("showRuntime", "Runtime", "How long it runs", DefaultOn: false, Line: true),
        new("showGenres", "Genres", "The first two it is tagged with", DefaultOn: false, Line: true),
        new("showReleaseGroup", "Release group", "Who put the release out", DefaultOn: false, Line: true),
        new("showCodec", "Codec", "Video and audio, as the file name reports them", DefaultOn: false, Line: true),
        new("showAdded", "Added", "The day it joined your library", DefaultOn: false, Line: true),
        .. RatingPosterOptions
    ];

    public static CatalogueControls For(MediaKind kind)
        => new(
            kind == MediaKind.Movie ? "movies" : "shows",
            [.. CatalogueFilterFields.For(kind).Select(View)],
            kind == MediaKind.Series ? [.. SharedSorts, .. SeriesOnlySorts] : [.. SharedSorts, .. MovieOnlySorts],
            kind == MediaKind.Series
                ? [.. SharedPosterOptions, .. SeriesOnlyPosterOptions]
                : [.. SharedPosterOptions, .. MovieOnlyPosterOptions]);

    private static CatalogueFilterFieldView View(CatalogueFilterField field)
        => new(
            field.Id,
            field.Label,
            field.Hint,
            field.Group switch
            {
                CatalogueFilterGroup.File => "file",
                CatalogueFilterGroup.Time => "time",
                CatalogueFilterGroup.Decision => "decision",
                _ => "title"
            },
            field.ValueKind switch
            {
                CatalogueFilterValueKind.Integer => "integer",
                CatalogueFilterValueKind.Decimal => "decimal",
                CatalogueFilterValueKind.Year => "year",
                CatalogueFilterValueKind.Minutes => "minutes",
                CatalogueFilterValueKind.Gigabytes => "gigabytes",
                CatalogueFilterValueKind.Rating => "rating",
                CatalogueFilterValueKind.Date => "date",
                CatalogueFilterValueKind.Boolean => "boolean",
                CatalogueFilterValueKind.QualityTier => "quality",
                CatalogueFilterValueKind.Genre => "genre",
                CatalogueFilterValueKind.Enum => "enum",
                _ => "text"
            },
            [.. field.Operators.Select(CatalogueFilterOperators.Token)],
            field.Options);
}
