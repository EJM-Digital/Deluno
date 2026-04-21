namespace Deluno.Platform.Quality;

public static class LibraryQualityDecider
{
    private static readonly IReadOnlyDictionary<string, int> QualityRanks =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
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
        };

    public static LibraryQualityDecision Decide(
        string mediaLabel,
        bool hasFile,
        string? currentQuality,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems)
    {
        var normalizedCurrent = NormalizeQuality(currentQuality);
        var normalizedTarget = NormalizeQuality(cutoffQuality);

        if (!hasFile)
        {
            return new LibraryQualityDecision(
                WantedStatus: "missing",
                WantedReason: $"Deluno is still looking for this {mediaLabel}.",
                QualityCutoffMet: false,
                CurrentQuality: normalizedCurrent,
                TargetQuality: normalizedTarget);
        }

        if (string.IsNullOrWhiteSpace(normalizedCurrent))
        {
            if (upgradeUnknownItems && !string.IsNullOrWhiteSpace(normalizedTarget))
            {
                return new LibraryQualityDecision(
                    WantedStatus: "upgrade",
                    WantedReason: $"Deluno imported this {mediaLabel}, but the current quality is still unknown. It will keep checking until it reaches {normalizedTarget}.",
                    QualityCutoffMet: false,
                    CurrentQuality: null,
                    TargetQuality: normalizedTarget);
            }

            return new LibraryQualityDecision(
                WantedStatus: "waiting",
                WantedReason: $"This {mediaLabel} is already in your library.",
                QualityCutoffMet: false,
                CurrentQuality: null,
                TargetQuality: normalizedTarget);
        }

        if (IsAtOrAboveCutoff(normalizedCurrent, normalizedTarget))
        {
            return new LibraryQualityDecision(
                WantedStatus: "waiting",
                WantedReason: $"This {mediaLabel} already meets your target quality with {normalizedCurrent}.",
                QualityCutoffMet: true,
                CurrentQuality: normalizedCurrent,
                TargetQuality: normalizedTarget);
        }

        if (upgradeUntilCutoff && !string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return new LibraryQualityDecision(
                WantedStatus: "upgrade",
                WantedReason: $"This {mediaLabel} is currently {normalizedCurrent}. Deluno will keep looking until it reaches {normalizedTarget}.",
                QualityCutoffMet: false,
                CurrentQuality: normalizedCurrent,
                TargetQuality: normalizedTarget);
        }

        return new LibraryQualityDecision(
            WantedStatus: "waiting",
            WantedReason: $"This {mediaLabel} is currently {normalizedCurrent}.",
            QualityCutoffMet: false,
            CurrentQuality: normalizedCurrent,
            TargetQuality: normalizedTarget);
    }

    public static string? DetectQuality(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw;

        if (ContainsAll(value, "remux", "2160"))
        {
            return "Remux 2160p";
        }

        if (ContainsAll(value, "bluray", "2160") || ContainsAll(value, "blu-ray", "2160") || ContainsAll(value, "bdrip", "2160"))
        {
            return "Bluray 2160p";
        }

        if (ContainsAll(value, "web", "2160") || ContainsAll(value, "webrip", "2160") || ContainsAll(value, "web-dl", "2160"))
        {
            return "WEB 2160p";
        }

        if (ContainsAll(value, "remux", "1080"))
        {
            return "Remux 1080p";
        }

        if (ContainsAll(value, "bluray", "1080") || ContainsAll(value, "blu-ray", "1080") || ContainsAll(value, "bdrip", "1080"))
        {
            return "Bluray 1080p";
        }

        if (ContainsAll(value, "web", "1080") || ContainsAll(value, "webrip", "1080") || ContainsAll(value, "web-dl", "1080"))
        {
            return "WEB 1080p";
        }

        if (ContainsAll(value, "hdtv", "1080"))
        {
            return "HDTV 1080p";
        }

        if (ContainsAll(value, "bluray", "720") || ContainsAll(value, "blu-ray", "720") || ContainsAll(value, "bdrip", "720"))
        {
            return "Bluray 720p";
        }

        if (ContainsAll(value, "web", "720") || ContainsAll(value, "webrip", "720") || ContainsAll(value, "web-dl", "720"))
        {
            return "WEB 720p";
        }

        if (ContainsAll(value, "hdtv", "720"))
        {
            return "HDTV 720p";
        }

        if (value.Contains("dvd", StringComparison.OrdinalIgnoreCase))
        {
            return "DVD";
        }

        if (value.Contains("sdtv", StringComparison.OrdinalIgnoreCase))
        {
            return "SDTV";
        }

        return null;
    }

    public static string? NormalizeQuality(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            return null;
        }

        return QualityRanks.Keys.FirstOrDefault(item => string.Equals(item, quality.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? DetectQuality(quality);
    }

    private static bool IsAtOrAboveCutoff(string? currentQuality, string? cutoffQuality)
    {
        if (string.IsNullOrWhiteSpace(currentQuality) || string.IsNullOrWhiteSpace(cutoffQuality))
        {
            return false;
        }

        return GetRank(currentQuality) >= GetRank(cutoffQuality);
    }

    private static int GetRank(string quality)
        => QualityRanks.TryGetValue(quality, out var rank) ? rank : 0;

    private static bool ContainsAll(string value, string tokenA, string tokenB)
        => value.Contains(tokenA, StringComparison.OrdinalIgnoreCase)
           && value.Contains(tokenB, StringComparison.OrdinalIgnoreCase);
}
