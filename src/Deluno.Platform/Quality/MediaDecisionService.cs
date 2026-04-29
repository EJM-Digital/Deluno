using Deluno.Infrastructure.Observability;

namespace Deluno.Platform.Quality;

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
    bool UpgradeUnknownItems);

public static class MediaDecisionRules
{
    private static readonly IVersionedMediaPolicyEngine Engine = new VersionedMediaPolicyEngine();

    public static LibraryQualityDecision DecideWantedState(MediaWantedDecisionInput input)
        => Engine.DecideWantedState(input);

    public static string NormalizeMediaType(string? mediaType)
        => MediaPolicyCatalog.NormalizeMediaType(mediaType);
}
