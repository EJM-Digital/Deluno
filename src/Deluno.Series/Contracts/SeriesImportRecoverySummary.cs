namespace Deluno.Series.Contracts;

public sealed record SeriesImportRecoverySummary(
    int OpenCount,
    int QualityCount,
    int UnmatchedCount,
    int CorruptCount,
    int DownloadFailedCount,
    int ImportFailedCount,
    IReadOnlyList<SeriesImportRecoveryCase> RecentCases);
