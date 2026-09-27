using Deluno.Connections.Data;

namespace Deluno.Integrations.Trakt;

/// <summary>A ready-to-use bearer token and the Client ID the trakt-api-key header needs alongside it.</summary>
public sealed record TraktAuthContext(string ClientId, string AccessToken);

/// <summary>
/// The one thing a caller wanting to read Trakt actually needs: "give me a
/// token that works right now" (#846). Refreshes silently while the refresh
/// token still works; once it stops working, every caller sees the same
/// "not connected" (null) rather than each inventing its own retry, and the
/// connection is marked for the Lists screen to surface as needing a
/// reconnect.
/// </summary>
public interface ITraktTokenProvider
{
    /// <summary>Null when Trakt is not connected, or the refresh token has stopped working.</summary>
    Task<TraktAuthContext?> GetAuthContextAsync(CancellationToken cancellationToken);
}

public sealed class TraktTokenProvider(
    ITraktConnectionRepository repository,
    ITraktClient client,
    TimeProvider timeProvider)
    : ITraktTokenProvider
{
    /// <summary>Refreshed a little before the stated expiry, so a call in flight is never handed a token that dies mid-request.</summary>
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    public async Task<TraktAuthContext?> GetAuthContextAsync(CancellationToken cancellationToken)
    {
        var credentials = await repository.GetClientCredentialsAsync(cancellationToken);
        if (credentials is null)
        {
            return null;
        }

        var tokens = await repository.GetTokensAsync(cancellationToken);
        if (tokens is null)
        {
            return null;
        }

        if (tokens.ExpiresUtc - RefreshBuffer > timeProvider.GetUtcNow())
        {
            return new TraktAuthContext(credentials.ClientId, tokens.AccessToken);
        }

        var refreshed = await client.RefreshAsync(credentials.ClientId, credentials.ClientSecret, tokens.RefreshToken, cancellationToken);
        if (refreshed is null)
        {
            await repository.MarkNeedsReconnectAsync(cancellationToken);
            return null;
        }

        var expiresUtc = timeProvider.GetUtcNow().AddSeconds(refreshed.ExpiresInSeconds);
        await repository.SaveTokensAsync(refreshed.AccessToken, refreshed.RefreshToken, expiresUtc, cancellationToken);
        return new TraktAuthContext(credentials.ClientId, refreshed.AccessToken);
    }
}
