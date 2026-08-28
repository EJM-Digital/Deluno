using Deluno.Connections.Contracts;

namespace Deluno.Connections.Data;

/// <summary>
/// The configured subtitle sources — which are on, in what order, and with whose
/// account.
///
/// <para>Deliberately small. What each provider *is* — its name, what it needs,
/// what it can answer for — is code Deluno ships and is read from the provider
/// registry, not from here. This stores only the part a person decided.</para>
/// </summary>
public interface ISubtitleProviderRepository
{
    Task<IReadOnlyList<SubtitleProviderConnection>> ListAsync(CancellationToken cancellationToken);

    Task<SubtitleProviderConnection> SaveAsync(
        string providerKey,
        string displayName,
        SaveSubtitleProviderRequest request,
        CancellationToken cancellationToken);

    Task RecordHealthAsync(
        string providerKey,
        string status,
        string? message,
        int? latencyMs,
        bool success,
        DateTimeOffset? rateLimitedUntilUtc,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string providerKey, CancellationToken cancellationToken);
}
