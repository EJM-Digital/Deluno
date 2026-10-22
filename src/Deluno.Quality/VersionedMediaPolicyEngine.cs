using System.Collections.ObjectModel;
using Deluno.Contracts;

namespace Deluno.Quality;

public interface IVersionedMediaPolicyEngine
{
    string CurrentVersion { get; }

    LibraryQualityDecision DecideWantedState(MediaWantedDecisionInput input);

    string? DetectQuality(string? raw);

    string? NormalizeQuality(string? quality);

    int QualityRank(string? quality);

    PolicyMigrationResult Migrate(MediaPolicySnapshot snapshot);
}

public sealed class VersionedMediaPolicyEngine : IVersionedMediaPolicyEngine
{
    private readonly MediaPolicyDefinition current = MediaPolicyCatalog.Current;

    public string CurrentVersion => current.Version;

    public LibraryQualityDecision DecideWantedState(MediaWantedDecisionInput input)
    {
        var mediaType = MediaPolicyCatalog.NormalizeMediaType(input.MediaType);
        var mediaLabel = mediaType == "tv" ? "TV show" : "movie";
        var normalizedCurrent = NormalizeQuality(input.CurrentQuality);
        var normalizedTarget = NormalizeQuality(input.CutoffQuality);

        if (!input.HasFile)
        {
            // Not out yet is not the same as not found. Saying Missing of a movie
            // that has not been released blames the library for the calendar,
            // and sends every search cycle after something that cannot exist.
            if (!input.IsReleased)
            {
                return Decision(
                    WantedStatuses.Upcoming,
                    $"This {mediaLabel} is not out yet. Deluno will start looking when it is.",
                    false,
                    normalizedCurrent,
                    normalizedTarget);
            }

            return Decision(
                WantedStatuses.Missing,
                $"Deluno is still looking for this {mediaLabel}.",
                false,
                normalizedCurrent,
                normalizedTarget);
        }

        if (string.IsNullOrWhiteSpace(normalizedCurrent))
        {
            if (input.UpgradeUnknownItems && !string.IsNullOrWhiteSpace(normalizedTarget))
            {
                return Decision(
                    WantedStatuses.Upgrade,
                    $"Deluno imported this {mediaLabel}, but the current quality is still unknown. It will keep checking until it reaches {normalizedTarget}.",
                    false,
                    null,
                    normalizedTarget);
            }

            return Decision(
                WantedStatuses.Covered,
                $"This {mediaLabel} is already in your library.",
                false,
                null,
                normalizedTarget);
        }

        // A file too small for its own label is a bad copy, not a finished
        // title — and the fix is the one Deluno already has. Checked before the
        // cutoff, because otherwise the tier's name alone would call it done
        // and nothing downstream would ever look again.
        //
        // Only the floor. A file *over* the ceiling is wasted disk rather than
        // a bad copy, and marking it Upgrade would send Deluno hunting for
        // something better, which is the opposite of the problem. That one is a
        // question you ask the shelf, not a job for the upgrade process.
        if (IsBelowItsOwnFloor(input))
        {
            var floorGb = (input.SizeFloorBytes ?? 0) / 1024d / 1024d / 1024d;
            var actualGb = (input.FileSizeBytes ?? 0) / 1024d / 1024d / 1024d;

            return Decision(
                WantedStatuses.Upgrade,
                $"This {mediaLabel} is {actualGb:0.##} GB, below the {floorGb:0.#} GB minimum your rules set for {normalizedCurrent}. Deluno will look for a better copy.",
                false,
                normalizedCurrent,
                normalizedTarget);
        }

        if (IsAtOrAboveCutoff(normalizedCurrent, normalizedTarget))
        {
            return Decision(
                WantedStatuses.Covered,
                $"This {mediaLabel} already meets your target quality with {normalizedCurrent}.",
                true,
                normalizedCurrent,
                normalizedTarget);
        }

        if (input.UpgradeUntilCutoff && !string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return Decision(
                WantedStatuses.Upgrade,
                $"This {mediaLabel} is currently {normalizedCurrent}. Deluno will keep looking until it reaches {normalizedTarget}.",
                false,
                normalizedCurrent,
                normalizedTarget);
        }

        return Decision(
            WantedStatuses.Covered,
            $"This {mediaLabel} is currently {normalizedCurrent}.",
            false,
            normalizedCurrent,
            normalizedTarget);
    }


    /// <summary>
    /// Whether the file is smaller than its own tier's rule allows.
    ///
    /// <para>Every part has to be known: a caller that did not supply a size,
    /// or a tier with no floor set, gets the decision it would have got before
    /// this existed. Unknown is never treated as a breach — that would mark a
    /// whole library Upgradable the first time somebody imported without sizes.</para>
    /// </summary>
    private static bool IsBelowItsOwnFloor(MediaWantedDecisionInput input)
        => input.HasFile
        && input.FileSizeBytes is > 0
        && input.SizeFloorBytes is > 0
        && input.FileSizeBytes < input.SizeFloorBytes;

    public string? DetectQuality(string? raw)
        => current.DetectQuality(raw);

    public string? NormalizeQuality(string? quality)
        => current.NormalizeQuality(quality);

    public int QualityRank(string? quality)
        => current.GetRank(NormalizeQuality(quality));

    public PolicyMigrationResult Migrate(MediaPolicySnapshot snapshot)
    {
        var sourceVersion = string.IsNullOrWhiteSpace(snapshot.Version)
            ? "unknown"
            : snapshot.Version.Trim();

        if (string.Equals(sourceVersion, current.Version, StringComparison.OrdinalIgnoreCase))
        {
            return new PolicyMigrationResult(sourceVersion, current.Version, Changed: false, Snapshot: snapshot, Notes: ["Policy is already current."]);
        }

        var cutoff = NormalizeQuality(snapshot.CutoffQuality) ?? current.DefaultCutoffQuality;
        var allowed = snapshot.AllowedQualities
            .Select(NormalizeQuality)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();

        if (allowed.Length == 0)
        {
            allowed = current.DefaultAllowedQualities.ToArray();
        }

        return new PolicyMigrationResult(
            sourceVersion,
            current.Version,
            Changed: true,
            Snapshot: snapshot with
            {
                Version = current.Version,
                CutoffQuality = cutoff,
                AllowedQualities = allowed
            },
            Notes:
            [
                $"Migrated policy snapshot from {sourceVersion} to {current.Version}.",
                "Quality names were normalized to the current policy vocabulary."
            ]);
    }

    private bool IsAtOrAboveCutoff(string? currentQuality, string? cutoffQuality)
    {
        if (string.IsNullOrWhiteSpace(currentQuality) || string.IsNullOrWhiteSpace(cutoffQuality))
        {
            return false;
        }

        return QualityRank(currentQuality) >= QualityRank(cutoffQuality);
    }

    private LibraryQualityDecision Decision(
        string wantedStatus,
        string wantedReason,
        bool qualityCutoffMet,
        string? currentQuality,
        string? targetQuality)
        => new(
            wantedStatus,
            wantedReason,
            qualityCutoffMet,
            currentQuality,
            targetQuality,
            current.Version);
}

public sealed record MediaPolicySnapshot(
    string Version,
    string? CutoffQuality,
    IReadOnlyList<string> AllowedQualities,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems);

public sealed record PolicyMigrationResult(
    string FromVersion,
    string ToVersion,
    bool Changed,
    MediaPolicySnapshot Snapshot,
    IReadOnlyList<string> Notes);

public sealed record MediaPolicyDefinition(
    string Version,
    string DefaultCutoffQuality,
    IReadOnlyList<string> DefaultAllowedQualities,
    IReadOnlyDictionary<string, int> QualityRanks)
{
    public int GetRank(string? quality)
        => !string.IsNullOrWhiteSpace(quality) && QualityRanks.TryGetValue(quality, out var rank) ? rank : 0;

    public string? NormalizeQuality(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            return null;
        }

        return QualityRanks.Keys.FirstOrDefault(item => string.Equals(item, quality.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? DetectQuality(quality);
    }

    public string? DetectQuality(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Whole words only, and never the title's own: "Webster", "Regional.Manager" and "The.Screener.2019" carry no
        // source (ReleaseNameWords).
        var words = ReleaseNameWords.TagWords(raw);
        bool Has(params string[] wanted) => ReleaseNameWords.HasTag(words, wanted);
        bool HasPrefix(params string[] prefixes) => ReleaseNameWords.HasTagStartingWith(words, prefixes);
        var bluray = Has("bluray", "bdrip") || ReleaseNameWords.HasPair(words, "blu", "ray");
        var web = Has("web", "webrip", "webdl");

        // Disc and low-grade sources first. Every branch here previously fell
        // through to null, so classification of existing releases is unchanged.
        if (Has("workprint")) return "WORKPRINT";
        if (Has("telesync", "ts")) return "TELESYNC";
        if (Has("telecine", "tc")) return "TELECINE";
        if (Has("dvdscr", "screener")) return "DVDSCR";
        if (Has("regional")) return "REGIONAL";
        if (Has("camrip", "cam")) return "CAM";
        if (Has("brdisk", "bdmv", "bdiso") || ReleaseNameWords.HasPair(words, "br", "disk")) return "BR-DISK";
        if (Has("rawhd") || ReleaseNameWords.HasPair(words, "raw", "hd")) return "Raw-HD";
        if (Has("dvdr") || ReleaseNameWords.HasPair(words, "dvd", "r")) return "DVD-R";

        var remux = Has("remux");
        var hdtv = Has("hdtv");
        foreach (var (resolution, label) in new[] { ("2160", "2160p"), ("1080", "1080p"), ("720", "720p"), ("576", "576p"), ("480", "480p") })
        {
            if (!HasPrefix(resolution))
            {
                continue;
            }

            if (remux && label is "2160p" or "1080p") return $"Remux {label}";
            if (hdtv && label is "2160p" or "1080p" or "720p") return $"HDTV {label}";
            if (bluray) return $"Bluray {label}";
            if (web && label is "2160p" or "1080p" or "720p" or "480p") return $"WEB {label}";
        }

        if (HasPrefix("dvd")) return "DVD";
        if (Has("sdtv")) return "SDTV";
        return null;
    }
}

public static class MediaPolicyCatalog
{
    public const string CurrentVersion = "media-policy/v1";

    public static MediaPolicyDefinition Current { get; } = new(
        CurrentVersion,
        "WEB 1080p",
        ["WEB 720p", "WEB 1080p", "Bluray 1080p", "WEB 2160p", "Bluray 2160p"],
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Unknown"] = 1,
            ["WORKPRINT"] = 2,
            ["CAM"] = 3,
            ["TELESYNC"] = 4,
            ["TELECINE"] = 5,
            ["REGIONAL"] = 6,
            ["DVDSCR"] = 7,
            ["SDTV"] = 10,
            ["DVD"] = 20,
            ["DVD-R"] = 21,
            ["WEB 480p"] = 22,
            ["Bluray 480p"] = 24,
            ["Bluray 576p"] = 25,
            ["HDTV 720p"] = 30,
            ["WEB 720p"] = 40,
            ["Bluray 720p"] = 50,
            ["HDTV 1080p"] = 60,
            ["WEB 1080p"] = 70,
            ["Bluray 1080p"] = 80,
            ["Remux 1080p"] = 90,
            ["HDTV 2160p"] = 95,
            ["WEB 2160p"] = 100,
            ["Bluray 2160p"] = 110,
            ["Remux 2160p"] = 120,
            ["BR-DISK"] = 125,
            ["Raw-HD"] = 126
        }));

    public static string NormalizeMediaType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() is "tv" or "series" or "shows" ? "tv" : "movies";
}
