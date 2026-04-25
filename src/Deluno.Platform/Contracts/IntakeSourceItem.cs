namespace Deluno.Platform.Contracts;

public sealed record IntakeSourceItem(
    string Id,
    string Name,
    string Provider,
    string FeedUrl,
    string MediaType,
    string? LibraryId,
    string? LibraryName,
    string? QualityProfileId,
    string? QualityProfileName,
    bool SearchOnAdd,
    bool IsEnabled,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
