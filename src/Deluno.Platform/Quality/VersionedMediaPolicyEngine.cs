using System.Collections.ObjectModel;

namespace Deluno.Platform.Quality;

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
            return Decision(
                "missing",
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
                    "upgrade",
                    $"Deluno imported this {mediaLabel}, but the current quality is still unknown. It will keep checking until it reaches {normalizedTarget}.",
                    false,
                    null,
                    normalizedTarget);
            }

            return Decision(
                "waiting",
                $"This {mediaLabel} is already in your library.",
                false,
                null,
                normalizedTarget);
        }

        if (IsAtOrAboveCutoff(normalizedCurrent, normalizedTarget))
        {
            return Decision(
                "waiting",
                $"This {mediaLabel} already meets your target quality with {normalizedCurrent}.",
                true,
                normalizedCurrent,
                normalizedTarget);
        }

        if (input.UpgradeUntilCutoff && !string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return Decision(
                "upgrade",
                $"This {mediaLabel} is currently {normalizedCurrent}. Deluno will keep looking until it reaches {normalizedTarget}.",
                false,
                normalizedCurrent,
                normalizedTarget);
        }

        return Decision(
            "waiting",
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

        if (ContainsAll(value, "remux", "2160")) return "Remux 2160p";
        if (ContainsAll(value, "bluray", "2160") || ContainsAll(value, "blu-ray", "2160") || ContainsAll(value, "bdrip", "2160")) return "Bluray 2160p";
        if (ContainsAll(value, "web", "2160") || ContainsAll(value, "webrip", "2160") || ContainsAll(value, "web-dl", "2160")) return "WEB 2160p";
        if (ContainsAll(value, "remux", "1080")) return "Remux 1080p";
        if (ContainsAll(value, "bluray", "1080") || ContainsAll(value, "blu-ray", "1080") || ContainsAll(value, "bdrip", "1080")) return "Bluray 1080p";
        if (ContainsAll(value, "web", "1080") || ContainsAll(value, "webrip", "1080") || ContainsAll(value, "web-dl", "1080")) return "WEB 1080p";
        if (ContainsAll(value, "hdtv", "1080")) return "HDTV 1080p";
        if (ContainsAll(value, "bluray", "720") || ContainsAll(value, "blu-ray", "720") || ContainsAll(value, "bdrip", "720")) return "Bluray 720p";
        if (ContainsAll(value, "web", "720") || ContainsAll(value, "webrip", "720") || ContainsAll(value, "web-dl", "720")) return "WEB 720p";
        if (ContainsAll(value, "hdtv", "720")) return "HDTV 720p";
        if (value.Contains("dvd", StringComparison.OrdinalIgnoreCase)) return "DVD";
        if (value.Contains("sdtv", StringComparison.OrdinalIgnoreCase)) return "SDTV";
        return null;
    }

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
            ["SDTV"] = 10,
            ["DVD"] = 20,
            ["HDTV 720p"] = 30,
            ["WEB 720p"] = 40,
            ["Bluray 720p"] = 50,
            ["HDTV 1080p"] = 60,
            ["WEB 1080p"] = 70,
            ["Bluray 1080p"] = 80,
            ["Remux 1080p"] = 90,
            ["WEB 2160p"] = 100,
            ["Bluray 2160p"] = 110,
            ["Remux 2160p"] = 120
        }));

    public static string NormalizeMediaType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() is "tv" or "series" or "shows" ? "tv" : "movies";
}
