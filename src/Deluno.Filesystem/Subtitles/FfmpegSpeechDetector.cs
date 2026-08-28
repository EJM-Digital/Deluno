using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem.Subtitles;

/// <summary>
/// Where the talking is in a video's audio.
/// </summary>
public interface ISpeechDetector
{
    /// <summary>
    /// Reads one audio stream and returns a mask of where it is not silent, or
    /// <c>null</c> when the audio cannot be read at all.
    /// </summary>
    Task<SpeechMask?> DetectAsync(
        string videoPath,
        int audioStreamIndex,
        TimeSpan duration,
        CancellationToken cancellationToken);
}

/// <summary>
/// Speech detection, done by FFmpeg rather than in C#.
///
/// <para><b>The decision worth recording.</b> <c>ffsubsync</c> decodes the whole
/// audio track to 16 kHz PCM, pipes it into a voice-activity detector, and gets
/// back a speech mask. Reproducing that here would have meant either shipping a
/// VAD implementation — WebRTC's is a few hundred lines of signal processing
/// that would have to be right, and would be tested against nothing — or pulling
/// hundreds of megabytes of audio through a pipe to run a simpler detector over
/// it badly.</para>
///
/// <para>FFmpeg already has the answer. <c>silencedetect</c> reports every
/// stretch below a threshold, as two numbers per stretch, on stderr — so the
/// audio never leaves FFmpeg's own process, nothing is decoded twice, and what
/// crosses the boundary is a few kilobytes of text for a feature film. The
/// speech mask is the complement of what it reports.</para>
///
/// <para><b>The band-pass is not decoration.</b> Silence detection over a full
/// mix calls an explosion speech. Cutting to roughly 200–3,000 Hz first leaves
/// the range human speech actually occupies, so music and effects stop voting.
/// This is the one place Deluno's detection is meaningfully better than
/// <c>ffsubsync</c>'s default, which runs its detector over the whole
/// mix.</para>
///
/// <para><b>It reads one stream, chosen by the caller.</b> Which audio track a
/// subtitle should be aligned against is a question about languages and
/// dispositions, and it belongs with the code that knows what the title is —
/// not here.</para>
/// </summary>
public sealed class FfmpegSpeechDetector(ILogger<FfmpegSpeechDetector> logger) : ISpeechDetector
{
    /// <summary>
    /// The level below which audio counts as silence, and how long it has to
    /// stay there.
    ///
    /// <para>−30 dB is quiet enough that room tone and breath under dialogue stay
    /// on the speech side, and loud enough that a scene of nothing but a distant
    /// hum does not. A fifth of a second is about the shortest real pause between
    /// spoken phrases; below that the mask fills with gaps that carry no
    /// information and only slow the correlation down.</para>
    /// </summary>
    private const string Filter = "highpass=f=200,lowpass=f=3000,silencedetect=noise=-30dB:d=0.2";

    public async Task<SpeechMask?> DetectAsync(
        string videoPath,
        int audioStreamIndex,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var executable = FfmpegTools.Ffmpeg();
        if (executable is null)
        {
            logger.LogWarning(
                "ffmpeg is missing from this install, so {Path} could not be listened to and its subtitles cannot be timed.",
                videoPath);
            return null;
        }

        if (duration <= TimeSpan.Zero)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);

        // The absolute stream index from ffprobe, which is why this is `-map
        // 0:<index>` rather than `0:a:<n>`: the two number streams differently
        // and mixing them up silently aligns against the wrong track.
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add($"0:{audioStreamIndex.ToString(CultureInfo.InvariantCulture)}");
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-dn");

        // Down to one channel at 16 kHz before filtering. A film's audio is
        // commonly six channels at 48 kHz, and none of the other five nor the
        // top two-thirds of the spectrum can change where the talking is.
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("16000");
        startInfo.ArgumentList.Add("-af");
        startInfo.ArgumentList.Add(Filter);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("-");

        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            logger.LogWarning(exception, "ffmpeg could not be started, so {Path} was not listened to.", videoPath);
            return null;
        }

        using var process = started;
        if (process is null)
        {
            return null;
        }

        var mask = new SpeechMask(SpeechMask.ToFrames(duration));

        // Speech is the complement of silence, so the mask starts on and the
        // reported silences are cut out of it. Starting from the reported
        // silences and inverting at the end would need the whole list held in
        // memory first; this way the stream is read once and thrown away.
        mask.Mark(TimeSpan.Zero, duration);

        // stdout is discarded but must still be drained: a full pipe buffer
        // deadlocks the child, and `-f null -` writes nothing only as long as
        // nothing changes about the arguments above.
        var drain = process.StandardOutput.ReadToEndAsync(cancellationToken);

        var silences = 0;
        TimeSpan? openedAt = null;

        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                if (TryRead(line, "silence_start:", out var silenceStart))
                {
                    openedAt = silenceStart;
                }
                else if (TryRead(line, "silence_end:", out var silenceEnd) && openedAt is { } from)
                {
                    mask.Unmark(from, silenceEnd);
                    openedAt = null;
                    silences++;
                }
            }

            await process.WaitForExitAsync(cancellationToken);
            await drain;
        }
        catch (OperationCanceledException)
        {
            // A cancelled sync must not leave an ffmpeg running against a file
            // the rest of Deluno may be about to move.
            TryKill(process);
            throw;
        }

        // A silence still open at the end of the file has no `silence_end` line,
        // because FFmpeg reports the pair only when the stretch closes.
        if (openedAt is { } trailing)
        {
            mask.Unmark(trailing, duration);
            silences++;
        }

        if (process.ExitCode != 0)
        {
            logger.LogWarning("ffmpeg exited with {Code} while listening to {Path}.", process.ExitCode, videoPath);
            return null;
        }

        if (silences == 0)
        {
            // No silence anywhere means the mask is solid, and a solid mask
            // correlates identically at every offset — it would hand back
            // whichever shift it tried first and call it an answer.
            logger.LogInformation("No silence was found in {Path}, so there is nothing to align its subtitles against.", videoPath);
            return null;
        }

        return mask;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // It exited between the check and the kill, which is the outcome
            // wanted anyway.
        }
    }

    /// <summary>
    /// <c>[silencedetect @ 0000...] silence_start: 12.345</c>.
    /// </summary>
    private static bool TryRead(string line, string marker, out TimeSpan value)
    {
        value = default;

        var at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        var rest = line[(at + marker.Length)..].TrimStart();

        // `silence_end` carries a `| silence_duration:` after it on the same
        // line, so the number ends at the first space.
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0)
        {
            rest = rest[..space];
        }

        if (!double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        value = TimeSpan.FromSeconds(Math.Max(0d, seconds));
        return true;
    }
}
