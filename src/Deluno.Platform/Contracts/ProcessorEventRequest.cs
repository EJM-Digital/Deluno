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
    string? ProcessorName,
    string? HandoffId = null,
    string? Disposition = null,
    bool? SourceRemoved = null,
    string? FailureClass = null,
    // Every file a folder hand-off produced (#619), with OutputPath the folder
    // holding them. A processor sends one completion per hand-off.
    IReadOnlyList<string>? OutputFiles = null);
