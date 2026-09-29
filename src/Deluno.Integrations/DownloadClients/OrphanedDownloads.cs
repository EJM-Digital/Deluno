using System.Text.RegularExpressions;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// A folder or file in a library's completed-download folder that nothing
/// claims (#588): no download client lists it, no import or hand-off is using
/// it, and it is not a folder Deluno configured for something else.
/// </summary>
/// <param name="DelunoGrabbedIt">
/// Whether Deluno can prove it downloaded this: its name, less a client's
/// duplicate suffix such as <c>.1</c>, is the name of a release Deluno grabbed.
/// Only then may Deluno remove it, and only when a person asks.
/// </param>
public sealed record OrphanedDownloadItem(
    string Path,
    string Name,
    string LibraryId,
    string LibraryName,
    long SizeBytes,
    DateTimeOffset LastWrittenUtc,
    bool DelunoGrabbedIt);

/// <summary>One entry found at the top of a completed-download folder.</summary>
public sealed record DownloadFolderEntry(string Path, bool IsDirectory, long SizeBytes, DateTimeOffset LastWrittenUtc);

/// <summary>A library's completed-download folder and what it found there.</summary>
public sealed record DownloadFolderScan(string LibraryId, string LibraryName, string DownloadsPath, IReadOnlyList<DownloadFolderEntry> Entries);

/// <summary>
/// The rule that decides what is orphaned, kept apart from the file system
/// and the repositories so it can be proven on its own.
/// </summary>
public static partial class OrphanedDownloads
{
    /// <summary>
    /// How long an entry must have sat unclaimed. A download that has just
    /// finished can appear on disk a moment before its client lists it.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(2);

    /// <param name="claimedPaths">
    /// Every path something is using: client queue and history items, import
    /// sources, hand-off sources and outputs, and the folders Deluno was
    /// configured with (library roots, processor output folders, incomplete
    /// downloads). An entry is claimed if it is one of them, holds one of them,
    /// or sits inside one of them.
    /// </param>
    /// <param name="grabbedReleaseNames">The names of releases Deluno has grabbed.</param>
    /// <param name="kept">Paths a person chose to keep.</param>
    public static IReadOnlyList<OrphanedDownloadItem> Find(
        IEnumerable<DownloadFolderScan> scans,
        IEnumerable<string> claimedPaths,
        IEnumerable<string> grabbedReleaseNames,
        IEnumerable<string> kept,
        DateTimeOffset now,
        IEnumerable<string>? excludePatterns = null,
        TimeSpan? minAge = null)
    {
        var grace = minAge ?? Grace;
        var skip = (excludePatterns ?? []).Select(GlobToRegex).OfType<Regex>().ToArray();
        var claimed = claimedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var grabbed = new HashSet<string>(grabbedReleaseNames.Select(name => name.Trim()), StringComparer.OrdinalIgnoreCase);
        var keptKeys = new HashSet<string>(kept.Select(Key), StringComparer.OrdinalIgnoreCase);
        var found = new List<OrphanedDownloadItem>();

        foreach (var scan in scans)
        {
            // The folder being scanned, and any folder holding it, claim
            // nothing in it: everything in a download folder is inside them.
            var downloads = Key(scan.DownloadsPath);
            var claimedHere = claimed
                .Where(path => !string.Equals(path, downloads, StringComparison.OrdinalIgnoreCase)
                    && !downloads.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var entry in scan.Entries)
            {
                var key = Key(entry.Path);
                // From the normalised key, so a Windows path names the same entry on Linux.
                var name = key[(key.LastIndexOf('/') + 1)..];
                if (now - entry.LastWrittenUtc < grace
                    || keptKeys.Contains(key)
                    || skip.Any(pattern => Skips(pattern, name))
                    || claimedHere.Any(path => Related(key, path)))
                {
                    continue;
                }

                found.Add(new OrphanedDownloadItem(
                    entry.Path,
                    name,
                    scan.LibraryId,
                    scan.LibraryName,
                    entry.SizeBytes,
                    entry.LastWrittenUtc,
                    WasGrabbed(entry.IsDirectory ? name : System.IO.Path.GetFileNameWithoutExtension(name), grabbed)));
            }
        }

        return found
            .OrderBy(item => item.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The skip list as typed: names or globs (<c>*.nfo</c>, <c>Thumbs.db</c>,
    /// <c>Sample?</c>) separated by commas, semicolons or new lines (#1001).
    /// </summary>
    public static IReadOnlyList<string> ParseExcludePatterns(string? text)
        => (text ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(pattern => pattern.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// A glob (<c>*</c> any run, <c>?</c> one character) matched against a
    /// whole name, ignoring case. Every other character is literal, so a
    /// pattern can never be a regular expression that misbehaves.
    /// </summary>
    public static bool MatchesGlob(string pattern, string name)
        => GlobToRegex(pattern) is { } regex && Skips(regex, name);

    /// <summary>
    /// A pattern that takes too long to match (something like <c>*a*a*a*a*b</c> against a long name) counts
    /// as a match, so the name is skipped. Skipping only ever keeps a folder off the delete list, so an
    /// awkward pattern can never make the scan fail or a folder more likely to be removed (#1072).
    /// </summary>
    private static bool Skips(Regex pattern, string name)
    {
        try
        {
            return pattern.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static Regex? GlobToRegex(string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0)
        {
            return null;
        }

        var body = string.Concat(pattern.Select(character => character switch
        {
            '*' => ".*",
            '?' => ".",
            _ => Regex.Escape(character.ToString())
        }));
        return new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>A path compared case-insensitively, with either slash and no trailing one.</summary>
    public static string Key(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Whether a name on disk is a release Deluno grabbed: as it is, or as a
    /// client saved it when the name was taken (<c>Name.1</c>, <c>Name.2</c>).
    /// Tried both ways, because a release name can itself end in ".1" (DDP5.1).
    /// </summary>
    public static bool WasGrabbed(string name, IReadOnlySet<string> grabbed)
        => grabbed.Contains(name.Trim()) || grabbed.Contains(DuplicateSuffix().Replace(name.Trim(), string.Empty));

    private static bool Related(string entry, string claimed)
        => string.Equals(entry, claimed, StringComparison.OrdinalIgnoreCase)
            || claimed.StartsWith(entry + "/", StringComparison.OrdinalIgnoreCase)
            || entry.StartsWith(claimed + "/", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\.\d{1,2}$")]
    private static partial Regex DuplicateSuffix();
}

/// <summary>A person keeping or removing one orphaned download (#588).</summary>
public sealed record OrphanedDownloadRequest(string Path);
