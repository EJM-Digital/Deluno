namespace Deluno.Intake.Contracts;

/// <summary>
/// A durable decision not to add one entry from an import list. It only affects
/// that list and never changes a title already in the library.
/// </summary>
public sealed record IntakeListExclusionItem(
    string Id,
    string SourceId,
    string EntryKey,
    string Title,
    int? Year,
    string? ImdbId,
    string Reason,
    DateTimeOffset? ExpiresUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record CreateIntakeListExclusionRequest(
    string Title,
    int? Year,
    string? ImdbId,
    int? DurationDays,
    string? Reason = null);
