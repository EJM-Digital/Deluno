namespace Deluno.Libraries.Contracts;

public sealed record UpdateLibraryWorkflowRequest(
    string? ImportWorkflow,
    string? ProcessorName,
    string? ProcessorOutputPath,
    int? ProcessorTimeoutMinutes,
    string? ProcessorFailureMode,
    string? CleanupMode = null,
    bool? RemoveEmptySourceFolders = null);
