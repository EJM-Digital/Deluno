using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Quality.Contracts;

namespace Deluno.Integrations.Search;

public interface IMediaSearchPlanner
{
    Task<MediaSearchPlan> BuildPlanAsync(
        string title,
        int? year,
        string mediaType,
        string? currentQuality,
        string? targetQuality,
        IReadOnlyList<LibrarySourceLinkItem> sources,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        int? seasonNumber = null,
        int? episodeNumber = null,
        /// <summary>
        /// The quality tiers the governing profile permits. Null or empty leaves
        /// tier selection to the cutoff alone; a non-empty list rejects anything
        /// outside it.
        /// </summary>
        IReadOnlyList<string>? allowedQualities = null,
        CancellationToken cancellationToken = default);
}
