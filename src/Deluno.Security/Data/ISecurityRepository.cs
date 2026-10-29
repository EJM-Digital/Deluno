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

    /// <summary>
    /// The single account Deluno is single-user for (#993): used to give a
    /// local-network request that skips sign-in the same identity a real
    /// sign-in would have, without asking which user -- there is only ever
    /// one. Null before the first user is bootstrapped.
    /// </summary>
    Task<UserItem?> GetSoleUserAsync(CancellationToken cancellationToken);

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

    /// <summary>Whether this is the current password of the user (#1433).</summary>
    Task<bool> VerifyUserPasswordAsync(string userId, string password, CancellationToken cancellationToken);

    /// <summary>How many recovery codes are left, and whether any were ever made (#1392).</summary>
    Task<RecoveryCodesStatus> GetRecoveryCodesStatusAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Makes a new set of recovery codes, voiding every earlier one. Only their hashes are stored.</summary>
    Task<IReadOnlyList<string>> CreateRecoveryCodesAsync(string userId, CancellationToken cancellationToken);

    /// <summary>The owner of an unused recovery code, without using it up; null for a code that is wrong or already used.</summary>
    Task<UserItem?> FindUserByRecoveryCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>Uses the code up and sets a new password, signing every session out. Null when the code is wrong or used.</summary>
    Task<UserItem?> ResetPasswordWithRecoveryCodeAsync(string code, string newPassword, CancellationToken cancellationToken);

}
