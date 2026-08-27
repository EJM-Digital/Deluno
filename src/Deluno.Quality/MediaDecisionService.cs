using Deluno.Infrastructure.Observability;

namespace Deluno.Quality;

public interface IMediaDecisionService
{
    LibraryQualityDecision DecideWantedState(MediaWantedDecisionInput input);

    string? DetectQuality(string? raw);

    string CurrentPolicyVersion { get; }
}

public sealed class MediaDecisionService(IVersionedMediaPolicyEngine policyEngine) : IMediaDecisionService
{
    public string CurrentPolicyVersion => policyEngine.CurrentVersion;

    public LibraryQualityDecision DecideWantedState(MediaWantedDecisionInput input)
    {
        var decision = policyEngine.DecideWantedState(input);
        DelunoObservability.DecisionOutcomes.Add(
            1,
            new("media.type", MediaPolicyCatalog.NormalizeMediaType(input.MediaType)),
            new("wanted.status", decision.WantedStatus),
            new("policy.version", decision.PolicyVersion),
            new("has.file", input.HasFile));
        return decision;
    }

    public string? DetectQuality(string? raw)
        => policyEngine.DetectQuality(raw);
}

public sealed record MediaWantedDecisionInput(
    string MediaType,
    bool HasFile,
    string? CurrentQuality,
    string? CutoffQuality,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems,
    /// <summary>
    /// Whether the title is out yet — released, or aired.
    ///
    /// Without this, a movie added six months before release was stored as
    /// Missing and counted against the library from the day it was added, and
    /// every search cycle went looking for something that did not exist. It
    /// defaults to true because a caller that does not know a release date
    /// should search rather than sit on its hands, which is what every caller
    /// did before this existed.
    /// </summary>
    bool IsReleased = true);

public static class MediaDecisionRules
{
    private static readonly IVersionedMediaPolicyEngine Engine = new VersionedMediaPolicyEngine();

    public static LibraryQualityDecision DecideWantedState(MediaWantedDecisionInput input)
        => Engine.DecideWantedState(input);

    public static string NormalizeMediaType(string? mediaType)
        => MediaPolicyCatalog.NormalizeMediaType(mediaType);
}
