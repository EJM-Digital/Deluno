using Deluno.Connections.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// The folder a torrent client without categories of its own is told to save
/// a grab into (#1073). Transmission has labels but no folder per label, and
/// Deluge's label only has a folder when someone set one by hand, so what
/// they did with a finished download depended on their own settings, not on
/// the library that asked for it. Deluno already knows the folder each
/// library's downloads finish in, so it names it on every grab.
/// </summary>
public static class ClientSaveFolders
{
    /// <summary>How a library's folder was reached: as this host writes it, and as the client must be told.</summary>
    /// <param name="ClientFolder">Null when the client is on another machine and no path mapping covers the folder: Deluno cannot name a folder that client can resolve, so it names none.</param>
    public sealed record Resolved(string HostFolder, string? ClientFolder);

    public const string NeedsMappingMessage =
        "Deluno can't name this folder for a client on another machine without a path mapping. Add one for the library's downloads folder, and Deluno will send each download there.";

    public enum PathStyle { Unknown, Windows, Posix }

    /// <summary>Drive-letter or UNC paths are Windows style, a leading slash is POSIX style, anything else (a relative name) is unknown.</summary>
    public static PathStyle StyleOf(string? path)
    {
        var value = path?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return PathStyle.Unknown;
        }

        if (value.StartsWith("\\\\", StringComparison.Ordinal) || (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':'))
        {
            return PathStyle.Windows;
        }

        return value[0] == '/' ? PathStyle.Posix : PathStyle.Unknown;
    }

    /// <summary>True when Deluno itself runs in a container (its folders are not the client's even on the same host).</summary>
    public static bool RunningInContainer()
        => string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an unmapped host folder can be named to a client Deluno believes
    /// is on this machine (#1073). "Localhost" is not proof: a client in Docker
    /// Desktop or WSL2, or a container using the host network, answers on the
    /// same address yet has its own file system. So the client's own reported
    /// folder must be written in the same path style as the host folder, and a
    /// Deluno running in a container never shares its folders without a mapping.
    /// </summary>
    public static bool CanShareHostFolder(bool sameMachine, bool delunoInContainer, string hostFolder, string? clientReportedPath)
    {
        if (!sameMachine || delunoInContainer)
        {
            return false;
        }

        var clientStyle = StyleOf(clientReportedPath);
        var hostStyle = StyleOf(hostFolder);
        return clientStyle == PathStyle.Unknown || hostStyle == PathStyle.Unknown || clientStyle == hostStyle;
    }

    /// <summary>True when the client runs on this machine: localhost, a loopback or local address, or this computer's own name.</summary>
    public static bool IsSameMachine(DownloadClientItem client)
    {
        var host = client.Host?.Trim();
        if (string.IsNullOrWhiteSpace(host) && Uri.TryCreate(client.EndpointUrl, UriKind.Absolute, out var endpoint))
        {
            host = endpoint.Host;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || host.Equals(System.Net.Dns.GetHostName(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!System.Net.IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        if (System.Net.IPAddress.IsLoopback(address))
        {
            return true;
        }

        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Any(unicast => unicast.Address.Equals(address));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The one downloads folder the libraries routed to this client for this
    /// kind of media and category agree on, or null when there is none or they
    /// disagree (two libraries sharing one category cannot both be right, so
    /// Deluno names nothing and the client's own setting applies).
    /// </summary>
    public static async Task<Resolved?> ResolveAsync(
        ILibrariesRepository libraries,
        DownloadClientItem client,
        string mediaType,
        string category,
        IReadOnlyList<DownloadClientPathMappingItem> mappings,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<string?>>? clientReportedPath = null)
    {
        var folders = new List<string>();
        foreach (var library in (await libraries.ListLibrariesAsync(cancellationToken))
                     .Where(item => string.Equals(item.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)))
        {
            var routing = await libraries.GetLibraryRoutingAsync(library.Id, cancellationToken);
            var routed = routing?.DownloadClients.Any(link =>
                string.Equals(link.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(RoutedCategory(client, library, link.Category), category, StringComparison.OrdinalIgnoreCase)) ?? false;
            if (routed)
            {
                folders.Add(library.DownloadsPath?.Trim() ?? string.Empty);
            }
        }

        var distinct = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinct.Length != 1 || string.IsNullOrWhiteSpace(distinct[0]) || !ClientPaths.IsFullClientPath(distinct[0]))
        {
            return null;
        }

        var host = distinct[0].TrimEnd('/', '\\');
        var clientFolder = ToClientPath(host, mappings);
        var mapped = !string.Equals(clientFolder, host, StringComparison.Ordinal) || IsCovered(host, mappings);
        if (mapped)
        {
            return new Resolved(host, clientFolder);
        }

        var sameMachine = IsSameMachine(client);
        var reported = sameMachine && clientReportedPath is not null ? await clientReportedPath(cancellationToken) : null;
        return new Resolved(host, CanShareHostFolder(sameMachine, RunningInContainer(), host, reported) ? clientFolder : null);
    }

    private static bool IsCovered(string hostFolder, IEnumerable<DownloadClientPathMappingItem> mappings)
    {
        var key = Key(hostFolder);
        return mappings.Any(item => item.IsEnabled && !string.IsNullOrWhiteSpace(item.LocalPath) && !string.IsNullOrWhiteSpace(item.RemotePath)
            && (key.Equals(Key(item.LocalPath), StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(Key(item.LocalPath) + "/", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The category a library's link to a client uses: its own, else the client's for that kind of media.</summary>
    public static string RoutedCategory(DownloadClientItem client, LibraryItem library, string? linkCategory)
        => !string.IsNullOrWhiteSpace(linkCategory)
            ? linkCategory.Trim()
            : (string.Equals(library.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? client.TvCategory : client.MoviesCategory)?.Trim() ?? string.Empty;

    /// <summary>
    /// A folder on this host written the way the client sees it: through the
    /// longest matching path mapping, backwards (the mapping reads
    /// client-to-host). With none that match, the folder is passed as it is.
    /// </summary>
    public static string ToClientPath(string hostFolder, IEnumerable<DownloadClientPathMappingItem> mappings)
    {
        var key = Key(hostFolder);
        var match = mappings
            .Where(item => item.IsEnabled && !string.IsNullOrWhiteSpace(item.LocalPath) && !string.IsNullOrWhiteSpace(item.RemotePath))
            .Select(item => new { Mapping = item, Local = Key(item.LocalPath) })
            .Where(item => key.Equals(item.Local, StringComparison.OrdinalIgnoreCase)
                           || key.StartsWith(item.Local + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Local.Length)
            .ThenBy(item => item.Mapping.Priority)
            .FirstOrDefault();
        if (match is null)
        {
            return hostFolder;
        }

        var remote = match.Mapping.RemotePath.Trim().TrimEnd('/', '\\');
        var separator = remote.Contains('/') && !remote.Contains('\\') ? '/' : '\\';
        var rest = key[match.Local.Length..].Trim('/');
        return rest.Length == 0 ? remote : remote + separator + rest.Replace('/', separator);
    }

    private static string Key(string path) => path.Trim().TrimEnd('\\', '/').Replace('\\', '/');
}
