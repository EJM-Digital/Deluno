namespace Deluno.Platform.Contracts;

public sealed record ProcessorEventRequest(
    string? LibraryId,
    string? MediaType,
    string? EntityType,
    string? EntityId,
    string? SourcePath,
    string? OutputPath,
    string? Status,
    string? Message,
    string? ProcessorName);
