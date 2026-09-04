namespace Deluno.Contracts;

/// <summary>
/// Reads a stored media path by its own shape, not by the host's.
///
/// <para><b>Why <see cref="Path"/> is the wrong tool for these.</b>
/// <c>System.IO.Path</c> answers for the machine it is running on. On Windows
/// it treats both <c>\</c> and <c>/</c> as separators; on Linux a backslash is
/// an ordinary filename character, so
/// <c>Path.GetFileName(@"D:\Media\Dune.mkv")</c> returns the whole string. That
/// is correct for a path this process is about to open, and wrong for a path
/// this process is only reading.</para>
///
/// <para>Deluno stores paths — the file a title was imported at, the subtitle a
/// queued job refers to, the file a dispatch produced — and the machine that
/// reads one back is not always the machine that wrote it. The installer is
/// Windows, the container image is Linux, and a migration from Radarr or Sonarr
/// carries whatever paths that install recorded. Which host wrote a path is not
/// knowable from the path, and does not need to be: what a file is called is a
/// property of the path.</para>
///
/// <para><b>Use this to read; use <see cref="Path"/> to act.</b> Deciding a
/// name to show, log, or build a sibling filename from is reading. Combining,
/// resolving, or opening is acting, and must stay with <see cref="Path"/> so
/// that a path this host cannot reach is refused rather than half-understood.
/// </para>
///
/// <para>The cost is a Linux file whose name genuinely contains a backslash,
/// which loses the part before it. Against that: without this, a Deluno running
/// as the container wrote whole paths into subtitle filenames, and reported
/// <c>D:\Media\film.en.srt</c> where a person expected <c>film.en.srt</c>.
/// </para>
/// </summary>
public static class MediaPath
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>The last segment: everything after the final <c>/</c> or <c>\</c>.</summary>
    public static string FileName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var separator = path.LastIndexOfAny(Separators);
        return separator >= 0 ? path[(separator + 1)..] : path;
    }

    /// <summary>
    /// <see cref="FileName"/> without its extension.
    ///
    /// <para>A leading dot is not an extension — <c>.srt</c> is the whole name —
    /// which is the rule <see cref="Path"/> uses and worth keeping.</para>
    /// </summary>
    public static string FileNameWithoutExtension(string? path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
