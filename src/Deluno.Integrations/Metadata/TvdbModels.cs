namespace Deluno.Integrations.Metadata;

/// <summary>
/// TheTVDB's own attribution, which its terms require to be shown, with a
/// direct link, to anyone viewing metadata that came from it. The broker
/// sends the same text; this is what Deluno shows if it did not.
/// </summary>
public static class TvdbAttribution
{
    public const string Text = "TV metadata provided by TheTVDB";
    public const string Url = "https://thetvdb.com";
}

/// <summary>
/// One series as TheTVDB describes it, in the shape the metadata gateway
/// serves (docs/metadata-broker-contract.md, "TheTVDB"). Direct mode builds
/// the same record from TheTVDB's own v4 API, so everything above this type
/// reads one shape.
/// </summary>
internal sealed record TvdbSeriesRecord(
    string TvdbId,
    string? TmdbId,
    string? ImdbId,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    string? BannerUrl,
    string? LogoUrl,
    IReadOnlyList<string>? Screenshots,
    IReadOnlyList<string>? Genres,
    string? Network,
    string? Status,
    int? RuntimeMinutes,
    string? OriginalLanguage,
    string? Certification,
    IReadOnlyList<string>? Aliases,
    string? ExternalUrl);

internal sealed record TvdbSearchResponse(IReadOnlyList<TvdbSeriesRecord>? Results);

internal sealed record TvdbSeriesResponse(TvdbSeriesRecord? Series);

internal sealed record TvdbRemoteResponse(string? TvdbId);

internal sealed record TvdbCatalogueResponse(IReadOnlyList<TvdbSeason>? Seasons);

internal sealed record TvdbSeason(
    int SeasonNumber,
    string? Name,
    string? AirDate,
    IReadOnlyList<TvdbEpisode>? Episodes);

internal sealed record TvdbEpisode(
    int EpisodeNumber,
    string? Title,
    string? Overview,
    string? AirDate,
    int? AbsoluteNumber);
