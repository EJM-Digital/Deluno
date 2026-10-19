using System.Net;

namespace Deluno.Connections;

/// <summary>What qBittorrent said to a sign-in.</summary>
/// <param name="SignedIn">The session is signed in and can be used.</param>
/// <param name="Rejected">qBittorrent answered, and the answer was "no" (wrong login, or an answer that did not sign in).</param>
/// <param name="Status">The last HTTP status it answered with.</param>
public sealed record QbittorrentSignInResult(bool SignedIn, bool Rejected, HttpStatusCode Status, bool Banned = false);

/// <summary>
/// The one way Deluno signs in to qBittorrent, used by the connection test and by every
/// later call (categories, free space, grabs, queue), so a login that passes the test
/// cannot then be refused by the checks that use it (#1061).
///
/// <para>qBittorrent 4.x answers the sign-in with 200 and a body of <c>Ok.</c> or
/// <c>Fails.</c>. Later releases (5.1 changed the add answer, 5.2 added API keys) do not all
/// keep that body, and a login can be accepted while the body says something else. So the
/// body is trusted only when it says <c>Ok.</c> or <c>Fails.</c>; any other answer is settled
/// by asking for the version with the session just made, which is what the connection test
/// itself does. A wrong login is never mistaken for a right one: the version call refuses it.</para>
///
/// <para>Right after an install qBittorrent may still be starting its web page, so a
/// rejection or a server error is tried once more after a short pause before it is reported.
/// A refusal (401 or 403, for example a ban after repeated wrong logins) is not retried.</para>
/// </summary>
public static class QbittorrentSignIn
{
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(750);

    public static async Task<QbittorrentSignInResult> SignInAsync(
        HttpClient http,
        string? username,
        string? secret,
        CancellationToken cancellationToken,
        TimeSpan? retryDelay = null)
    {
        QbittorrentSignInResult result = new(false, false, 0);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(retryDelay ?? DefaultRetryDelay, cancellationToken);
            }

            result = await TryOnceAsync(http, username, secret, cancellationToken);
            if (result.SignedIn || result.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return result;
            }
        }

        return result;
    }

    private static async Task<QbittorrentSignInResult> TryOnceAsync(HttpClient http, string? username, string? secret, CancellationToken cancellationToken)
    {
        using var body = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("username", username ?? string.Empty),
            new KeyValuePair<string, string>("password", secret ?? string.Empty)
        ]);
        using var response = await http.PostAsync("api/v2/auth/login", body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // #1362: a 403 that says the address is banned is a ban (it lifts by itself), not a wrong password.
            var refusal = response.StatusCode == HttpStatusCode.Forbidden
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;
            return new(false, false, response.StatusCode, refusal.Contains("banned", StringComparison.OrdinalIgnoreCase));
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (text.Contains("Fails", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, true, response.StatusCode);
        }

        if (text.Contains("Ok.", StringComparison.OrdinalIgnoreCase))
        {
            return new(true, false, response.StatusCode);
        }

        using var version = await http.GetAsync("api/v2/app/version", cancellationToken);
        return version.IsSuccessStatusCode
            ? new(true, false, response.StatusCode)
            : new(false, true, version.StatusCode);
    }
}
