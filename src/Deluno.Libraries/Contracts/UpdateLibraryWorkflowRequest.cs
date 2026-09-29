namespace Deluno.Libraries.Contracts;

public sealed record UpdateLibraryWorkflowRequest(
    string? ImportWorkflow,
    string? ProcessorName,
    string? ProcessorOutputPath,
    int? ProcessorTimeoutMinutes,
    string? ProcessorFailureMode,
    string? CleanupMode = null,
    bool? RemoveEmptySourceFolders = null,
    /// <summary>The processor connection to hand downloads to, by id (#1077). Wins over <see cref="ProcessorName"/>, which older callers still send.</summary>
    string? ProcessorConnectionId = null);
