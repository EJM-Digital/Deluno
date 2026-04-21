namespace Deluno.Movies.Contracts;

public sealed record MovieImportRecoverySummary(
    int OpenCount,
    int QualityCount,
    int UnmatchedCount,
    int CorruptCount,
    int DownloadFailedCount,
    int ImportFailedCount,
    IReadOnlyList<MovieImportRecoveryCase> RecentCases);
