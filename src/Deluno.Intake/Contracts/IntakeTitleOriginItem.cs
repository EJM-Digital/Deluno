namespace Deluno.Intake.Contracts;

/// <summary>
/// Records why a title came into Deluno from an import list. The source name
/// and provider are copied at discovery time so this remains useful even when
/// the list is later renamed or removed.
/// </summary>
public sealed record IntakeTitleOriginItem(
    string Id,
    string SourceId,
    string SourceName,
    string Provider,
    string MediaType,
    string EntityId,
    string EntryKey,
    string Title,
    int? Year,
    string? ImdbId,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);

public sealed record CreateIntakeTitleOriginRequest(
    string SourceId,
    string SourceName,
    string Provider,
    string MediaType,
    string EntityId,
    string EntryKey,
    string Title,
    int? Year,
    string? ImdbId);
