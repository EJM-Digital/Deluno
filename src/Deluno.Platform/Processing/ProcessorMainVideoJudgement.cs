using Deluno.Contracts;
namespace Deluno.Platform.Processing;

/// <summary>
/// A hand-off is judged by its main video (golden path, rc.30.8). A release often holds small extras (a 12 MB
/// featurette, a trailer) that a processor skips or refuses because they fall under its own size floor. When
/// the processor reports the hand-off as partly failed but it did deliver every main video, the hand-off
/// succeeded: the extras it left out are recorded in its details and named in one plain line, never a failure.
/// </summary>
public static class ProcessorMainVideoJudgement
{
    private static readonly IReadOnlySet<string> VideoExtensions = VideoFileExtensions.Extensions;

    /// <summary>A video at least this share of the largest one counts as a main video; smaller ones are extras.</summary>
    private const double MainVideoShare = 0.25;

    public sealed record Result(bool MainVideoDelivered, IReadOnlyList<string> Extras, string? Note);

    /// <param name="sourcePath">What the download client holds for the release (a file, or the release's folder).</param>
    /// <param name="outputFiles">The files the processor says it finished.</param>
    public static Result Judge(string? sourcePath, IReadOnlyList<string>? outputFiles, string processorName)
    {
        var delivered = (outputFiles ?? [])
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => file.Trim())
            .Where(file => IsVideo(file) && File.Exists(file))
            .Select(file => new FileInfo(file))
            .ToArray();
        var source = VideosOf(sourcePath);
        if (delivered.Length == 0 || source.Count == 0)
        {
            return new Result(false, [], null);
        }

        var largest = source.Max(video => video.Length);
        var main = source.Where(video => video.Length >= largest * MainVideoShare).ToArray();
        var extras = source.Where(video => video.Length < largest * MainVideoShare).ToArray();

        // Every main video needs its cleaned copy. A processor keeps the name it was given; if it renamed the
        // file, the number of cleaned videos has to cover the main ones instead.
        var stemsDelivered = delivered.Select(file => Path.GetFileNameWithoutExtension(file.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingByName = main.Count(video => !stemsDelivered.Contains(Path.GetFileNameWithoutExtension(video.Name)));
        var mainDelivered = missingByName == 0 || delivered.Length >= main.Length;
        if (!mainDelivered)
        {
            return new Result(false, [], null);
        }

        var extraNames = extras
            .Where(extra => !stemsDelivered.Contains(Path.GetFileNameWithoutExtension(extra.Name)))
            .Select(extra => extra.Name)
            .ToArray();
        var note = extraNames.Length == 0
            ? null
            : extraNames.Length == 1
                ? $"{processorName} left one small extra file out ({extraNames[0]}). The main video was delivered and imported."
                : $"{processorName} left {extraNames.Length} small extra files out ({string.Join(", ", extraNames.Take(3))}{(extraNames.Length > 3 ? ", and more" : string.Empty)}). The main video was delivered and imported.";
        return new Result(true, extraNames, note);
    }

    /// <summary>The video files a finished hand-off left at its output path (a file, or the folder holding them).</summary>
    public static IReadOnlyList<string> OutputsAt(string? outputPath)
        => VideosOf(outputPath).Select(file => file.FullName).ToArray();

    private static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    private static List<FileInfo> VideosOf(string? sourcePath)
    {
        var videos = new List<FileInfo>();
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return videos;
        }

        try
        {
            var path = sourcePath.Trim();
            if (File.Exists(path))
            {
                if (IsVideo(path)) videos.Add(new FileInfo(path));
            }
            else if (Directory.Exists(path))
            {
                videos.AddRange(new DirectoryInfo(path)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Where(file => IsVideo(file.Name)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            videos.Clear();
        }

        return videos;
    }
}
