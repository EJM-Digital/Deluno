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

    /// <summary>
    /// Whether a reported output is recognisably about one particular download.
    ///
    /// <para>Being inside the library's output folder says only that the
    /// processor is writing where it was told to. It does not say the file is
    /// the one this hand-off was waiting for, and a completed event names the
    /// hand-off and the file as two separate claims — so without this, a
    /// processor could report one hand-off and hand over another release's
    /// file, and Deluno would import it under the wrong title (#573).</para>
    ///
    /// <para>A processor says which download a file came from by keeping its
    /// name: the folder it writes into, or the file itself. That is what the
    /// output-folder watcher already correlates on, and this is the same rule
    /// so both ways in agree.</para>
    /// </summary>
    public static bool IsOutputAbout(string? outputPath, string? configuredOutputPath, string? sourcePath)
    {
        if (!IsOutputOwnedByLibrary(outputPath, configuredOutputPath) || string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        try
        {
            var sourceLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath.Trim()));
            if (string.IsNullOrWhiteSpace(sourceLeaf) || sourceLeaf is "." or "..")
            {
                return false;
            }

            var names = new[] { sourceLeaf, Path.GetFileNameWithoutExtension(sourceLeaf) }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();

            var root = Path.GetFullPath(configuredOutputPath!.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = Path.GetFullPath(outputPath!.Trim())[(root.Length + 1)..];

            // Every folder it sits in below the output root, and its own name
            // with and without the extension the processor gave it.
            var segments = relative
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(segment => new[] { segment, Path.GetFileNameWithoutExtension(segment) });

            return segments.Any(segment => names.Contains(segment, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
