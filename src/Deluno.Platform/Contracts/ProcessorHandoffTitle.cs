namespace Deluno.Platform.Contracts;

/// <summary>
/// What Deluno has stored about the title a processor hand-off is for (#1104).
/// Every value is optional: only what Deluno knows is sent, never a guess.
/// </summary>
public sealed record ProcessorHandoffTitle(
    string? Title = null,
    int? Year = null,
    int? TmdbId = null,
    int? TvdbId = null,
    string? ImdbId = null,
    int? Season = null,
    int? Episode = null,
    string? PosterUrl = null);

/// <summary>
/// Looks up the title behind a hand-off from what Deluno already stores (no network call).
/// The movie side and the series side stay separate; the implementation picks by the
/// download's entity type. Returns null when the hand-off is not tied to a known title.
/// </summary>
public interface IProcessorHandoffTitleResolver
{
    Task<ProcessorHandoffTitle?> ResolveAsync(ProcessorHandoffItem handoff, CancellationToken cancellationToken);
}

/// <summary>
/// The public address a locally cached artwork file came from (#1104). A processor cannot
/// fetch Deluno's own cache path, so a hand-off names the remote address instead.
/// </summary>
public interface IArtworkRemoteUrls
{
    Task<string?> FindRemoteUrlAsync(string cacheKey, CancellationToken cancellationToken);
}
