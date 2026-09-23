namespace Deluno.Contracts;

/// <summary>How a library takes a finished download in.</summary>
public static class ImportWorkflows
{
    /// <summary>
    /// A processor cleans the download first, and Deluno imports what it hands
    /// back.
    /// </summary>
    public const string RefineBeforeImport = "refine-before-import";

    /// <summary>Whether a library hands its downloads to a processor before importing them.</summary>
    public static bool HandsToProcessor(string? importWorkflow)
        => string.Equals(importWorkflow, RefineBeforeImport, StringComparison.OrdinalIgnoreCase);
}
