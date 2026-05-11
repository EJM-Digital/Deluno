using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

public interface IPlatformSettingsRepository
{
    Task<bool> HasUsersAsync(CancellationToken cancellationToken);
    Task<bool> RequiresBootstrapAsync(CancellationToken cancellationToken);

    Task<PlatformSettingsSnapshot> GetAsync(CancellationToken cancellationToken);

    Task<string?> GetMetadataProviderSecretAsync(string provider, CancellationToken cancellationToken);

    Task<PlatformSettingsSnapshot> SaveAsync(
        UpdatePlatformSettingsRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LibraryItem>> ListLibrariesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<QualityProfileItem>> ListQualityProfilesAsync(CancellationToken cancellationToken);
    Task ReorderQualityProfilesAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);

    Task<IReadOnlyList<TagItem>> ListTagsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<IntakeSourceItem>> ListIntakeSourcesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomFormatItem>> ListCustomFormatsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DestinationRuleItem>> ListDestinationRulesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PolicySetItem>> ListPolicySetsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<LibraryViewItem>> ListLibraryViewsAsync(string userId, string variant, CancellationToken cancellationToken);

    Task<UserItem?> ValidateUserCredentialsAsync(
        string username,
        string password,
        CancellationToken cancellationToken);

    Task<UserItem?> GetUserByIdAsync(
        string id,
        CancellationToken cancellationToken);

    Task<bool> ChangeUserPasswordAsync(
        string userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken);

    Task<bool> RevokeUserAccessTokensAsync(
        string userId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ApiKeyItem>> ListApiKeysAsync(CancellationToken cancellationToken);

    Task<CreatedApiKeyResponse> CreateApiKeyAsync(
        CreateApiKeyRequest request,
        CancellationToken cancellationToken);

    Task<ApiKeyItem?> ValidateApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken);

    Task<bool> DeleteApiKeyAsync(string id, CancellationToken cancellationToken);

    Task<UserItem> BootstrapUserAsync(
        BootstrapUserRequest request,
        CancellationToken cancellationToken);

    Task<QualityProfileItem> CreateQualityProfileAsync(
        CreateQualityProfileRequest request,
        CancellationToken cancellationToken);

    Task<TagItem> CreateTagAsync(
        CreateTagRequest request,
        CancellationToken cancellationToken);

    Task<IntakeSourceItem> CreateIntakeSourceAsync(
        CreateIntakeSourceRequest request,
        CancellationToken cancellationToken);

    Task<CustomFormatItem> CreateCustomFormatAsync(
        CreateCustomFormatRequest request,
        CancellationToken cancellationToken);
    Task<DestinationRuleItem> CreateDestinationRuleAsync(
        CreateDestinationRuleRequest request,
        CancellationToken cancellationToken);
    Task<PolicySetItem> CreatePolicySetAsync(
        CreatePolicySetRequest request,
        CancellationToken cancellationToken);
    Task<LibraryViewItem> CreateLibraryViewAsync(
        string userId,
        CreateLibraryViewRequest request,
        CancellationToken cancellationToken);

    Task<QualityProfileItem?> UpdateQualityProfileAsync(
        string id,
        UpdateQualityProfileRequest request,
        CancellationToken cancellationToken);

    Task<TagItem?> UpdateTagAsync(
        string id,
        UpdateTagRequest request,
        CancellationToken cancellationToken);

    Task<IntakeSourceItem?> UpdateIntakeSourceAsync(
        string id,
        UpdateIntakeSourceRequest request,
        CancellationToken cancellationToken);

    Task<CustomFormatItem?> UpdateCustomFormatAsync(
        string id,
        UpdateCustomFormatRequest request,
        CancellationToken cancellationToken);
    Task<DestinationRuleItem?> UpdateDestinationRuleAsync(
        string id,
        UpdateDestinationRuleRequest request,
        CancellationToken cancellationToken);
    Task<PolicySetItem?> UpdatePolicySetAsync(
        string id,
        UpdatePolicySetRequest request,
        CancellationToken cancellationToken);
    Task<LibraryViewItem?> UpdateLibraryViewAsync(
        string userId,
        string id,
        UpdateLibraryViewRequest request,
        CancellationToken cancellationToken);

    Task<LibraryItem> CreateLibraryAsync(
        CreateLibraryRequest request,
        CancellationToken cancellationToken);

    Task<LibraryItem?> UpdateLibraryAutomationAsync(
        string id,
        UpdateLibraryAutomationRequest request,
        CancellationToken cancellationToken);

    Task<LibraryItem?> UpdateLibraryQualityProfileAsync(
        string id,
        UpdateLibraryQualityProfileRequest request,
        CancellationToken cancellationToken);

    Task<LibraryItem?> UpdateLibraryWorkflowAsync(
        string id,
        UpdateLibraryWorkflowRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectionItem>> ListConnectionsAsync(CancellationToken cancellationToken);

    Task<ConnectionItem> CreateConnectionAsync(
        CreateConnectionRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexerItem>> ListIndexersAsync(CancellationToken cancellationToken);

    Task<IndexerItem> CreateIndexerAsync(
        CreateIndexerRequest request,
        CancellationToken cancellationToken);

    Task<IndexerItem?> UpdateIndexerAsync(
        string id,
        UpdateIndexerRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadClientItem>> ListDownloadClientsAsync(CancellationToken cancellationToken);

    Task<DownloadClientItem> CreateDownloadClientAsync(
        CreateDownloadClientRequest request,
        CancellationToken cancellationToken);

    Task<DownloadClientItem?> UpdateDownloadClientAsync(
        string id,
        UpdateDownloadClientRequest request,
        CancellationToken cancellationToken);

    Task<LibraryRoutingSnapshot?> GetLibraryRoutingAsync(string libraryId, CancellationToken cancellationToken);

    Task<LibraryRoutingSnapshot?> SaveLibraryRoutingAsync(
        string libraryId,
        UpdateLibraryRoutingRequest request,
        CancellationToken cancellationToken);

    Task<IndexerTestResult?> UpdateIndexerHealthAsync(
        string id,
        string healthStatus,
        string message,
        string? failureCategory,
        int? latencyMs,
        CancellationToken cancellationToken);

    Task<IndexerTestResult?> UpdateDownloadClientHealthAsync(
        string id,
        string healthStatus,
        string message,
        string? failureCategory,
        int? latencyMs,
        CancellationToken cancellationToken);

    Task<bool> DeleteLibraryAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteConnectionAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteIndexerAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteDownloadClientAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteQualityProfileAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteTagAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteIntakeSourceAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteCustomFormatAsync(string id, CancellationToken cancellationToken);
    Task<bool> DeleteDestinationRuleAsync(string id, CancellationToken cancellationToken);
    Task<bool> DeletePolicySetAsync(string id, CancellationToken cancellationToken);
    Task<bool> DeleteLibraryViewAsync(string userId, string id, CancellationToken cancellationToken);

}
