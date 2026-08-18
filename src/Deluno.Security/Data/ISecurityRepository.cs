using Deluno.Security.Contracts;

namespace Deluno.Security.Data;

/// <summary>
/// Users, credentials and API keys. Carved out of
/// <c>IPlatformSettingsRepository</c> by ADR-001 Step 1; the signatures are
/// unchanged so call sites only swap the injected type.
/// </summary>
public interface ISecurityRepository
{
    Task<bool> HasUsersAsync(CancellationToken cancellationToken);

    Task<bool> RequiresBootstrapAsync(CancellationToken cancellationToken);

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
}
