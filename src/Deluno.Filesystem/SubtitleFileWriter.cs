using Deluno.Contracts;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem;

/// <summary>
/// Writes a fetched subtitle beside the video it belongs to.
///
/// <para><b>The name is the scanner's name.</b>
/// <see cref="SubtitleFileNaming"/> builds it and
/// <see cref="SubtitleInventoryService.ReadTags"/> reads it, and a test walks
/// one into the other — because a file this wrote that the scan did not
/// recognise would be re-fetched every cycle for ever, spending somebody's
/// daily allowance on a subtitle already sitting on their disk, and nothing on
/// screen would look wrong.</para>
///
/// <para><b>Written to a temporary name and moved.</b> A half-written
/// <c>.srt</c> is a file a player will happily open and show nothing from, and
/// the scan would count it as held. The move is atomic on both filesystems
/// Deluno supports.</para>
/// </summary>
public sealed class SubtitleFileWriter(ILogger<SubtitleFileWriter> logger) : ISubtitleFileWriter
{
    public async Task<string> WriteAsync(
        string videoPath,
        string language,
        bool hearingImpaired,
        byte[] subtitle,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"Cannot work out where “{videoPath}” lives, so there is nowhere to put its subtitle.");
        }

        if (!Directory.Exists(directory))
        {
            // The video is gone. Reconciliation owns that; writing a subtitle
            // for it would leave an orphan nothing ever reads.
            throw new DirectoryNotFoundException($"“{directory}” is not there, so the video is not either.");
        }

        var target = Path.Combine(directory, SubtitleFileNaming.For(videoPath, language, hearingImpaired));
        var temporary = target + ".partial";

        await File.WriteAllBytesAsync(temporary, subtitle, cancellationToken);
        File.Move(temporary, target, overwrite: true);

        logger.LogInformation("Wrote a {Language} subtitle to {Path}.", language, target);
        return target;
    }
}

/// <summary>
/// What a subtitle Deluno wrote is called.
///
/// <para>Its own type so the writer and the test that holds it against the
/// scanner both name the same function, rather than the test re-deriving the
/// name and agreeing with itself.</para>
/// </summary>
public static class SubtitleFileNaming
{
    /// <summary>
    /// <c>Big Buck Bunny (2008).en.srt</c>, or <c>.en.sdh.srt</c> when it is a
    /// hearing-impaired track.
    ///
    /// <para><c>sdh</c> rather than <c>hi</c> or <c>cc</c>: the scanner reads all
    /// three, players and Bazarr both use <c>sdh</c>, and picking the one a
    /// person is most likely to recognise costs nothing.</para>
    /// </summary>
    public static string For(string videoPath, string language, bool hearingImpaired)
    {
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var code = SubtitleLanguages.Normalize(language) ?? SubtitleLanguages.Unknown;
        var variant = hearingImpaired ? ".sdh" : string.Empty;

        return $"{stem}.{code}{variant}.srt";
    }
}
