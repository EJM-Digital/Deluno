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

        var value = raw;

        // Disc and low-grade sources first. Every branch here previously fell
        // through to null, so classification of existing releases is unchanged.
        if (value.Contains("workprint", StringComparison.OrdinalIgnoreCase)) return "WORKPRINT";
        if (value.Contains("telesync", StringComparison.OrdinalIgnoreCase) || HasToken(value, "ts")) return "TELESYNC";
        if (value.Contains("telecine", StringComparison.OrdinalIgnoreCase) || HasToken(value, "tc")) return "TELECINE";
        if (value.Contains("dvdscr", StringComparison.OrdinalIgnoreCase) || value.Contains("screener", StringComparison.OrdinalIgnoreCase)) return "DVDSCR";
        if (value.Contains("regional", StringComparison.OrdinalIgnoreCase)) return "REGIONAL";
        if (value.Contains("camrip", StringComparison.OrdinalIgnoreCase) || HasToken(value, "cam")) return "CAM";
        if (value.Contains("br-disk", StringComparison.OrdinalIgnoreCase) || value.Contains("brdisk", StringComparison.OrdinalIgnoreCase) || value.Contains("bdmv", StringComparison.OrdinalIgnoreCase) || value.Contains("bdiso", StringComparison.OrdinalIgnoreCase)) return "BR-DISK";
        if (value.Contains("raw-hd", StringComparison.OrdinalIgnoreCase) || value.Contains("rawhd", StringComparison.OrdinalIgnoreCase)) return "Raw-HD";
        if (value.Contains("dvd-r", StringComparison.OrdinalIgnoreCase) || HasToken(value, "dvdr")) return "DVD-R";

        if (ContainsAll(value, "remux", "2160")) return "Remux 2160p";
        if (ContainsAll(value, "hdtv", "2160")) return "HDTV 2160p";
        if (ContainsAll(value, "bluray", "2160") || ContainsAll(value, "blu-ray", "2160") || ContainsAll(value, "bdrip", "2160")) return "Bluray 2160p";
        if (ContainsAll(value, "web", "2160") || ContainsAll(value, "webrip", "2160") || ContainsAll(value, "web-dl", "2160")) return "WEB 2160p";
        if (ContainsAll(value, "remux", "1080")) return "Remux 1080p";
        if (ContainsAll(value, "bluray", "1080") || ContainsAll(value, "blu-ray", "1080") || ContainsAll(value, "bdrip", "1080")) return "Bluray 1080p";
        if (ContainsAll(value, "web", "1080") || ContainsAll(value, "webrip", "1080") || ContainsAll(value, "web-dl", "1080")) return "WEB 1080p";
        if (ContainsAll(value, "hdtv", "1080")) return "HDTV 1080p";
        if (ContainsAll(value, "bluray", "720") || ContainsAll(value, "blu-ray", "720") || ContainsAll(value, "bdrip", "720")) return "Bluray 720p";
        if (ContainsAll(value, "web", "720") || ContainsAll(value, "webrip", "720") || ContainsAll(value, "web-dl", "720")) return "WEB 720p";
        if (ContainsAll(value, "hdtv", "720")) return "HDTV 720p";
        if (ContainsAll(value, "bluray", "576") || ContainsAll(value, "blu-ray", "576")) return "Bluray 576p";
        if (ContainsAll(value, "bluray", "480") || ContainsAll(value, "blu-ray", "480")) return "Bluray 480p";
        if (ContainsAll(value, "web", "480")) return "WEB 480p";
        if (value.Contains("dvd", StringComparison.OrdinalIgnoreCase)) return "DVD";
        if (value.Contains("sdtv", StringComparison.OrdinalIgnoreCase)) return "SDTV";
        return null;
    }

    /// <summary>Whole-token match, so "cam" never fires on "Camera" nor "ts" on "Ghosts".</summary>
    private static bool HasToken(string value, string token)
        => value.Split([' ', '.', '-', '_', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAll(string value, string tokenA, string tokenB)
        => value.Contains(tokenA, StringComparison.OrdinalIgnoreCase)
           && value.Contains(tokenB, StringComparison.OrdinalIgnoreCase);
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
