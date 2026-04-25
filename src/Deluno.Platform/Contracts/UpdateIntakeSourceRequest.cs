namespace Deluno.Platform.Contracts;

public sealed record UpdateIntakeSourceRequest(
    string Name,
    string Provider,
    string FeedUrl,
    string? MediaType,
    string? LibraryId,
    string? QualityProfileId,
    bool SearchOnAdd,
    bool IsEnabled);
