namespace Deluno.Platform.Quality;

public static class LibraryQualityDecider
{
    private static readonly IVersionedMediaPolicyEngine Engine = new VersionedMediaPolicyEngine();

    public static LibraryQualityDecision Decide(
        string mediaLabel,
        bool hasFile,
        string? currentQuality,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems)
        => Engine.DecideWantedState(new MediaWantedDecisionInput(
            MediaType: mediaLabel.Contains("TV", StringComparison.OrdinalIgnoreCase) ? "tv" : "movies",
            hasFile,
            currentQuality,
            cutoffQuality,
            upgradeUntilCutoff,
            upgradeUnknownItems));

    public static string? DetectQuality(string? raw)
        => Engine.DetectQuality(raw);

    public static string? NormalizeQuality(string? quality)
        => Engine.NormalizeQuality(quality);

    public static int GetRank(string? quality)
        => Engine.QualityRank(quality);
}
