namespace Deluno.Platform.Contracts;

public sealed record DestinationResolutionRequest(
    string? MediaType,
    string? Title,
    int? Year,
    IReadOnlyList<string>? Genres,
    IReadOnlyList<string>? Tags,
    string? Studio,
    string? OriginalLanguage);

public sealed record DestinationResolutionResult(
    string MediaType,
    string Title,
    int? Year,
    string RootPath,
    string FolderName,
    string FullPath,
    string? MatchedRuleId,
    string? MatchedRuleName,
    string Reason);
