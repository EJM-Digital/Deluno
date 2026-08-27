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
    /// The bar under the poster: the subtitle languages you asked for.
    ///
    /// DESIGN-001 gives every title a bar, and it is subtitle
    /// languages, on exactly the same terms as a movie's — which is the point:
    /// the bar used to count aired episodes here, so one strip of pixels asked a
    /// different question on the TV shelf than on the Movies shelf, and a show
    /// could never show its subtitle state at all.
    ///
    /// <c>Wanted</c> is the languages asked for **per episode**. <c>Held</c> is
    /// how many are present, **summed across the episodes the show has on
    /// disk**. Thirteen episodes with two languages asked for of each is 26
    /// slots; four episodes short a language makes the bar 22/26 green.
    ///
    /// Measured only over episodes on disk. Counting the ones you are missing
    /// would drag the bar down for a reason that is not about subtitles, and the
    /// dot above it already says the show is Missing.
    ///
    /// Zero until Subber ([#301](https://github.com/jampat000/Deluno/issues/301))
    /// exists.
    /// </summary>
    int SubtitleLanguagesWanted = 0,
    int SubtitleLanguagesHeld = 0);
