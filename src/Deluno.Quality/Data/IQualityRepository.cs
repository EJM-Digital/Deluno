using Deluno.Quality.Contracts;

namespace Deluno.Quality.Data;

public interface IQualityRepository
{
    Task<IReadOnlyList<QualityProfileItem>> ListQualityProfilesAsync(CancellationToken cancellationToken);
    Task ReorderQualityProfilesAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);

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

    Task<QualityProfileItem> CreateQualityProfileFromPresetAsync(
        string presetId,
        string? nameOverride,
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

    Task<PolicySetItem?> UpdatePolicySetAsync(
        string id,
        UpdatePolicySetRequest request,
        CancellationToken cancellationToken,
        string changeKind = "update",
        string? expectedPlanHash = null);

    Task<bool> DeleteQualityProfileAsync(string id, CancellationToken cancellationToken);
    Task<bool> DeleteCustomFormatAsync(string id, CancellationToken cancellationToken);
    Task<bool> DeletePolicySetAsync(string id, CancellationToken cancellationToken);
}
