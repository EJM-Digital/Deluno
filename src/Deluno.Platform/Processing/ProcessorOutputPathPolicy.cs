namespace Deluno.Platform.Processing;

/// <summary>
/// Defines the filesystem ownership boundary for pre-import processor callbacks.
/// A completed callback may only ask Deluno to import a file below the library's
/// explicitly configured processor-output directory.
/// </summary>
public static class ProcessorOutputPathPolicy
{
    public static bool IsOutputOwnedByLibrary(string? outputPath, string? configuredOutputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(configuredOutputPath))
        {
            return false;
        }

        try
        {
            var output = Path.GetFullPath(outputPath.Trim());
            var root = Path.GetFullPath(configuredOutputPath.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootWithSeparator = root + Path.DirectorySeparatorChar;

            return output.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
