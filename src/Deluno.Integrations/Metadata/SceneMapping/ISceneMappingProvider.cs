namespace Deluno.Integrations.Metadata.SceneMapping;

/// <summary>
/// One episode where a scene-numbering source disagrees with the provider's
/// own season/episode order — the anime problem Sonarr leans on TheXEM for.
/// <see cref="ProviderSeasonNumber"/>/<see cref="ProviderEpisodeNumber"/> are
/// the canonical key a title is catalogued under; the rest are the alternate
/// keys a release is actually named with.
/// </summary>
public sealed record SceneMappingEntry(
    int ProviderSeasonNumber,
    int ProviderEpisodeNumber,
    int? SceneSeasonNumber,
    int? SceneEpisodeNumber,
    int? AbsoluteNumber);

/// <summary>
/// A source of scene/absolute numbering corrections, keyed by a title's TVDB
/// id — the identity every scene-numbering source in general use keys by,
/// TheXEM included. Deluno does not itself decide scene numbers; it asks
/// somewhere that tracks the fansub community's actual numbering, the same
/// way Sonarr does.
/// </summary>
public interface ISceneMappingProvider
{
    /// <summary>What the mapping is sourced from, for logging and failure attribution.</summary>
    string Key { get; }

    /// <summary>
    /// Every scene/absolute correction TheXEM (or an equivalent source) holds
    /// for this title, or an empty list when the title has no mapping —
    /// true of most shows, since only numbering mismatches are worth
    /// recording at all. Never throws: an unreachable or unmapped source is
    /// exactly as useful to a caller as one that answered "nothing here",
    /// and a background refresh must not fail a whole pass over one title.
    /// </summary>
    Task<IReadOnlyList<SceneMappingEntry>> GetMappingAsync(string tvdbId, CancellationToken cancellationToken);
}
