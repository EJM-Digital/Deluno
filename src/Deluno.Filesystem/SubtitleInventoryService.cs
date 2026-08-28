using Deluno.Contracts;

namespace Deluno.Filesystem;

/// <summary>
/// What subtitles a video file actually has, right now, from every place one
/// can be.
///
/// Deluno knows about most of its subtitles because it fetched them and wrote
/// them itself — that is recorded at the moment it happens and does not need
/// finding. This is the other half: learning about the subtitles Deluno did not
/// fetch. On the day somebody first asks a library for English, that is all of
/// them, and without this every poster in the library would claim to be missing
/// a subtitle that is sitting right beside the video.
///
/// Two places are read, because subtitles live in two places:
///
/// <list type="bullet">
/// <item><b>Beside the video</b> — <c>Big Buck Bunny (2008).en.srt</c>. The
/// common case by a distance: it is what a previous Bazarr left behind, what
/// most releases ship, and what a person drops in by hand.</item>
/// <item><b>Inside the container</b> — an MKV commonly carries a dozen tracks.
/// ffprobe has been able to report these since before Subber existed and
/// nothing has ever read them.</item>
/// </list>
///
/// This service is deliberately a reader. It writes nothing and decides
/// nothing; what is wanted, and what to do about a gap, belongs to the caller.
/// </summary>
public interface ISubtitleInventoryService
{
    /// <param name="probeContainer">
    /// Whether to read the tracks inside the video as well as the files beside
    /// it. False when the caller already knows them — the tracks in a container
    /// cannot change while the container does not, and a process per file is
    /// the whole cost of this service.
    /// </param>
    Task<SubtitleInventory> InspectAsync(
        string videoPath,
        bool probeContainer,
        CancellationToken cancellationToken);
}

public sealed record SubtitleInventory(
    string VideoPath,
    bool VideoExists,
    /// <summary>
    /// <c>succeeded</c>, <c>unavailable</c>, <c>failed</c>, or <c>skipped</c>
    /// when there was no file to probe.
    ///
    /// Carried rather than dropped because "no embedded tracks" and "nobody
    /// looked" are different facts, and an install without FFmpeg produces the
    /// second one for every file it owns.
    /// </summary>
    string ProbeStatus,
    string? ProbeMessage,
    IReadOnlyList<DetectedSubtitle> Subtitles);

public sealed record DetectedSubtitle(
    string Language,
    string Source,
    bool Forced,
    bool HearingImpaired,
    string? Path,
    int? StreamIndex,
    string? Codec);

public sealed class SubtitleInventoryService(IMediaProbeService mediaProbeService) : ISubtitleInventoryService
{
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".smi"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts"
    };

    /// <summary>
    /// The Plex and Bazarr convention for keeping subtitles out of the way.
    /// One level, by name, because walking a library root looking for loose
    /// <c>.srt</c> files is how a scan becomes something you notice.
    /// </summary>
    private static readonly string[] SubtitleSubfolders = ["Subs", "Subtitles"];

    private static readonly HashSet<string> ForcedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "forced", "forced_narrative", "forcednarrative", "foreign"
    };

    private static readonly HashSet<string> HearingImpairedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "hi", "sdh", "cc", "hearingimpaired", "hearing_impaired"
    };

    private static readonly char[] TokenSeparators = [' ', '_', '-'];

    public async Task<SubtitleInventory> InspectAsync(
        string videoPath,
        bool probeContainer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return new SubtitleInventory(videoPath ?? string.Empty, false, "skipped", "The video file does not exist.", []);
        }

        var found = new List<DetectedSubtitle>();
        found.AddRange(FindSidecars(videoPath));

        if (!probeContainer)
        {
            // `cached` rather than `skipped`: the difference matters to the scan
            // candidate query, which reads `unavailable` as "come back when
            // ffprobe arrives" and must not read this the same way. Nobody is
            // waiting on this probe because its answer is already recorded.
            return new SubtitleInventory(videoPath, true, "cached", null, Deduplicate(found));
        }

        var probe = await mediaProbeService.ProbeAsync(videoPath, cancellationToken);
        if (probe.Status == "succeeded")
        {
            foreach (var stream in probe.SubtitleStreams)
            {
                found.Add(new DetectedSubtitle(
                    Language: SubtitleLanguages.Normalize(stream.Language) ?? SubtitleLanguages.Unknown,
                    Source: SubtitleSources.Embedded,
                    Forced: stream.Forced || LooksForced(stream.Title),
                    HearingImpaired: stream.HearingImpaired || LooksHearingImpaired(stream.Title),
                    Path: null,
                    StreamIndex: stream.Index,
                    Codec: stream.Codec));
            }
        }

        return new SubtitleInventory(videoPath, true, probe.Status, probe.Message, Deduplicate(found));
    }

    /// <summary>
    /// Subtitle files beside the video, and in a <c>Subs</c> folder under it.
    ///
    /// A file is taken as this video's when its name starts with the video's —
    /// the naming Deluno itself uses and the one every tool writes. A folder
    /// holding exactly one video is the one exception: a lone
    /// <c>English.srt</c> next to a lone <c>.mkv</c> belongs to it, and
    /// insisting on the prefix there would call a subtitle missing while the
    /// reader is looking straight at it.
    /// </summary>
    private static IEnumerable<DetectedSubtitle> FindSidecars(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            yield break;
        }

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        string[] entries;
        try
        {
            entries = Directory.GetFiles(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        var soleVideo = entries.Count(entry => VideoExtensions.Contains(Path.GetExtension(entry))) == 1;

        foreach (var candidate in EnumerateCandidates(directory, entries))
        {
            var fileName = Path.GetFileName(candidate);
            var extension = Path.GetExtension(candidate);
            if (!SubtitleExtensions.Contains(extension))
            {
                continue;
            }

            string tagSection;
            if (fileName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
            {
                tagSection = fileName[(baseName.Length + 1)..];
            }
            else if (soleVideo)
            {
                tagSection = fileName;
            }
            else
            {
                continue;
            }

            var tags = ReadTags(tagSection);
            yield return new DetectedSubtitle(
                Language: tags.Language,
                Source: SubtitleSources.External,
                Forced: tags.Forced,
                HearingImpaired: tags.HearingImpaired,
                Path: candidate,
                StreamIndex: null,
                Codec: extension.TrimStart('.').ToLowerInvariant());
        }
    }

    private static IEnumerable<string> EnumerateCandidates(string directory, string[] entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
        }

        foreach (var name in SubtitleSubfolders)
        {
            var nested = Path.Combine(directory, name);
            if (!Directory.Exists(nested))
            {
                continue;
            }

            string[] nestedEntries;
            try
            {
                nestedEntries = Directory.GetFiles(nested);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in nestedEntries)
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Reads <c>en.forced.srt</c>, <c>eng.sdh.srt</c>, <c>pt-BR.srt</c> and the
    /// bare <c>srt</c> that a plain <c>Movie.srt</c> leaves behind.
    ///
    /// Every token is offered to the one language vocabulary; whatever it does
    /// not recognise is tried as a modifier and otherwise ignored, because
    /// release names put all sorts of things in here. A file with no language
    /// token at all is <see cref="SubtitleLanguages.Unknown"/> and stays that
    /// way — see the note there on why it is not guessed at.
    /// </summary>
    internal static SubtitleFileTags ReadTags(string tagSection)
    {
        var language = SubtitleLanguages.Unknown;
        var forced = false;
        var hearingImpaired = false;

        var parts = tagSection.Split('.', StringSplitOptions.RemoveEmptyEntries);
        // The last part is the extension.
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var candidates = parts[index]
                .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Prepend(parts[index])
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var token in candidates)
            {
                if (language == SubtitleLanguages.Unknown)
                {
                    var code = SubtitleLanguages.Normalize(token);
                    if (code is not null && code != SubtitleLanguages.Unknown)
                    {
                        language = code;
                        continue;
                    }
                }

                if (ForcedTags.Contains(token))
                {
                    forced = true;
                }
                else if (HearingImpairedTags.Contains(token))
                {
                    hearingImpaired = true;
                }
            }
        }

        return new SubtitleFileTags(language, forced, hearingImpaired);
    }

    private static bool LooksForced(string? title)
        => title is not null && ForcedTags.Any(tag => title.Contains(tag, StringComparison.OrdinalIgnoreCase));

    private static bool LooksHearingImpaired(string? title)
        => title is not null &&
           (title.Contains("SDH", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("hearing", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One row per language and variant. A VobSub pair is two files for one
    /// subtitle, and a release that ships both an embedded English track and an
    /// English <c>.srt</c> has English once, not twice — otherwise the bar
    /// under the poster would count past what was asked for.
    ///
    /// A file wins over a stream where both exist, because a file is the thing
    /// a player and a person can both act on.
    /// </summary>
    private static IReadOnlyList<DetectedSubtitle> Deduplicate(List<DetectedSubtitle> found)
    {
        var byVariant = new Dictionary<(string Language, bool Forced, bool HearingImpaired), DetectedSubtitle>();
        foreach (var subtitle in found)
        {
            var key = (subtitle.Language, subtitle.Forced, subtitle.HearingImpaired);
            if (!byVariant.TryGetValue(key, out var existing) ||
                (existing.Source == SubtitleSources.Embedded && subtitle.Source != SubtitleSources.Embedded))
            {
                byVariant[key] = subtitle;
            }
        }

        return byVariant.Values
            .OrderBy(subtitle => subtitle.Language, StringComparer.Ordinal)
            .ThenBy(subtitle => subtitle.Forced)
            .ThenBy(subtitle => subtitle.HearingImpaired)
            .ToArray();
    }
}

internal sealed record SubtitleFileTags(string Language, bool Forced, bool HearingImpaired);
