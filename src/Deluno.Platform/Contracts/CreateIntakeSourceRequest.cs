namespace Deluno.Platform.Contracts;

public sealed record CreateIntakeSourceRequest(
    string Name,
    string Provider,
    string FeedUrl,
    string? MediaType,
    string? LibraryId,
    string? QualityProfileId,
    bool SearchOnAdd,
    bool IsEnabled);
