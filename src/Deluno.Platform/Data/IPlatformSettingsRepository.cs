using Deluno.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

public interface IPlatformSettingsRepository
{
    Task<PlatformSettingsSnapshot> GetAsync(CancellationToken cancellationToken);

    Task<SetupProgressItem> GetSetupProgressAsync(CancellationToken cancellationToken);

    Task<SetupProgressItem> SaveSetupProgressAsync(
        UpdateSetupProgressRequest request,
        CancellationToken cancellationToken);

    Task<SetupDraftItem> GetSetupDraftAsync(CancellationToken cancellationToken);

    Task<SetupDraftItem> SaveSetupDraftAsync(
        UpdateSetupDraftRequest request,
        CancellationToken cancellationToken);

    Task ClearSetupDraftAsync(CancellationToken cancellationToken);

    Task<string?> GetMetadataProviderSecretAsync(string provider, CancellationToken cancellationToken);

    Task<PlatformSettingsSnapshot> SaveAsync(
        UpdatePlatformSettingsRequest request,
        CancellationToken cancellationToken);

    Task<PlatformSettingsSnapshot> SetGlobalAutomationEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken);

    Task<PlatformSettingsSnapshot> MarkWorkflowVerifiedAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TagItem>> ListTagsAsync(CancellationToken cancellationToken);

    Task<TagItem> CreateTagAsync(
        CreateTagRequest request,
        CancellationToken cancellationToken);

    Task<TagItem?> UpdateTagAsync(
        string id,
        UpdateTagRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteTagAsync(string id, CancellationToken cancellationToken);

}
