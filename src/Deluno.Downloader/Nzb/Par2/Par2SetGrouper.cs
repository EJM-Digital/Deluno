using System.Text.RegularExpressions;

namespace Deluno.Downloader.Nzb.Par2;

/// <summary>
/// Groups a list of par2 files into their distinct recovery sets and
/// picks an index volume per set.
///
/// <para><b>Why this is non-trivial:</b> a single NZB can ship multiple,
/// independent par2 recovery sets. The canonical pattern is a feature
/// release plus a sample:</para>
/// <code>
///   Movie.Title.2024.1080p.mkv
///   Movie.Title.2024.1080p.par2          ← index for main set
///   Movie.Title.2024.1080p.vol000+01.par2
///   Movie.Title.2024.1080p.vol001+02.par2
///   ...
///   sample/movie-sample.mkv
///   sample/movie-sample.par2             ← index for sample set
///   sample/movie-sample.vol000+01.par2
/// </code>
///
/// <para><b>Set ↔ payload association:</b> par2's spec (v2.0) embeds
/// the protected file list IN the .par2 packet, so par2cmdline knows
/// which payload files belong to which set without us telling it. We
/// don't try to read packet headers ourselves; we just hand par2cmdline
/// the index volume and let it discover its own family of recovery
/// volumes plus the payload files it protects (located in the same
/// directory by convention — release groups never scatter par2 across
/// directories).</para>
///
/// <para><b>Set name derivation:</b> the recovery-volume naming
/// convention is <c>&lt;setname&gt;.vol###+##.par2</c>. The index
/// volume drops the <c>.vol###+##</c> suffix and is just
/// <c>&lt;setname&gt;.par2</c>. We group by setname by stripping the
/// <c>.vol###+##</c> infix when present, then collapsing on the
/// resulting basename. If a set has only volume files and no index
/// (rare but spec-legal), the smallest volume by size acts as the
/// entry point — par2cmdline can bootstrap from any volume.</para>
///
/// <para><b>Orphan payload files:</b> files that aren't covered by any
/// par2 set still ship to extract/post-process normally — par2 being
/// absent just means we can't verify, not that the payload is invalid.
/// The existence of even one par2 set in the NZB doesn't imply that
/// EVERY payload file is protected (release groups sometimes par2 only
/// the rar volumes, leaving sfv/nfo untouched).</para>
/// </summary>
public static class Par2SetGrouper
{
    private static readonly Regex VolumePattern = new(
        @"\.vol\d+\+\d+\.par2$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns one (setName, indexFilePath) tuple per distinct recovery
    /// set found in <paramref name="par2Files"/>.
    /// </summary>
    public static IReadOnlyList<Par2SetGroup> Group(IReadOnlyList<string> par2Files)
    {
        var groups = par2Files
            .GroupBy(f =>
            {
                var name = Path.GetFileName(f);
                var stripped = VolumePattern.Replace(name, "");
                // Remove the trailing ".par2" so the key is just the
                // basename — handles bare-index volumes too.
                if (stripped.EndsWith(".par2", StringComparison.OrdinalIgnoreCase))
                    stripped = stripped[..^5];
                // Include directory so two sets with the same name in
                // different subdirs (movie + sample/) stay distinct.
                return Path.Combine(Path.GetDirectoryName(f) ?? "", stripped);
            }, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<Par2SetGroup>(groups.Count);
        foreach (var group in groups)
        {
            // Prefer the explicit index volume (<basename>.par2 with no
            // vol### infix). Fall back to the smallest volume if there's
            // no index — par2cmdline can start from any volume in the
            // set, indexes are just an optimization.
            var members = group.ToList();
            var index = members.FirstOrDefault(f =>
                !VolumePattern.IsMatch(Path.GetFileName(f)))
                ?? members.OrderBy(f => SafeFileSize(f)).First();
            var setName = StripVolumeInfix(Path.GetFileName(index));
            if (setName.EndsWith(".par2", StringComparison.OrdinalIgnoreCase))
                setName = setName[..^5];
            result.Add(new Par2SetGroup(setName, index, members));
        }
        return result;
    }

    private static string StripVolumeInfix(string fileName) =>
        VolumePattern.Replace(fileName, "");

    private static long SafeFileSize(string path)
    {
        // Tests pass paths that may not exist on disk; treat missing as
        // size 0 so grouping still works. Production callers always
        // pass real files (post-fetch).
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }
}

/// <summary>One par2 recovery set discovered in a directory.</summary>
/// <param name="SetName">Canonical set name (basename minus all .par2/.vol###+## suffixes).</param>
/// <param name="IndexFile">Path to hand par2cmdline as the entry point.</param>
/// <param name="AllFiles">Every .par2 file that belongs to this set (index + volumes).</param>
public sealed record Par2SetGroup(
    string SetName,
    string IndexFile,
    IReadOnlyList<string> AllFiles);
