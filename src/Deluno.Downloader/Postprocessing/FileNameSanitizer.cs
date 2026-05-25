namespace Deluno.Downloader.Postprocessing;

/// <summary>
/// Rewrites filenames to be safe across the OS targets we ship for
/// (Windows + Linux + macOS). Replaces OS-invalid chars with underscore,
/// trims trailing dots/spaces (Windows-hostile), and disambiguates files
/// that would only differ by case on case-insensitive filesystems.
///
/// This runs AFTER flatten / sample-filter so we only sanitize files
/// that are going to be imported.
/// </summary>
public sealed class FileNameSanitizer : IPostProcessor
{
    // Conservative superset of WindowsInvalidFileNameChars + a few extras
    // that confuse case-insensitive filesystems or shell tools.
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public Task<IReadOnlyList<string>> ProcessAsync(
        string workingDir,
        IReadOnlyList<string> files,
        CancellationToken ct)
    {
        var renamed = new List<string>(files.Count);
        var seenNamesCaseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var src in files)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.GetDirectoryName(src)!;
            var current = Path.GetFileName(src);
            var sanitized = Sanitize(current);

            // Disambiguate case collisions in the same directory.
            var withDirKey = Path.Combine(dir, sanitized).ToLowerInvariant();
            if (seenNamesCaseInsensitive.Contains(withDirKey))
            {
                var stem = Path.GetFileNameWithoutExtension(sanitized);
                var ext = Path.GetExtension(sanitized);
                for (var n = 2; n < 1000; n++)
                {
                    var attempt = $"{stem} ({n}){ext}";
                    var attemptKey = Path.Combine(dir, attempt).ToLowerInvariant();
                    if (!seenNamesCaseInsensitive.Contains(attemptKey))
                    {
                        sanitized = attempt;
                        break;
                    }
                }
            }
            seenNamesCaseInsensitive.Add(Path.Combine(dir, sanitized).ToLowerInvariant());

            if (sanitized == current)
            {
                renamed.Add(src);
                continue;
            }

            var dst = Path.Combine(dir, sanitized);
            try
            {
                File.Move(src, dst);
                renamed.Add(dst);
            }
            catch
            {
                renamed.Add(src); // best-effort
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(renamed);
    }

    public static string Sanitize(string fileName)
    {
        var chars = fileName.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Invalid, chars[i]) >= 0 || chars[i] < 0x20)
                chars[i] = '_';
        }
        var result = new string(chars).TrimEnd(' ', '.');
        return string.IsNullOrWhiteSpace(result) ? "_" : result;
    }
}
