namespace Deluno.Contracts;

/// <summary>
/// Every question a movie shelf and a TV shelf can be asked, declared once.
///
/// <para><b>Why this exists.</b> Until #324 the browser held one filter set, one
/// sort list and one poster-options list for both media kinds, and
/// <c>variant</c> reached the panel to decide exactly two things: a hint under
/// Year and which <c>/genres</c> endpoint to call. So a TV shelf was offered a
/// film's controls and a film shelf a show's. Sonarr's Episode Progress, Season
/// Count and Scene Numbering mean nothing on a film; Radarr's In Cinemas,
/// Physical Release and Minimum Availability mean nothing on a series. Poured
/// into one shared panel that is a wall of controls, most of them inert on
/// whichever shelf you are looking at.</para>
///
/// <para><b>The precedent is <c>MediaTableMap.For(MediaKind)</c></b> (ADR-001):
/// anything the two kinds share is written once, and anything genuinely
/// different is declared once, in one place a reader can diff. <see cref="Shared"/>
/// is the first list; <see cref="MovieOnly"/> and <see cref="SeriesOnly"/> are
/// the second.</para>
///
/// <para><b>And the browser does not keep a copy.</b> The field list is served
/// by <c>GET /api/{movies|series}/controls</c> and rendered from there, so the
/// interface cannot offer a filter the server cannot perform — which is the
/// exact failure #302 was deleted for, one layer up.</para>
/// </summary>
public static class CatalogueFilterFields
{
    /// <summary>
    /// Asked of both kinds, and written once.
    ///
    /// <para>The year column differs by name — <c>release_year</c> against
    /// <c>start_year</c> — and that is the one place <c>{year}</c> stands in for
    /// it, the same substitution <see cref="MediaKind"/>'s table map already
    /// makes for the alias.</para>
    /// </summary>
    private static IReadOnlyList<CatalogueFilterField> Shared(MediaKind kind) =>
    [
        new("title", "Title", "The name as your library files it.",
            CatalogueFilterGroup.Title, CatalogueFilterValueKind.Text,
            CatalogueFilterSource.Entry, "{alias}.title"),

        new("originalTitle", "Original title", "The name in its own language, where the metadata carries one.",
            CatalogueFilterGroup.Title, CatalogueFilterValueKind.Text,
            CatalogueFilterSource.Entry, "{alias}.original_title"),

        new("genre", "Genre", "Whole genres — a title tagged Melodrama is not a Drama match.",
            CatalogueFilterGroup.Title, CatalogueFilterValueKind.Genre,
            CatalogueFilterSource.Entry, "{alias}.genres"),

        new("year",
            kind == MediaKind.Movie ? "Year" : "Started",
            kind == MediaKind.Movie ? "The year it was released." : "The year the show started.",
            CatalogueFilterGroup.Title, CatalogueFilterValueKind.Year,
            CatalogueFilterSource.Entry, "{alias}.{year}"),

        new("rating", "Rating", "The metadata score, out of ten.",
            CatalogueFilterGroup.Title, CatalogueFilterValueKind.Rating,
            CatalogueFilterSource.Entry, "{alias}.rating"),

        new("votes", "Rating votes", "How many people the score is drawn from. A 9.4 from eleven votes is not a 9.4.",
            CatalogueFilterGroup.Title, CatalogueFilterValueKind.Integer,
            CatalogueFilterSource.Entry, "{alias}.vote_count"),

        new("popularity", "Popularity", "How much the wider world is watching it.",
            CatalogueFilterGroup.Title, CatalogueFilterValueKind.Decimal,
            CatalogueFilterSource.Entry, "{alias}.popularity"),

        new("runtime", "Runtime", "Minutes.",
            CatalogueFilterGroup.Title, CatalogueFilterValueKind.Minutes,
            CatalogueFilterSource.Entry, "{alias}.runtime_minutes"),

        // Monitoring is deliberately *not* here. It is a separate axis on the
        // query — `monitored=true` — because the facet counts cross it with the
        // status, and it has its own control in the filter panel. Declaring it
        // as a field too would be two ways to ask one question, which is the
        // repetition James reads off the screen before any test does.

        new("imdbId", "IMDb id", "Useful for finding the one title you can name exactly.",
            CatalogueFilterGroup.Title, CatalogueFilterValueKind.Text,
            CatalogueFilterSource.Entry, "{alias}.imdb_id"),

        // ---- The file. Radarr states in its own dialog that it cannot ask any
        // of these: "filters are available only for the properties of a movie,
        // they are not available for properties of the file(s) you may have".
        new("quality", "Quality", "The tier the file actually is. A title with no file matches none of these.",
            CatalogueFilterGroup.File, CatalogueFilterValueKind.QualityTier,
            CatalogueFilterSource.WantedState, "ws.current_quality"),

        new("size", "Size on disk", "Gigabytes. Leave either end blank for no limit.",
            CatalogueFilterGroup.File, CatalogueFilterValueKind.Gigabytes,
            CatalogueFilterSource.WantedState, "ws.file_size_bytes"),

        new("bitrate", "Bitrate", "Size over runtime — the question behind every “why is this 2160p file only four gigabytes”.",
            CatalogueFilterGroup.File, CatalogueFilterValueKind.Decimal,
            CatalogueFilterSource.Entry,
            // Spelled exactly as the expression index in V0016/V0017, in bytes
            // per minute. A stray cast here and the index stops serving it.
            "CAST({alias}.primary_file_size_bytes AS REAL) / NULLIF({alias}.runtime_minutes, 0)"),

        new("videoCodec", "Video codec", "As the file reports it — x265, AVC, HEVC.",
            CatalogueFilterGroup.File, CatalogueFilterValueKind.Text,
            CatalogueFilterSource.WantedState, "ws.video_codec"),

        new("audioCodec", "Audio codec", "TrueHD, DTS-HD, EAC3.",
            CatalogueFilterGroup.File, CatalogueFilterValueKind.Text,
            CatalogueFilterSource.WantedState, "ws.audio_codec"),

        new("audioChannels", "Audio channels", "5.1, 7.1, 2.0.",
            CatalogueFilterGroup.File, CatalogueFilterValueKind.Text,
            CatalogueFilterSource.WantedState, "ws.audio_channels"),

        new("releaseGroup", "Release group", "Who put out the copy you actually hold — not the groups you have configured.",
            CatalogueFilterGroup.File, CatalogueFilterValueKind.Text,
            CatalogueFilterSource.WantedState, "ws.release_group"),

        new("path", "File path", "Where it sits on disk.",
            CatalogueFilterGroup.File, CatalogueFilterValueKind.Text,
            CatalogueFilterSource.WantedState, "ws.file_path"),

        new("hasFile", "Has a file", "Whether anything is on disk for it at all.",
            CatalogueFilterGroup.File, CatalogueFilterValueKind.Boolean,
            CatalogueFilterSource.WantedState, "COALESCE(ws.has_file, 0)"),

        // ---- Time. Relative first, so a saved view does not go stale.
        new("added", "Added", "When it joined your library.",
            CatalogueFilterGroup.Time, CatalogueFilterValueKind.Date,
            CatalogueFilterSource.Entry, "{alias}.created_utc"),

        new("metadataUpdated", "Metadata refreshed", "When Deluno last read this title from its provider.",
            CatalogueFilterGroup.Time, CatalogueFilterValueKind.Date,
            CatalogueFilterSource.Entry, "{alias}.metadata_updated_utc"),

        // ---- What Deluno decided. Nothing else in this space asks any of it.
        // Deluno's verdict is not here either: the legend chips above the shelf
        // are that question, and they are the colour key for the marks on the
        // posters below them. A field saying the same thing would be a second
        // control selecting the same rows and disagreeing at the edges, because
        // "missing" on the chips means no file and already out, while the stored
        // column is written by a different rule.

        new("wantedReason", "Why", "The sentence Deluno wrote about why the title is on the list.",
            CatalogueFilterGroup.Decision, CatalogueFilterValueKind.Text,
            CatalogueFilterSource.WantedState, "ws.wanted_reason"),

        new("targetQuality", "Target quality", "What the profile asked for, as opposed to what you have.",
            CatalogueFilterGroup.Decision, CatalogueFilterValueKind.QualityTier,
            CatalogueFilterSource.WantedState, "ws.target_quality"),

        new("cutoffMet", "Quality cutoff met", "Whether Deluno has stopped looking for something better.",
            CatalogueFilterGroup.Decision, CatalogueFilterValueKind.Boolean,
            CatalogueFilterSource.WantedState, "COALESCE(ws.quality_cutoff_met, 0)"),

        new("lastSearch", "Last searched", "Never searched, or not searched in ninety days, is a real question and nothing else can ask it.",
            CatalogueFilterGroup.Decision, CatalogueFilterValueKind.Date,
            CatalogueFilterSource.WantedState, "ws.last_search_utc"),

        new("nextEligibleSearch", "Next eligible search", "When the retry delay lets Deluno try again.",
            CatalogueFilterGroup.Decision, CatalogueFilterValueKind.Date,
            CatalogueFilterSource.WantedState, "ws.next_eligible_search_utc"),

        new("lastSearchResult", "Last search result", "What came back the last time Deluno looked.",
            CatalogueFilterGroup.Decision, CatalogueFilterValueKind.Text,
            CatalogueFilterSource.WantedState, "ws.last_search_result"),

        new("missingSince", "Missing since", "How long a gap has been a gap.",
            CatalogueFilterGroup.Decision, CatalogueFilterValueKind.Date,
            CatalogueFilterSource.WantedState, "ws.missing_since_utc")
    ];

    /// <summary>
    /// A film has three release dates and an availability rule, and a series has
    /// none of them. Sonarr does not offer these because they do not exist for a
    /// show — which is the point of splitting the list rather than showing a
    /// reader nine controls that can only ever match nothing.
    /// </summary>
    private static IReadOnlyList<CatalogueFilterField> MovieOnly =>
    [
        new("inCinemas", "In cinemas", "The theatrical release date.",
            CatalogueFilterGroup.Time, CatalogueFilterValueKind.Date,
            CatalogueFilterSource.Entry, "{alias}.in_cinemas_date"),

        new("digitalRelease", "Digital release", "When it became buyable or streamable.",
            CatalogueFilterGroup.Time, CatalogueFilterValueKind.Date,
            CatalogueFilterSource.Entry, "{alias}.digital_release_date"),

        new("physicalRelease", "Physical release", "Disc.",
            CatalogueFilterGroup.Time, CatalogueFilterValueKind.Date,
            CatalogueFilterSource.Entry, "{alias}.physical_release_date"),

        new("minimumAvailability", "Minimum availability", "The point Deluno is allowed to start looking.",
            CatalogueFilterGroup.Decision, CatalogueFilterValueKind.Enum,
            CatalogueFilterSource.Entry, "{alias}.minimum_availability",
            Options: ["announced", "inCinemas", "released"])
    ];

    /// <summary>
    /// A show has a network; a film has a studio, which arrives with #306. Both
    /// are "who made it", and they are still two columns on two tables, so they
    /// are two declarations rather than one shared row with a conditional column.
    /// </summary>
    private static IReadOnlyList<CatalogueFilterField> SeriesOnly =>
    [
        new("network", "Network", "Who broadcasts it.",
            CatalogueFilterGroup.Title, CatalogueFilterValueKind.Text,
            CatalogueFilterSource.Entry, "{alias}.network")
    ];

    public static IReadOnlyList<CatalogueFilterField> For(MediaKind kind)
        => kind switch
        {
            MediaKind.Movie => [.. Shared(kind), .. MovieOnly],
            MediaKind.Series => [.. Shared(kind), .. SeriesOnly],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    /// <summary>
    /// The field, or null for an id this kind does not have.
    ///
    /// <para>Null is the whole safety property: the endpoint turns it into a 400
    /// rather than dropping the condition. A filter that is silently ignored is
    /// a shelf that looks narrowed and is not, and #302's ghost branches are the
    /// record of how long that can go unnoticed.</para>
    /// </summary>
    public static CatalogueFilterField? Find(MediaKind kind, string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : For(kind).FirstOrDefault(field => string.Equals(field.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
}
