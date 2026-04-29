using Deluno.Infrastructure.Observability;

namespace Deluno.Platform.Quality;

public interface IMediaDecisionService
{
    LibraryQualityDecision DecideWantedState(MediaWantedDecisionInput input);

    string? DetectQuality(string? raw);
}

public sealed class MediaDecisionService : IMediaDecisionService
{
    public LibraryQualityDecision DecideWantedState(MediaWantedDecisionInput input)
    {
        var decision = MediaDecisionRules.DecideWantedState(input);
        DelunoObservability.DecisionOutcomes.Add(
            1,
            new("media.type", MediaDecisionRules.NormalizeMediaType(input.MediaType)),
            new("wanted.status", decision.WantedStatus),
            new("has.file", input.HasFile));
        return decision;
    }

    public string? DetectQuality(string? raw)
        => LibraryQualityDecider.DetectQuality(raw);
}

public sealed record MediaWantedDecisionInput(
    string MediaType,
    bool HasFile,
    string? CurrentQuality,
    string? CutoffQuality,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems);

public static class MediaDecisionRules
{
    public static LibraryQualityDecision DecideWantedState(MediaWantedDecisionInput input)
        => LibraryQualityDecider.Decide(
            mediaLabel: NormalizeMediaType(input.MediaType) == "tv" ? "TV show" : "movie",
            hasFile: input.HasFile,
            currentQuality: input.CurrentQuality,
            cutoffQuality: input.CutoffQuality,
            upgradeUntilCutoff: input.UpgradeUntilCutoff,
            upgradeUnknownItems: input.UpgradeUnknownItems);

    public static string NormalizeMediaType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() is "tv" or "series" or "shows" ? "tv" : "movies";
}
