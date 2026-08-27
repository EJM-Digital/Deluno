namespace Deluno.Series.Contracts;

public sealed record SeriesListItem(
    string Id,
    string Title,
    int? StartYear,
    string? ImdbId,
    bool Monitored,
    bool HasFile,
    string? MetadataProvider,
    string? MetadataProviderId,
    string? OriginalTitle,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    IReadOnlyList<MetadataRatingItem> Ratings,
    string? Genres,
    string? ExternalUrl,
    string? MetadataJson,
    DateTimeOffset? MetadataUpdatedUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    /// <summary>
    /// Size of the file Deluno is tracking, and the quality it detected.
    ///
    /// Populated by the paged catalogue query only. The list has always shown a
    /// size column and a size sort, but read them from a metadata blob field no
    /// provider writes — these come from the wanted state, where Deluno actually
    /// records them.
    /// </summary>
    long? FileSizeBytes = null,
    string? CurrentQuality = null,
    /// <summary>
    /// What the file is, and what the title is.
    ///
    /// Populated by the paged catalogue query. The list has always shown a size
    /// column, a codec column and sorts for runtime, popularity and votes, and
    /// read every one of them from a metadata blob that never carried them. The
    /// file-shaped ones come from the file name, the title-shaped ones from the
    /// provider, and the bitrate from dividing one by the other — which is why
    /// it says "approximate" and is not stored.
    /// </summary>
    string? FilePath = null,
    string? VideoCodec = null,
    string? AudioCodec = null,
    string? AudioChannels = null,
    string? ReleaseGroup = null,
    int? RuntimeMinutes = null,
    double? Popularity = null,
    int? VoteCount = null,
    double? ApproximateBitrateMbps = null,
    /// <summary>
    /// The search state Deluno holds for this show, from the paged catalogue
    /// query's join to the wanted state.
    ///
    /// The grid used to read these from <c>/api/series/wanted</c>, whose
    /// <c>recentItems</c> is capped at 25 — so in a library of any size, most
    /// cards had no status, no reason and no target quality at all and fell back
    /// to "is there a file". A page carries its own, however deep it is.
    ///
    /// Null throughout means Deluno is not tracking the show in any library, so
    /// there is no state to report — which is not the same as a state of "no".
    /// </summary>
    string? LibraryId = null,
    string? WantedStatus = null,
    string? WantedReason = null,
    string? TargetQuality = null,
    bool? QualityCutoffMet = null,
    DateTimeOffset? LastSearchUtc = null,
    DateTimeOffset? NextEligibleSearchUtc = null,
    /// <summary>
    /// What the show's episodes add up to — the bar under the poster, and the
    /// rung its dot sits on.
    ///
    /// Counted over what has <em>aired</em>, never over what will exist: an
    /// ongoing show measured against its eventual episode count reads
    /// permanently unfinished, which is true of every ongoing show and therefore
    /// tells you nothing. <c>NextAirDateUtc</c> is the first episode still to
    /// come, and is what makes Upcoming a state rather than a guess.
    /// </summary>
    int EpisodeCount = 0,
    int AiredEpisodeCount = 0,
    int AiredWithFileCount = 0,
    int AiredUpgradableCount = 0,
    DateTimeOffset? NextAirDateUtc = null,
    /// <summary>
    /// The bar under the poster, for a show its episodes carry it, so these stay zero.
    ///
    /// DESIGN-001 gives every title a bar — episodes on a show, subtitle
    /// languages on a film — proportioned to what you asked for. Four languages
    /// and you have English is a quarter green. A title that asked for nothing
    /// keeps a grey bar that claims nothing, rather than no bar, so the shelf
    /// does not change shape when Subber ([#301](https://github.com/jampat000/Deluno/issues/301))
    /// starts filling these in.
    ///
    /// Zero until Subber exists. The contract is here now so the mark does not
    /// have to be redesigned around it later — which is the whole reason the
    /// design settled the vocabulary before the feature.
    /// </summary>
    int SubtitleLanguagesWanted = 0,
    int SubtitleLanguagesHeld = 0);
