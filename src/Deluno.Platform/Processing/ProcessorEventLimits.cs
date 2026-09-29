namespace Deluno.Platform.Processing;

/// <summary>
/// How much one processor callback may say (#1023). Written down in
/// docs/external-integration-api.md as part of the contract a processor such as
/// Weir is built against, and enforced by
/// <c>POST /api/integrations/processors/events</c> so the two cannot drift.
///
/// <para>The count is sized for the largest thing a hand-off can be: a whole
/// show's worth of season packs is a few hundred files, so a thousand leaves
/// room and still stops a runaway or hostile report before Deluno checks each
/// named file exists on disk. The path length is the longest path Linux
/// allows (<c>PATH_MAX</c>, 4096); Windows paths a processor can produce are
/// shorter than that in practice.</para>
/// </summary>
public static class ProcessorEventLimits
{
    /// <summary>The most files one report may list in <c>outputFiles</c>.</summary>
    public const int MaxOutputFiles = 1000;

    /// <summary>The longest any single path in a report may be, in characters.</summary>
    public const int MaxPathLength = 4096;

    /// <summary>What is wrong with the files a report lists, keyed by the field name a validation problem uses; empty when nothing is.</summary>
    public static IReadOnlyDictionary<string, string[]> Check(IReadOnlyList<string>? outputFiles)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (outputFiles is null)
        {
            return errors;
        }

        if (outputFiles.Count > MaxOutputFiles)
        {
            errors["outputFiles"] =
            [
                $"A report may list at most {MaxOutputFiles} output files and this one lists {outputFiles.Count}. Name the folder in outputPath and list only the files that belong in the library. Deluno did not queue an import."
            ];
        }
        else if (outputFiles.Any(file => file is { Length: > MaxPathLength }))
        {
            errors["outputFiles"] =
            [
                $"An output file path may be at most {MaxPathLength} characters. Deluno did not queue an import."
            ];
        }

        return errors;
    }
}
