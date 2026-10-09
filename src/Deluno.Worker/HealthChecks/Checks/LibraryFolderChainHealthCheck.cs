using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Libraries.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// The category and save-path chain for every library, on a schedule
/// (#909) -- the same check the folder-chain drawer already runs when
/// somebody opens it (<see cref="IFolderChainService"/>, #616), so a renamed
/// category or a remapped share is caught within fifteen minutes instead of
/// only when a person happens to open that drawer.
///
/// <para>Also reports, informationally, when a library's downloads folder and
/// its root are on different volumes -- hardlinking is then impossible and
/// every import copies the file instead, taking the space twice. Not a
/// problem in the sense that stops anything; a person choosing that layout on
/// purpose (network downloads, local library) should still be told why every
/// import costs double.</para>
/// </summary>
public sealed class LibraryFolderChainHealthCheck(
    ILibrariesRepository libraries,
    IFolderChainService folderChain) : IHealthCheck
{
    public string Category => HealthCheckCategories.Library;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<HealthCheckOutcome>();
        foreach (var library in await libraries.ListLibrariesAsync(cancellationToken))
        {
            var report = await folderChain.CheckAsync(library.Id, cancellationToken);
            if (report is null)
            {
                continue;
            }

            outcomes.Add(BuildChainOutcome(report));

            if (VolumesOutcome(report) is { } volumes)
            {
                outcomes.Add(volumes);
            }
        }

        return outcomes;
    }

    private static HealthCheckOutcome BuildChainOutcome(FolderChainReport report)
    {
        var id = $"library:{report.LibraryId}:folder-chain";
        var name = $"The {report.LibraryName} library's category and save path";

        if (report.AllMatch)
        {
            return HealthCheckOutcome.Ok(id, HealthCheckCategories.Library, name, "Every routed client's category and save path match this library's folders.");
        }

        var problems = report.Clients
            .SelectMany(client => client.Links.Select(link => (ClientName: client.ClientName, link)))
            .Concat(report.LibraryLinks.Select(link => (ClientName: string.Empty, link)))
            .Where(item => item.link.Status != FolderChainStatuses.Ok)
            .ToArray();

        var worst = problems.FirstOrDefault(item => item.link.Status == FolderChainStatuses.Problem, problems.FirstOrDefault());
        var message = worst.link is null
            ? "One or more links in this library's folder chain could not be confirmed."
            : worst.link.Message;

        return worst.link is { Status: FolderChainStatuses.Problem }
            ? HealthCheckOutcome.Problem(
                id, HealthCheckCategories.Library, name, message,
                cause: $"A download client's category or save path no longer matches what the {report.LibraryName} library expects.",
                fix: $"Open the {report.LibraryName} library's folder chain and fix the category or path it names.")
            : HealthCheckOutcome.Unknown(id, HealthCheckCategories.Library, name, message);
    }

    /// <summary>
    /// The volume a folder is on. A network share (\\nas\media) is its own volume, named by server and share:
    /// a drive lookup cannot answer for one, which left "could not tell" on a library whose downloads are on
    /// C: and whose library is on a NAS.
    /// </summary>
    public static string VolumeOf(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith("\\\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            var parts = trimmed.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return "//" + string.Join('/', parts.Take(2));
        }

        return new DriveInfo(trimmed).Name;
    }

    private static HealthCheckOutcome? VolumesOutcome(FolderChainReport report)
    {
        if (string.IsNullOrWhiteSpace(report.DownloadsPath) || string.IsNullOrWhiteSpace(report.RootPath))
        {
            return null;
        }

        var id = $"library:{report.LibraryId}:volumes";
        var name = $"The {report.LibraryName} library's downloads and library folders";

        bool? sameVolume;
        try
        {
            sameVolume = string.Equals(VolumeOf(report.DownloadsPath), VolumeOf(report.RootPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            sameVolume = null;
        }

        return sameVolume switch
        {
            true => HealthCheckOutcome.Ok(id, HealthCheckCategories.Library, name, "Downloads and the library share a volume, so imports can hardlink."),
            false => HealthCheckOutcome.Unknown(
                id, HealthCheckCategories.Library, name,
                $"The {report.LibraryName} library's downloads folder and its library root are on different volumes, so Deluno cannot hardlink between them. Every import copies the file instead, using the space twice until the download is cleaned up.",
                detailsJson: null),
            null => null
        } ?? HealthCheckOutcome.Unknown(id, HealthCheckCategories.Library, name, "Deluno could not tell whether downloads and this library share a volume.");
    }
}
