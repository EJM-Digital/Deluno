using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Filesystem;

public sealed class FfprobeMediaProbeService : IMediaProbeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Unavailable("Source file does not exist.");
        }

        var executable = ResolveExecutable();
        if (executable is null)
        {
            return Unavailable("ffprobe was not found on PATH. Install FFmpeg or configure the runtime image with ffprobe to enable stream validation.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add(path);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Unavailable("ffprobe could not be started.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                return Failed(string.IsNullOrWhiteSpace(error) ? $"ffprobe exited with code {process.ExitCode}." : error.Trim());
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
                    LanguageOf(stream)))
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
                    LanguageOf(stream)))
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
                subtitles);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            return Failed(exception.Message);
        }
    }

    private static MediaProbeInfo Unavailable(string message)
        => new("unavailable", "ffprobe", message, null, null, null, [], [], []);

    private static MediaProbeInfo Failed(string message)
        => new("failed", "ffprobe", message, null, null, null, [], [], []);

    private static string? ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("DELUNO_FFPROBE_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        return "ffprobe";
    }

    private static string? LanguageOf(FfprobeStream stream)
        => stream.Tags is not null && stream.Tags.TryGetValue("language", out var language)
            ? language
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
        [property: JsonPropertyName("bit_rate")] string? BitRate);

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
        [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string>? Tags);
}
