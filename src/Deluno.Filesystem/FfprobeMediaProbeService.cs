using System.Diagnostics;
using Deluno.Infrastructure.Processes;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Filesystem;

public sealed class FfprobeMediaProbeService : IMediaProbeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>ffprobe reads a file's headers in seconds; this is for a share that stalls, not for a slow disk.</summary>
    private static readonly TimeSpan ProbeLimit = TimeSpan.FromMinutes(5);

    public async Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Unavailable("Source file does not exist.");
        }

        var executable = FfmpegTools.Ffprobe();
        if (executable is null)
        {
            return Unavailable(MissingFfprobe);
        }

        // A file Deluno cannot even open (a share that dropped, a lock, a permission problem) is not a
        // bad release: say so here, before ffprobe turns the same OS error into "failed" (#1072).
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var first = new byte[1];
            _ = await stream.ReadAsync(first, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Unreadable(exception.Message);
        }

        var startInfo = new ProcessStartInfo { FileName = executable };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add(path);

        try
        {
            // Both streams are read as they arrive (a full pipe would hold ffprobe up), and ffprobe is stopped if it does
            // not finish: a share that stalls mid-read can hold it forever (#1258).
            var result = await ChildProcess.RunAsync(startInfo, ProbeLimit, cancellationToken);
            if (!result.Started)
            {
                // A missing binary is an environment state, not a bad file. The
                // contract is "validate with ffprobe when available" — an install
                // without it must import unvalidated, not fail every import.
                return Unavailable(MissingFfprobe);
            }

            if (result.TimedOut)
            {
                return Unreadable("ffprobe did not finish reading this file in time, so the file could not be reached.");
            }

            var output = result.Output;
            var error = result.Error;

            if (result.ExitCode != 0)
            {
                if (LooksLikeTheFileCouldNotBeReached(error))
                {
                    return Unreadable(error.Trim());
                }

                return Failed(string.IsNullOrWhiteSpace(error) ? $"ffprobe exited with code {result.ExitCode}." : error.Trim());
            }

            var document = JsonSerializer.Deserialize<FfprobeDocument>(output, JsonOptions);
            if (document is null)
            {
                return Failed("ffprobe returned an empty or invalid JSON payload.");
            }

            var streams = document.Streams ?? [];
            var video = streams
                .Where(stream => string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase))
                .Select(stream => new MediaVideoStreamInfo(
                    stream.Index,
                    stream.CodecName,
                    stream.Profile,
                    stream.Width,
                    stream.Height,
                    stream.PixelFormat,
                    ParseFrameRate(stream.AverageFrameRate ?? stream.RealFrameRate),
                    ParseLong(stream.BitRate),
                    LanguageOf(stream),
                    stream.ColorTransfer,
                    SideDataTypesOf(stream)))
                .ToArray();
            var audio = streams
                .Where(stream => string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase))
                .Select(stream => new MediaAudioStreamInfo(
                    stream.Index,
                    stream.CodecName,
                    stream.Profile,
                    stream.Channels,
                    stream.ChannelLayout,
                    ParseInt(stream.SampleRate),
                    ParseLong(stream.BitRate),
                    LanguageOf(stream)))
                .ToArray();
            var subtitles = streams
                .Where(stream => string.Equals(stream.CodecType, "subtitle", StringComparison.OrdinalIgnoreCase))
                .Select(stream => new MediaSubtitleStreamInfo(
                    stream.Index,
                    stream.CodecName,
                    LanguageOf(stream),
                    IsDisposed(stream, "forced"),
                    IsDisposed(stream, "hearing_impaired"),
                    TitleOf(stream)))
                .ToArray();

            return new MediaProbeInfo(
                "succeeded",
                "ffprobe",
                null,
                ParseDouble(document.Format?.Duration),
                document.Format?.FormatName,
                ParseLong(document.Format?.BitRate),
                video,
                audio,
                subtitles,
                TitleOf(document.Format?.Tags));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Deluno could not get at the file — a share dropped mid-read, a
            // lock, a disk error. That says nothing about the release, and
            // flattening it into the same answer as "ffprobe read this and
            // rejected it" meant any policy for the pair was wrong about one of
            // them half the time. DESIGN-007 decision 3.
            return Unreadable(exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            // ffprobe ran and could not make sense of the file. Corrupt,
            // truncated, or an archive wearing a video extension — and any copy
            // of that release is the same bytes.
            return Failed(exception.Message);
        }
    }

    /// <summary>
    /// Said once, because it is said from two places and they used to say
    /// different things — one blamed the PATH and the other told you to set an
    /// environment variable.
    ///
    /// <para>It no longer says "install FFmpeg". Deluno ships FFmpeg, so an
    /// absent ffprobe is a broken install rather than a missing prerequisite,
    /// and telling somebody to go and install what they already paid 128 MB for
    /// would send them off to fix the wrong thing.</para>
    /// </summary>
    private const string MissingFfprobe =
        "ffprobe is missing from this install, so streams could not be read. It ships in tools\\ffmpeg beside Deluno — reinstall, or point DELUNO_FFMPEG_DIR at a copy.";

    /// <summary>
    /// ffprobe's own words for "the operating system would not give me the file" (a dropped share, a
    /// lock, a permission problem), as opposed to "I read it and it is broken" (which stays
    /// <c>failed</c>: "Invalid data found", "moov atom not found" and the like).
    /// </summary>
    public static bool LooksLikeTheFileCouldNotBeReached(string? ffprobeError)
        => !string.IsNullOrWhiteSpace(ffprobeError) && UnreachableMarkers.Any(marker => ffprobeError.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] UnreachableMarkers =
    [
        "Input/output error", "Permission denied", "Access is denied", "being used by another process",
        "Stale file handle", "Connection reset", "Connection timed out", "Host is down", "Network is unreachable",
        "network path", "network name is no longer available", "Resource temporarily unavailable",
        "Device or resource busy", "Transport endpoint is not connected", "No such file or directory"
    ];

    private static MediaProbeInfo Unavailable(string message)
        => new("unavailable", "ffprobe", message, null, null, null, [], [], []);

    // The message is plain words; ffprobe's own text is kept as the detail only.
    public static MediaProbeInfo Failed(string message)
        => new("failed", "ffprobe", ProbeFailureWords.FailedMessage, null, null, null, [], [], [], null, message);

    public static MediaProbeInfo Unreadable(string message)
        => new("unreadable", "ffprobe", ProbeFailureWords.UnreadableMessage, null, null, null, [], [], [], null, message);


    private static bool IsDisposed(FfprobeStream stream, string flag)
        => stream.Disposition is not null && stream.Disposition.TryGetValue(flag, out var value) && value == 1;

    private static string? TitleOf(FfprobeStream stream)
        => TitleOf(stream.Tags);

    private static string? TitleOf(IReadOnlyDictionary<string, string>? tags)
        => tags is not null && tags.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title)
            ? title
            : null;

    private static string? LanguageOf(FfprobeStream stream)
        => stream.Tags is not null && stream.Tags.TryGetValue("language", out var language)
            ? language
            : null;

    private static IReadOnlyList<string>? SideDataTypesOf(FfprobeStream stream)
        => stream.SideDataList is { Count: > 0 }
            ? stream.SideDataList
                .Select(item => item.Type)
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Select(type => type!)
                .ToArray()
            : null;

    private static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static long? ParseLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static double? ParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static double? ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0/0")
        {
            return null;
        }

        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator > 0)
        {
            return Math.Round(numerator / denominator, 3);
        }

        return ParseDouble(value);
    }

    private sealed record FfprobeDocument(
        [property: JsonPropertyName("streams")] IReadOnlyList<FfprobeStream>? Streams,
        [property: JsonPropertyName("format")] FfprobeFormat? Format);

    private sealed record FfprobeFormat(
        [property: JsonPropertyName("format_name")] string? FormatName,
        [property: JsonPropertyName("duration")] string? Duration,
        [property: JsonPropertyName("bit_rate")] string? BitRate,
        [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string>? Tags);

    private sealed record FfprobeStream(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("codec_name")] string? CodecName,
        [property: JsonPropertyName("codec_type")] string? CodecType,
        [property: JsonPropertyName("profile")] string? Profile,
        [property: JsonPropertyName("width")] int? Width,
        [property: JsonPropertyName("height")] int? Height,
        [property: JsonPropertyName("pix_fmt")] string? PixelFormat,
        [property: JsonPropertyName("avg_frame_rate")] string? AverageFrameRate,
        [property: JsonPropertyName("r_frame_rate")] string? RealFrameRate,
        [property: JsonPropertyName("bit_rate")] string? BitRate,
        [property: JsonPropertyName("channels")] int? Channels,
        [property: JsonPropertyName("channel_layout")] string? ChannelLayout,
        [property: JsonPropertyName("sample_rate")] string? SampleRate,
        [property: JsonPropertyName("color_transfer")] string? ColorTransfer,
        [property: JsonPropertyName("side_data_list")] IReadOnlyList<FfprobeSideData>? SideDataList,
        [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string>? Tags,
        [property: JsonPropertyName("disposition")] IReadOnlyDictionary<string, int>? Disposition);

    private sealed record FfprobeSideData(
        [property: JsonPropertyName("side_data_type")] string? Type);
}
