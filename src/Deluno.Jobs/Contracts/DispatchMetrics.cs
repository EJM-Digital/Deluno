namespace Deluno.Jobs.Contracts;

public sealed record DispatchMetrics(
    long TotalDispatchesRecorded,
    long SuccessfulGrabs,
    long FailedGrabs,
    long DetectedDownloads,
    long SuccessfulImports,
    long FailedImports,
    int ActiveDispatchesCount,
    int RecoveryCasesOpenCount,
    TimeSpan AverageGrabToDetection,
    TimeSpan AverageDetectionToImport,
    IReadOnlyDictionary<string, int> GrabFailuresByClient,
    IReadOnlyDictionary<string, int> ImportFailuresByKind,
    DateTimeOffset ComputedUtc);
