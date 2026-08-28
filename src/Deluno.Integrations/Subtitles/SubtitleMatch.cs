using Deluno.Contracts;
using Deluno.Quality;

namespace Deluno.Integrations.Subtitles;

/// <summary>
/// Which rung a candidate is on, for one file.
///
/// <para>One function, because the alternative is the shape every defect in this
/// codebase has been: the fetcher deciding what to pick and the bar deciding what
/// to paint, from two copies of the same rule that cannot check each other.</para>
/// </summary>
public static class SubtitleMatchRanking
{
    /// <summary>
    /// The rung this candidate sits on against the file being subtitled.
    ///
    /// <para>Both sides are read with <see cref="MediaFileNameFacts"/> — the same
    /// parser the library already uses for the release group and codec columns —
    /// so a subtitle named <c>Severance.S01E01.1080p.WEB.H264-TEPES</c> and a file
    /// named the same way are compared by one set of rules rather than two.</para>
    ///
    /// <para>Unknown is never treated as a match. A provider that tells you
    /// nothing about the release gets <see cref="SubtitleMatch.AnyRelease"/>,
    /// which is the truth: it might be perfect and Deluno cannot say so, and
    /// claiming otherwise is the lie this ladder exists to avoid.</para>
    /// </summary>
    public static SubtitleMatch Rank(string? candidateReleaseName, string? videoPathOrReleaseName)
    {
        var file = MediaFileNameFacts.Parse(videoPathOrReleaseName);
        if (string.IsNullOrWhiteSpace(candidateReleaseName))
        {
            return SubtitleMatch.AnyRelease;
        }

        // A provider names one subtitle after several releases, so the best of
        // them is the answer.
        var best = SubtitleMatch.AnyRelease;
        foreach (var part in candidateReleaseName.Split([',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rung = RankOne(part, file);
            if (rung > best)
            {
                best = rung;
            }

            if (best == SubtitleMatch.MadeForThisFile)
            {
                break;
            }
        }

        return best;
    }

    private static SubtitleMatch RankOne(string candidate, MediaFileFacts file)
    {
        var subtitle = MediaFileNameFacts.Parse(candidate);

        if (!string.IsNullOrWhiteSpace(file.ReleaseGroup)
            && GroupMatches(candidate, subtitle.ReleaseGroup, file.ReleaseGroup!))
        {
            return SubtitleMatch.MadeForThisFile;
        }

        if (!string.IsNullOrWhiteSpace(subtitle.Source)
            && !string.IsNullOrWhiteSpace(file.Source)
            && SameMaster(subtitle.Source!, file.Source!))
        {
            return SubtitleMatch.SameSource;
        }

        return SubtitleMatch.AnyRelease;
    }

    /// <summary>
    /// Whether this candidate names the file's release group.
    ///
    /// <para><b>Providers do not all say it the same way, and the rig is what
    /// proved it.</b> <c>MediaFileNameFacts</c> looks for the trailing
    /// <c>-GROUP</c> convention, which is right for a <i>file</i> name and wrong
    /// for half of what a provider sends. Gestdown answers with a bare
    /// <c>"TEPES"</c> in its <c>version</c> field; others send a full release
    /// name; others a list. A rule that only understood one of those would have
    /// quietly scored every Gestdown subtitle at the bottom rung and re-fetched
    /// it for ever.</para>
    ///
    /// <para>So: the parsed group if there is one, and otherwise the whole token
    /// when it is short and looks like a name rather than a release — no dots,
    /// no spaces, nothing that would make it a title.</para>
    /// </summary>
    private static bool GroupMatches(string candidate, string? parsedGroup, string fileGroup)
    {
        if (!string.IsNullOrWhiteSpace(parsedGroup)
            && string.Equals(parsedGroup, fileGroup, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var bare = candidate.Trim();
        return bare.Length is > 0 and <= 24
            && bare.AsSpan().IndexOfAny('.', ' ', '_') < 0
            && string.Equals(bare, fileGroup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether two sources are the same master for timing purposes.
    ///
    /// <para>A Remux is a Blu-ray with the compression taken off, so a subtitle
    /// cut for one is in time for the other — same disc, same frames, same
    /// moment the film starts. Everything else is only itself.</para>
    /// </summary>
    private static bool SameMaster(string left, string right)
    {
        static string Master(string value) => value switch
        {
            "Remux" => "Bluray",
            _ => value
        };

        return string.Equals(Master(left), Master(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What to tell somebody, in one line, about a subtitle on this rung.</summary>
    public static string Describe(SubtitleMatch match) => match switch
    {
        SubtitleMatch.MadeForThisFile => "Made for this file, so the timing is right.",
        SubtitleMatch.SameSource => "Cut for the same kind of release, so the timing is almost certainly right.",
        _ => "The right title, but not known to match your release — the timing may need a nudge."
    };
}
