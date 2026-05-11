using Deluno.Platform.Contracts;

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
        CancellationToken cancellationToken = default);
}
