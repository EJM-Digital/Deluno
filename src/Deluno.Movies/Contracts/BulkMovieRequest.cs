namespace Deluno.Movies.Contracts;

/// <summary>Request to perform bulk operations on multiple movies</summary>
public sealed record BulkMovieRequest(
    /// <summary>Movie IDs to operate on</summary>
    IReadOnlyList<string> MovieIds,

    /// <summary>Operation type: remove, search, grab, quality, monitoring</summary>
    string Operation,

    /// <summary>Quality profile ID when operation is 'quality'</summary>
    string? QualityProfileId = null,

    /// <summary>Monitored state when operation is 'monitoring'</summary>
    bool? Monitored = null,

    /// <summary>Download client ID when operation is 'grab'</summary>
    string? DownloadClientId = null,

    /// <summary>Force override reason when operation is 'grab'</summary>
    string? ForceOverrideReason = null);

/// <summary>Response from bulk movie operation</summary>
public sealed record BulkMovieResponse(
    /// <summary>Total movies processed</summary>
    int TotalProcessed,

    /// <summary>Count of successful operations</summary>
    int SuccessCount,

    /// <summary>Count of failed operations</summary>
    int FailureCount,

    /// <summary>Operation type that was performed</summary>
    string Operation,

    /// <summary>Per-movie results with success/failure details</summary>
    IReadOnlyList<BulkMovieItemResult> Results);

/// <summary>Result for a single item in bulk operation</summary>
public sealed record BulkMovieItemResult(
    /// <summary>Movie ID</summary>
    string MovieId,

    /// <summary>Movie title</summary>
    string MovieTitle,

    /// <summary>Whether this item succeeded</summary>
    bool Succeeded,

    /// <summary>Error message if failed</summary>
    string? ErrorMessage = null,

    /// <summary>Additional metadata about the operation result</summary>
    Dictionary<string, string?>? Metadata = null);
