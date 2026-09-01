using Deluno.Quality.ReleasePreferences;
using System.Text.Json.Serialization;

namespace Deluno.Quality.Playback;

public static class PlaybackGoalModes
{
    public const string EveryDevice = "every-device";
    public const string PrimaryDevice = "primary-device";
    public const string Fallback = "fallback";

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            PrimaryDevice => PrimaryDevice,
            Fallback => Fallback,
            _ => EveryDevice
        };
}

public static class PlaybackCapabilityStates
{
    public const string Present = "present";
    public const string Absent = "absent";
    public const string Unknown = "unknown";
    public const string Conflicting = "conflicting";

    public static PreferenceFactState Parse(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Absent => PreferenceFactState.Absent,
            Unknown => PreferenceFactState.Unknown,
            Conflicting => PreferenceFactState.Conflicting,
            _ => PreferenceFactState.Present
        };

    public static string Normalize(string? value)
        => Parse(value) switch
        {
            PreferenceFactState.Absent => Absent,
            PreferenceFactState.Unknown => Unknown,
            PreferenceFactState.Conflicting => Conflicting,
            _ => Present
        };
}

public static class PlaybackCapabilitySources
{
    public const string User = "user";
    public const string Template = "template";
    public const string VerifiedDiscovery = "verified-discovery";

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "owner" or "user" or "assertion" => User,
            Template => Template,
            "verified" or "discovery" or VerifiedDiscovery => VerifiedDiscovery,
            _ => User
        };
}

/// <summary>
/// One owner assertion about a playback capability. A missing entry is not a
/// negative capability; it stays unknown until the owner or a probe supplies
/// evidence.
/// </summary>
public sealed record PlaybackCapability(
    string TraitId,
    string State = PlaybackCapabilityStates.Present,
    string Source = PlaybackCapabilitySources.User,
    double? Confidence = 1,
    string? Detail = null,
    DateTimeOffset? LastConfirmedUtc = null);

public sealed record PlaybackDeviceProfile(
    string Id,
    string Name,
    IReadOnlyList<PlaybackCapability> Capabilities,
    bool IsEnabled,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record PlaybackDeviceGroup(
    string Id,
    string Name,
    string Mode,
    IReadOnlyList<string> DeviceProfileIds,
    string? PrimaryDeviceProfileId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record PlaybackGoalItem(
    string Id,
    string Name,
    string MediaType,
    string DeviceGroupId,
    bool MustPlay,
    IReadOnlyList<string> RequiredTraitIds,
    IReadOnlyList<IReadOnlyList<string>> RequiredAnyTraitGroups,
    IReadOnlyList<string> PreferredTraitIds,
    string? StopWhenTraitId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<string>? ForbiddenTraitIds = null)
{
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveForbiddenTraitIds
        => ForbiddenTraitIds ?? [];
}

public sealed record CreatePlaybackDeviceProfileRequest(
    string? Name,
    IReadOnlyList<PlaybackCapability>? Capabilities,
    bool IsEnabled = true);

public sealed record UpdatePlaybackDeviceProfileRequest(
    string? Name,
    IReadOnlyList<PlaybackCapability>? Capabilities,
    bool IsEnabled = true);

public sealed record CreatePlaybackDeviceGroupRequest(
    string? Name,
    string? Mode,
    IReadOnlyList<string>? DeviceProfileIds,
    string? PrimaryDeviceProfileId);

public sealed record UpdatePlaybackDeviceGroupRequest(
    string? Name,
    string? Mode,
    IReadOnlyList<string>? DeviceProfileIds,
    string? PrimaryDeviceProfileId);

public sealed record CreatePlaybackGoalRequest(
    string? Name,
    string? MediaType,
    string? DeviceGroupId,
    bool MustPlay,
    IReadOnlyList<string>? RequiredTraitIds,
    IReadOnlyList<IReadOnlyList<string>>? RequiredAnyTraitGroups,
    IReadOnlyList<string>? PreferredTraitIds,
    string? StopWhenTraitId,
    IReadOnlyList<string>? ForbiddenTraitIds = null);

public sealed record UpdatePlaybackGoalRequest(
    string? Name,
    string? MediaType,
    string? DeviceGroupId,
    bool MustPlay,
    IReadOnlyList<string>? RequiredTraitIds,
    IReadOnlyList<IReadOnlyList<string>>? RequiredAnyTraitGroups,
    IReadOnlyList<string>? PreferredTraitIds,
    string? StopWhenTraitId,
    IReadOnlyList<string>? ForbiddenTraitIds = null);

public sealed record PlaybackGoalCompilation(
    PlaybackGoalItem Goal,
    PlaybackDeviceGroup? Group,
    IReadOnlyList<PlaybackDeviceProfile> SelectedDevices,
    ReleasePreferencePlan Plan,
    string PlanHash,
    IReadOnlyList<string> UnknownCapabilities,
    IReadOnlyList<string> Warnings,
    bool RequiresReview);
