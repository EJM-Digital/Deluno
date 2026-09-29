using System.Globalization;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// One torrent's own seed limits, put in plain words for Transfers (#1026).
/// Each adapter reads its client's per-torrent fields and hands them here so
/// the wording is the same whichever client answered.
/// </summary>
public static class SeedLimitText
{
    /// <summary>
    /// Transmission's per-torrent limits. <c>seedRatioMode</c> and
    /// <c>seedIdleMode</c> are 0 to follow the daemon's own limits, 1 to use
    /// the torrent's <c>seedRatioLimit</c> / <c>seedIdleLimit</c> (idle is in
    /// minutes), and 2 for no limit (Transmission's TR_RATIOLIMIT_* and
    /// TR_IDLELIMIT_* values, the same ones Radarr and Sonarr's
    /// TransmissionProxy write with mode 1).
    /// </summary>
    public static string? ForTransmission(int? ratioMode, double? ratioLimit, int? idleMode, int? idleLimitMinutes)
    {
        if (ratioMode is null && idleMode is null)
        {
            return null;
        }

        var own = new List<string>();
        if (ratioMode == 1 && ratioLimit is { } ratio)
        {
            own.Add($"ratio {Format(ratio)}");
        }

        if (idleMode == 1 && idleLimitMinutes is { } idle)
        {
            own.Add($"{idle} minute{(idle == 1 ? string.Empty : "s")} with no uploading");
        }

        if (own.Count > 0)
        {
            return $"Stops seeding at {string.Join(" or after ", own)}.";
        }

        return ratioMode == 2 && idleMode == 2
            ? "Seeds with no limit."
            : "Follows Transmission's overall seed limits.";
    }

    /// <summary>
    /// Deluge's per-torrent <c>stop_at_ratio</c> / <c>stop_ratio</c> /
    /// <c>remove_at_ratio</c>. Deluge copies its global seed settings onto each
    /// torrent when it is added, so the torrent's own flag is the answer: on
    /// means it stops (or is removed) at that ratio, off means nothing stops it.
    /// Deluge has no per-torrent idle limit.
    /// </summary>
    public static string? ForDeluge(bool? stopAtRatio, double? stopRatio, bool? removeAtRatio)
    {
        if (stopAtRatio is null)
        {
            return null;
        }

        if (stopAtRatio == false)
        {
            return "Seeds with no ratio limit.";
        }

        var ratio = stopRatio is { } value ? Format(value) : "its limit";
        return removeAtRatio == true
            ? $"Removed from Deluge at ratio {ratio}."
            : $"Stops seeding at ratio {ratio}.";
    }

    private static string Format(double ratio)
        => ratio.ToString("0.##", CultureInfo.InvariantCulture);
}
