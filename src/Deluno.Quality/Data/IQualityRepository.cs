using Deluno.Quality.Contracts;

namespace Deluno.Quality.Data;

public interface IQualityRepository
{
    Task<IReadOnlyList<QualityProfileItem>> ListQualityProfilesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomFormatItem>> ListCustomFormatsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PolicySetItem>> ListPolicySetsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaPlanVersionItem>> ListMediaPlanVersionsAsync(
        string planId,
        CancellationToken cancellationToken);

    Task<MediaPlanVersionItem?> GetMediaPlanVersionAsync(
        string planId,
        int version,
        CancellationToken cancellationToken);

    Task<MediaPlanVersionItem?> GetLatestMediaPlanVersionAsync(
        string planId,
        CancellationToken cancellationToken);

    Task<QualityProfileItem> CreateQualityProfileAsync(
        CreateQualityProfileRequest request,
        CancellationToken cancellationToken);

    Task<CustomFormatItem> CreateCustomFormatAsync(
        CreateCustomFormatRequest request,
        CancellationToken cancellationToken,
        string? preferredId = null);

    Task<PolicySetItem> CreatePolicySetAsync(
        CreatePolicySetRequest request,
        CancellationToken cancellationToken);

    Task<QualityProfileItem?> UpdateQualityProfileAsync(
        string id,
        UpdateQualityProfileRequest request,
        CancellationToken cancellationToken);

    Task<CustomFormatItem?> UpdateCustomFormatAsync(
        string id,
        UpdateCustomFormatRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces what a rule is - its name, guide identity and conditions - and
    /// keeps what the owner chose: its id (so every profile answer about it
    /// stays attached), score and media type. Unlike an update this can clear
    /// the guide identity. Returns null when the rule is gone or its conditions
    /// are no longer <paramref name="expectedConditions"/>, so a definition
    /// the owner edited after a preview is never overwritten.
    /// </summary>
    Task<CustomFormatItem?> ReplaceCustomFormatDefinitionAsync(
        string id,
        string name,
        string? trashId,
        string conditions,
        string expectedConditions,
        CancellationToken cancellationToken);

    Task<PolicySetItem?> UpdatePolicySetAsync(
        string id,
        UpdatePolicySetRequest request,
        CancellationToken cancellationToken,
        string changeKind = "update",
        string? expectedPlanHash = null);

    Task<bool> DeleteQualityProfileAsync(string id, CancellationToken cancellationToken);

    /// <summary>Deletes the profile after clearing every library, title and other setting that names it; null when there is no such profile (#1334).</summary>
    Task<QualityProfileRemoval?> RemoveQualityProfileAsync(string id, CancellationToken cancellationToken);
    Task<bool> DeleteCustomFormatAsync(string id, CancellationToken cancellationToken);
    Task<bool> DeletePolicySetAsync(string id, CancellationToken cancellationToken);
}

/// <summary>What deleting a quality profile cleared: libraries and titles that named it, and other settings (an intake list, a request-app mapping, a media plan).</summary>
public sealed record QualityProfileRemoval(string Id, string Name, int Libraries, int Titles, int Other);

public enum ProfileReferenceKind { Library, Title, Other }

/// <summary>One column in one database that stores a quality profile's id.</summary>
public sealed record ProfileReferenceColumn(string Database, string Table, string Column, ProfileReferenceKind Kind);
