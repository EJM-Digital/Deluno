using System.Net;

namespace Deluno.Connections;

/// <summary>What a download client was given that it refused (#1251).</summary>
public enum DownloadClientCredential
{
    /// <summary>An API key (SABnzbd, or qBittorrent signed in with a key).</summary>
    ApiKey,

    /// <summary>A username and a password (qBittorrent, NZBGet, Transmission).</summary>
    UsernameAndPassword,

    /// <summary>Only a password (Deluge's web page).</summary>
    Password
}

/// <summary>
/// The one sentence for a key or login a download client refused, whichever client and whichever call (an action, a
/// removal, the connection test). A client's own status ("returned 403") says nothing to a person; this names the client,
/// what it refused, where the value lives, and the check that finds the fault.
/// </summary>
public static class DownloadClientCredentialText
{
    /// <summary>The check that tells you where the fault is.</summary>
    public const string WhereToCheck = "Use Test connection on the client in Deluno's Settings to check it.";

    public static string Refused(string clientName, DownloadClientCredential kind)
        => kind switch
        {
            DownloadClientCredential.ApiKey => $"{clientName} refused Deluno's API key; check it in the client's settings. {WhereToCheck}",
            DownloadClientCredential.Password => $"{clientName} refused Deluno's password; check it in the client's settings. {WhereToCheck}",
            _ => $"{clientName} refused Deluno's username and password; check them in the client's settings. {WhereToCheck}"
        };

    /// <summary>Whether a status is a client saying "not with that key or login".</summary>
    public static bool IsRefusal(HttpStatusCode status)
        => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
