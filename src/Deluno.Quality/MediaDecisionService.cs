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
    bool IsReleased = true,
    /// <summary>
    /// How big the file actually is, and how big its tier says it should be.
    ///
    /// <para><b>Why the decision needs them.</b> "Quality met" was decided from
    /// the tier's <i>name</i> alone: a file labelled WEB 2160p met a WEB 2160p
    /// cutoff whatever was inside it. On the rig that meant a 0.06&#160;GB file
    /// sat there marked Quality met against a rule saying a 2160p file is 7–60
    /// GB, and Deluno was content.</para>
    ///
    /// <para>James: <i>"if there are files that are already under the rules due
    /// to a library import or something then its up to deluno to use the upgrade
    /// process as the standard process."</i> Which is right — a file too small
    /// for its own label is not a finished title, it is a bad copy, and Deluno
    /// already knows how to replace those. It does not need a report and a
    /// person; it needs to be Upgradable.</para>
    ///
    /// <para>Both null means the caller does not know, and a decision is made
    /// exactly as it was before — never a guess that the file is fine.</para>
    /// </summary>
    long? FileSizeBytes = null,
    long? SizeFloorBytes = null);

public static class MediaDecisionRules
{
    private static readonly IVersionedMediaPolicyEngine Engine = new VersionedMediaPolicyEngine();

    public static LibraryQualityDecision DecideWantedState(MediaWantedDecisionInput input)
        => Engine.DecideWantedState(input);

    public static string NormalizeMediaType(string? mediaType)
        => MediaPolicyCatalog.NormalizeMediaType(mediaType);
}
