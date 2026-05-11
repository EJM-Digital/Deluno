namespace Deluno.Jobs.Contracts;

public sealed record DispatchAlert(
    string Id,
    string DispatchId,
    string Title,
    string Summary,
    string AlertKind,
    string Severity,
    IReadOnlyDictionary<string, string>? Metadata,
    DateTimeOffset DetectedUtc,
    bool Acknowledged,
    DateTimeOffset? AcknowledgedUtc);
