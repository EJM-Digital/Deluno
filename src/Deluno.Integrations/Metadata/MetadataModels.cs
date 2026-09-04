using Deluno.Contracts;

namespace Deluno.Integrations.Metadata;

public sealed record MetadataSearchResult(
    string Provider,
    string ProviderId,
    string MediaType,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    IReadOnlyList<MetadataRatingItem> Ratings,
    IReadOnlyList<string> Genres,
    string? ImdbId,
    string? ExternalUrl,
    IReadOnlyList<MetadataCastMember>? Cast = null,
    /// <summary>
    /// Who made it, beside who is in it.
    ///
    /// <para>A detail page that lists the cast and stops there answers half the
    /// question — Radarr's does both, and the crew is the half that says whose
    /// film it is. One <see cref="Director"/> was all Deluno ever kept, which is
    /// the right shape for a sort column and much too thin for a page.</para>
    /// </summary>
    IReadOnlyList<MetadataCrewMember>? Crew = null,
    // Movies carry several dates and they mean different things: a movie can be
    // in cinemas months before it is obtainable. `Year` alone cannot express
    // "not out yet", which is what an availability rule needs to decide.
    DateOnly? InCinemasDate = null,
    DateOnly? DigitalReleaseDate = null,
    DateOnly? PhysicalReleaseDate = null,
    /// <summary>
    /// Runtime, popularity and vote count.
    ///
    /// The library list has always offered sorting on all three and never had
    /// any of them: they were read from a metadata blob that did not carry
    /// them, so every title compared as zero. Runtime earns its place twice
    /// over — it is also the denominator that turns a file size into a bitrate.
    /// </summary>
    int? RuntimeMinutes = null,
    double? Popularity = null,
    int? VoteCount = null,
    /// <summary>
    /// The rest of what a provider detail lookup already carries, and Deluno
    /// had never asked it for.
    ///
    /// Three of these are not new ideas: the library adapters read
    /// <c>certification</c>, <c>collection</c> and <c>language</c> straight out
    /// of the stored metadata blob and have done for a long time, and nothing
    /// ever put a value in any of them — so they read empty on every install,
    /// the same shape as the codec and release-group columns the list displayed
    /// for months with nothing populating them.
    ///
    /// The rest are what Radarr and Sonarr let a library be organised by:
    /// studio, network, and whether a show has ended. A show that has finished
    /// and is missing episodes is a different problem from one still airing
    /// them, and until now Deluno could not tell the two apart.
    /// </summary>
    string? Certification = null,
    string? Studio = null,
    string? Network = null,
    string? Collection = null,
    /// <summary>The provider identifier for <see cref="Collection"/>, when one exists.</summary>
    string? CollectionProviderId = null,
    string? Director = null,
    string? TrailerUrl = null,
    string? Tagline = null,
    string? Homepage = null,
    string? OriginalLanguage = null,
    /// <summary><c>Released</c> / <c>In Production</c>, or <c>Ended</c> / <c>Returning Series</c>.</summary>
    string? Status = null,
    /// <summary>
    /// What a title is about, beyond its genre.
    ///
    /// <para>"Space travel" and "time loop" are questions Genre cannot ask,
    /// because a film is Science Fiction either way. The browser has had a
    /// <c>keywords</c> field since long before anything fetched them, so it has
    /// always read empty — the same shape as the certification and collection
    /// fields beside it.</para>
    /// </summary>
    IReadOnlyList<string>? Keywords = null,
    /// <summary>
    /// The TVDb identifier when the provider exposes one. TMDb carries this in
    /// its external-id response; keeping it beside IMDb lets the TV folder
    /// naming preset work without pretending a TMDb id is a TVDb id.
    /// </summary>
    string? TvDbId = null)
{
    /// <summary>
    /// The catalogue entry Deluno would land on if this result were added, when
    /// it already holds one.
    ///
    /// <para><b>The dedupe was never the missing part.</b> Adding a title the
    /// catalogue already holds has always been a no-op that hands back the
    /// existing row - three unique indexes and
    /// <c>FindEntryIdAsync</c> see to that. What the Add screen never did was
    /// <i>say so</i>, so the only way to learn you already owned something was
    /// to add it and watch nothing happen.</para>
    ///
    /// <para>It is answered on the server because it cannot honestly be
    /// answered anywhere else: the library screen holds one page of the
    /// catalogue, so a title you own that is not on that page would read as
    /// new.</para>
    ///
    /// <para>Null on everything the provider itself returns. It is set once,
    /// by the search endpoint, after the provider - including its cache - has
    /// finished. Nothing persists it.</para>
    /// </summary>
    public string? LibraryEntryId { get; init; }
}

/// <summary>
/// When a movie can actually be obtained. A cinema date is not an availability
/// date — searching on it wastes every cycle until a digital release exists.
/// </summary>
public sealed record MetadataReleaseDates(
    DateOnly? InCinemas,
    DateOnly? Digital,
    DateOnly? Physical)
{
    public static readonly MetadataReleaseDates None = new(null, null, null);

    public bool HasAny => InCinemas is not null || Digital is not null || Physical is not null;
}

/// <summary>A provider collection and its full movie membership.</summary>
public sealed record MetadataCollection(
    string Provider,
    string ProviderId,
    string Name,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    IReadOnlyList<MetadataCollectionMovie> Movies);

/// <summary>One movie in a provider collection, whether Deluno holds it or not.</summary>
public sealed record MetadataCollectionMovie(
    string ProviderId,
    string Title,
    int? Year,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    string? ExternalUrl,
    string? ImdbId = null);

/// <summary>One season of a series as the provider describes it.</summary>
public sealed record MetadataSeason(
    int SeasonNumber,
    string? Name,
    int EpisodeCount,
    DateOnly? AirDate,
    IReadOnlyList<MetadataEpisode> Episodes);

/// <summary>
/// One episode as the provider describes it — whether or not a file for it
/// exists. The catalogue is the provider's; what is on disk is an overlay.
/// </summary>
public sealed record MetadataEpisode(
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    string? Overview,
    DateTimeOffset? AirDateUtc);

public sealed record MetadataCastMember(
    string Name,
    string? Character,
    string? ProfileUrl,
    /// <summary>
    /// The provider's id for this person.
    ///
    /// <para>What a credit links to, and what following a person's filmography
    /// would key on. Neither is possible from a name: names collide, and two
    /// different Chris Evanses are one row if you key on the string.</para>
    /// </summary>
    string? PersonId = null,
    /// <summary>Lazy-resolved IMDb person URL, when the broker can provide one.</summary>
    string? ImdbUrl = null);

/// <summary>
/// A crew credit. <paramref name="Job"/> holds every job this person did on the
/// title, joined — the same person is routinely credited three times, and three
/// identical portraits in a row reads as a bug rather than as a fuller credit.
/// </summary>
public sealed record MetadataCrewMember(
    string Name,
    string? Job,
    string? ProfileUrl,
    /// <summary>The provider's id for this person. See <see cref="MetadataCastMember.PersonId"/>.</summary>
    string? PersonId = null,
    /// <summary>Lazy-resolved IMDb person URL, when the broker can provide one.</summary>
    string? ImdbUrl = null);

public sealed record MetadataRatingItem(
    string Source,
    string Label,
    double? Score,
    double? MaxScore,
    int? VoteCount,
    string? Url,
    string? Kind);

public sealed record MetadataLookupRequest(
    string? Query,
    string? MediaType,
    int? Year,
    string? ProviderId);

/// <summary>
/// The result of resolving a title Deluno has already linked to a provider.
/// Exact identity lookup is deliberately separate from fuzzy discovery: a
/// missing provider record must never be replaced by the first similar title.
/// </summary>
public sealed record MetadataProviderRecordLookup(
    MetadataProviderRecordStatus Status,
    string Provider,
    string ProviderId,
    MetadataSearchResult? Result = null,
    Deluno.Contracts.IntegrationFailure? Failure = null);

public enum MetadataProviderRecordStatus
{
    Found,
    Missing,
    Unavailable
}

/// <summary>
/// What a caller is told when the metadata provider could not answer at all.
///
/// <para>A bare 503 with no body is an external-service failure that has been
/// swallowed: Deluno knows which provider it asked, what the provider said and
/// whether it will try again, and then throws all of it away at the boundary,
/// leaving every surface with nothing to say but "could not be refreshed"
/// (#338). The typed failure travels with the status instead.</para>
/// </summary>
public sealed record MetadataProviderUnavailable(
    string Code,
    string Message,
    IntegrationFailure? Failure);

public static class MetadataProviderResponses
{
    /// <summary>
    /// The unavailable payload for a resolved-but-unanswered provider record.
    /// <paramref name="consequence"/> says what happened to the owner's title,
    /// which is the part they actually care about.
    /// </summary>
    public static MetadataProviderUnavailable Unavailable(
        MetadataProviderRecordLookup lookup,
        string consequence)
        => Unavailable(lookup.Provider, lookup.Failure, consequence);

    public static MetadataProviderUnavailable Unavailable(
        string provider,
        IntegrationFailure? failure,
        string consequence)
    {
        var provider_ = string.IsNullOrWhiteSpace(provider) ? "The metadata provider" : provider.ToUpperInvariant();
        // The failure's own message when there is one - it names the cause.
        // The generic sentence is the fallback, not the first answer.
        var cause = failure?.Message is { Length: > 0 } message
            ? $"{provider_} could not answer: {message}"
            : $"{provider_} is temporarily unavailable.";
        return new MetadataProviderUnavailable(
            "metadata-provider-unavailable",
            $"{cause} {consequence}".Trim(),
            failure);
    }
}

/// <summary>A calm, title-scoped notice about a provider identity that no longer exists.</summary>
public sealed record MetadataProviderIssue(
    string Kind,
    string Provider,
    string ProviderId,
    string EvidenceKey,
    DateTimeOffset DetectedUtc,
    DateTimeOffset? AcknowledgedUtc);

/// <summary>The identity facts compared before a held title is linked to another provider record.</summary>
public sealed record MetadataLinkIdentity(
    string? Provider,
    string? ProviderId,
    string Title,
    int? Year,
    string? ImdbId,
    string? Context = null);

/// <summary>A different held title that already owns one of the proposed identities.</summary>
public sealed record MetadataIdentityConflict(
    string Id,
    string Title,
    string Reason);

public sealed class MetadataIdentityConflictException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);

/// <summary>The TV catalogue effect of accepting a provider remap.</summary>
public sealed record MetadataCatalogueImpact(
    int ExistingEpisodeCount,
    int ImportedEpisodeCount,
    int ProposedEpisodeCount,
    int ProposedSeasonCount,
    int ExistingEpisodesOutsideProposed);

public sealed record MetadataEpisodeIdentity(
    int SeasonNumber,
    int EpisodeNumber,
    bool HasFile = false);

public sealed record MetadataCatalogueEvaluation(
    MetadataCatalogueImpact Impact,
    int NewEpisodeCount,
    IReadOnlyList<string> ProposedKeys)
{
    public bool PreservesExistingCatalogue => Impact.ExistingEpisodesOutsideProposed == 0;
}

public static class MetadataCatalogueSafety
{
    public static MetadataCatalogueEvaluation Evaluate(
        IEnumerable<MetadataEpisodeIdentity> existingEpisodes,
        IEnumerable<MetadataEpisodeIdentity> proposedEpisodes)
    {
        var existing = existingEpisodes
            .GroupBy(EpisodeKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var existingKeys = existing.Select(EpisodeKey).ToHashSet(StringComparer.Ordinal);
        var proposed = proposedEpisodes
            .GroupBy(EpisodeKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var proposedKeys = proposed
            .Select(EpisodeKey)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var proposedSet = proposedKeys.ToHashSet(StringComparer.Ordinal);
        var impact = new MetadataCatalogueImpact(
            existing.Length,
            existing.Count(episode => episode.HasFile),
            proposed.Length,
            proposed.Select(episode => episode.SeasonNumber).Distinct().Count(),
            existingKeys.Count(key => !proposedSet.Contains(key)));
        return new MetadataCatalogueEvaluation(
            impact,
            proposedSet.Count(key => !existingKeys.Contains(key)),
            proposedKeys);
    }

    private static string EpisodeKey(MetadataEpisodeIdentity episode)
        => $"S{episode.SeasonNumber:D4}E{episode.EpisodeNumber:D4}";
}

/// <summary>
/// A reviewable metadata remap. Applying it requires <see cref="ConfirmationToken"/>,
/// which binds the reviewed provider answer to the current stored title state.
/// </summary>
public sealed record MetadataLinkPreview(
    string MediaType,
    string SubjectId,
    MetadataLinkIdentity Current,
    MetadataLinkIdentity Proposed,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Consequences,
    MetadataIdentityConflict? Conflict,
    MetadataCatalogueImpact? CatalogueImpact,
    bool CanApply,
    string? BlockReason,
    string ConfirmationToken);

public static class MetadataLinkPreviewTokens
{
    public static string Create(
        string subjectId,
        DateTimeOffset subjectUpdatedUtc,
        MetadataLinkIdentity proposed,
        IEnumerable<string>? catalogueKeys = null)
    {
        var payload = string.Join('\n', new[]
        {
            subjectId,
            subjectUpdatedUtc.ToUniversalTime().ToString("O"),
            proposed.Provider ?? string.Empty,
            proposed.ProviderId ?? string.Empty,
            proposed.Title,
            proposed.Year?.ToString() ?? string.Empty,
            proposed.ImdbId ?? string.Empty,
            proposed.Context ?? string.Empty,
            string.Join(',', catalogueKeys ?? [])
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
    }
}

public sealed record MetadataProviderStatus(
    string Provider,
    bool IsConfigured,
    string Mode,
    string Message,
    IReadOnlyList<MetadataSourceStatus> Sources,
    IntegrationFailure? LastFailure = null);

public sealed record MetadataSourceStatus(
    string Source,
    string Label,
    string Role,
    bool IsConfigured,
    string Mode,
    string Message);
