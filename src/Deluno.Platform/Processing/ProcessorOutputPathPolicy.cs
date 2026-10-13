namespace Deluno.Platform.Processing;

/// <summary>
/// Defines the filesystem ownership boundary for pre-import processor callbacks.
/// A completed callback may only ask Deluno to import a file below the library's
/// explicitly configured processor-output directory.
/// </summary>
public static class ProcessorOutputPathPolicy
{
    /// <summary>
    /// The output path of a report that named its files but no folder (Weir's partly failed report, golden path
    /// rc.30.9): the file itself for one file, otherwise the deepest folder that holds all of them.
    /// </summary>
    public static string? OutputPathFromFiles(IReadOnlyList<string>? outputFiles)
    {
        var files = (outputFiles ?? []).Where(file => !string.IsNullOrWhiteSpace(file)).Select(file => file.Trim()).ToArray();
        if (files.Length == 0)
        {
            return null;
        }

        if (files.Length == 1)
        {
            return files[0];
        }

        try
        {
            var parts = files
                .Select(file => (Path.GetDirectoryName(Path.GetFullPath(file)) ?? string.Empty)
                    .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.None))
                .ToArray();
            var common = new List<string>();
            for (var i = 0; i < parts.Min(p => p.Length); i++)
            {
                if (parts.Any(p => !string.Equals(p[i], parts[0][i], StringComparison.OrdinalIgnoreCase)))
                {
                    break;
                }

                common.Add(parts[0][i]);
            }

            var folder = string.Join(Path.DirectorySeparatorChar, common);
            return string.IsNullOrWhiteSpace(folder) ? null : folder;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

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
            //
            // Split on both separators whatever this machine calls its own: the
            // path was written by a processor, which may not be running on the
            // same kind of machine as Deluno.
            var segments = relative
                .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(segment => new[] { segment, Path.GetFileNameWithoutExtension(segment) });

            // A name collision renames only the file, as "stem (n).ext" (#1262).
            return segments.Any(segment => names.Contains(segment, StringComparer.OrdinalIgnoreCase))
                || relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(segment => Path.GetFileNameWithoutExtension(segment))
                    .Any(segment => names.Any(name => ProcessorOutputLayout.IsCollisionRenamedStem(segment, Path.GetFileNameWithoutExtension(name))));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts", ".wmv", ".mpg", ".mpeg", ".iso"];

    /// <summary>
    /// Deletes a processor's second copy of a download Deluno has already
    /// imported (#1155): the output itself, and the release folder around it
    /// once no video is left in it. Only what is recognisably about this
    /// download (<see cref="IsOutputAbout"/>) and inside the library's own output
    /// folder is ever touched. Returns whether anything was deleted.
    /// </summary>
    public static bool DeleteSecondCopy(string? outputPath, string? configuredOutputPath, string? sourcePath)
    {
        if (!IsOutputAbout(outputPath, configuredOutputPath, sourcePath))
        {
            return false;
        }

        try
        {
            var output = Path.GetFullPath(outputPath!.Trim());
            var root = Path.GetFullPath(configuredOutputPath!.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            else if (File.Exists(output))
            {
                File.Delete(output);
            }
            else
            {
                return false;
            }

            var top = Path.Combine(root, output[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]);
            if (!string.Equals(top, output, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(top) &&
                !Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories)
                    .Any(file => VideoExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
            {
                Directory.Delete(top, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
