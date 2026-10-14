using System.Net;
using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// A download client refused the key or login Deluno gave it (#1251). Thrown from wherever the refusal is seen, so it
/// never reads as "could not be asked" or "returned 403", and turned into one plain failure at the edge of the action.
/// </summary>
internal sealed class ClientCredentialsRefusedException(string message) : InvalidOperationException(message);

internal static class ClientCredentials
{
    public static ClientCredentialsRefusedException Refused(string clientName, DownloadClientCredential kind)
        => new(DownloadClientCredentialText.Refused(clientName, kind));

    /// <summary>Throws when the client answered "not with that key or login".</summary>
    public static void ThrowIfRefused(HttpResponseMessage response, string clientName, DownloadClientCredential kind)
        => ThrowIfRefused(response.StatusCode, clientName, kind);

    public static void ThrowIfRefused(HttpStatusCode? status, string clientName, DownloadClientCredential kind)
    {
        if (status is { } code && DownloadClientCredentialText.IsRefusal(code))
        {
            throw Refused(clientName, kind);
        }
    }

    /// <summary>Throws, naming the client by its protocol's own credential, when it answered "not with that key or login".</summary>
    public static void ThrowIfRefused(HttpResponseMessage response, DownloadClientItem client)
    {
        if (DownloadClientCredentialText.IsRefusal(response.StatusCode))
        {
            var (name, kind) = Describe(client);
            throw Refused(name, kind);
        }
    }

    /// <summary>For a catch: turns an HTTP failure that is a refusal into the plain exception; otherwise lets it through.</summary>
    public static bool IsRefusal(Exception exception)
        => exception is ClientCredentialsRefusedException
           || (exception is HttpRequestException { StatusCode: { } code } && DownloadClientCredentialText.IsRefusal(code));

    /// <summary>
    /// What a client's protocol takes as its credential: SABnzbd an API key, Deluge a password, qBittorrent an API key when
    /// no username is set (it is then sent as a bearer token) and a username and password otherwise, and the rest a login.
    /// </summary>
    public static (string Name, DownloadClientCredential Kind) Describe(DownloadClientItem client)
        => client.Protocol.ToLowerInvariant() switch
        {
            "sabnzbd" => ("SABnzbd", DownloadClientCredential.ApiKey),
            "deluge" => ("Deluge", DownloadClientCredential.Password),
            "qbittorrent" => ("qBittorrent", string.IsNullOrWhiteSpace(client.Username) ? DownloadClientCredential.ApiKey : DownloadClientCredential.UsernameAndPassword),
            "nzbget" => ("NZBGet", DownloadClientCredential.UsernameAndPassword),
            "transmission" => ("Transmission", DownloadClientCredential.UsernameAndPassword),
            "utorrent" => ("uTorrent", DownloadClientCredential.UsernameAndPassword),
            "rtorrent" => ("rTorrent", DownloadClientCredential.UsernameAndPassword),
            _ => (client.Name, DownloadClientCredential.UsernameAndPassword)
        };

    /// <summary>
    /// The plain sentence when this exception is a client refusing the key or login (thrown as such, or an HTTP 401 or 403),
    /// else null. One place, so an action, a snapshot and a check all word it the same way.
    /// </summary>
    public static string? MessageFor(DownloadClientItem client, Exception exception)
    {
        if (exception is ClientCredentialsRefusedException refused)
        {
            return refused.Message;
        }

        if (IsRefusal(exception))
        {
            var (name, kind) = Describe(client);
            return DownloadClientCredentialText.Refused(name, kind);
        }

        return null;
    }

    public static DownloadClientActionResult Failure(DownloadClientItem client, string queueItemId, string action, string message)
        => DownloadClientHelpers.ActionFailure(client, queueItemId, action, message, category: "auth");

    public static DownloadClientActionResult Failure(DownloadClientItem client, string queueItemId, string action, ClientCredentialsRefusedException refused)
        => Failure(client, queueItemId, action, refused.Message);

    /// <summary>#1312: a grab the client refused for its key or login, as a failed result rather than an exception.</summary>
    public static DownloadClientGrabResult GrabFailure(DownloadClientItem client, DownloadClientGrabRequest request, string message)
        => new(client.Id, request.ReleaseName, false, "failed", message)
        {
            Failure = IntegrationFailureFactory.FromLegacy("download-client", client.Id, client.Name, "grab", "auth", message)
        };
}
