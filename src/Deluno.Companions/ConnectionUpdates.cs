using System.Net;
using Deluno.Connections.Data;
using Deluno.Connections.Versions;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Companions;

/// <summary>What Deluno can do about a connection's newer version (#1118).</summary>
public static class ConnectionUpdateModes
{
    /// <summary>Deluno installed it on this computer and can update it: the screen shows Update now.</summary>
    public const string UpdateNow = "updateNow";

    /// <summary>Deluno installed it, but its newest release is newer than the build Deluno checked: release notes only.</summary>
    public const string NotChecked = "notChecked";

    /// <summary>Anything else: release notes and a one-line how-to.</summary>
    public const string Manual = "manual";
}

/// <param name="Project">Whose releases it is compared with (<see cref="ConnectionProjects"/>).</param>
/// <param name="Installed">The version the connection last reported.</param>
/// <param name="Latest">The newest stable release.</param>
/// <param name="Host">Where the app runs, from the connection's address; null when unknown.</param>
/// <param name="PinnedVersion">The build Deluno itself installs for the project; null when it installs the latest release.</param>
/// <param name="InstalledByDeluno">The companion tool for the project is installed on this computer (Windows).</param>
public sealed record ConnectionUpdateFacts(string Project, string Installed, string Latest, string? Host, string? PinnedVersion, bool InstalledByDeluno, bool CanInstallCompanions);

/// <param name="Mode"><see cref="ConnectionUpdateModes"/>.</param>
/// <param name="TargetVersion">What Update now installs: the pinned build, or the latest release for Weir.</param>
/// <param name="Message">One plain sentence for the screen.</param>
public sealed record ConnectionUpdateOption(string Mode, string? TargetVersion, string Message);

/// <summary>
/// Whether Deluno may update a connection's app itself (#1118). Only an app Deluno installed on this computer,
/// and only to a build it can check: Weir's latest release (against its SHA256SUMS), or the pinned qBittorrent
/// and SABnzbd. Everything else is release notes and a how-to.
/// </summary>
public static class ConnectionUpdateDecision
{
    public const string DockerHowTo = "Pull the new image and restart the container.";
    public const string ElsewhereHowTo = "Update it where it runs.";

    public static ConnectionUpdateOption Decide(ConnectionUpdateFacts facts)
    {
        var loopback = DownloadClientConnector.IsLoopback(facts.Host);
        if (facts.InstalledByDeluno && facts.CanInstallCompanions && loopback)
        {
            var name = ConnectionProjects.DisplayName(facts.Project);
            if (facts.PinnedVersion is null)
            {
                return new ConnectionUpdateOption(
                    ConnectionUpdateModes.UpdateNow,
                    facts.Latest,
                    $"Deluno downloads {name} {facts.Latest}, checks it against the checksums its release publishes, installs it and restarts {name}.");
            }

            if (ReleaseVersions.IsOutOfDate(facts.Installed, facts.PinnedVersion))
            {
                var checkedOnly = ReleaseVersions.IsOutOfDate(facts.PinnedVersion, facts.Latest)
                    ? $" Deluno hasn't checked {facts.Latest} yet, so it installs {facts.PinnedVersion}."
                    : string.Empty;
                return new ConnectionUpdateOption(
                    ConnectionUpdateModes.UpdateNow,
                    facts.PinnedVersion,
                    $"Deluno installs {name} {facts.PinnedVersion}, a build it has checked, and restarts {name}.{checkedOnly}");
            }

            return new ConnectionUpdateOption(
                ConnectionUpdateModes.NotChecked,
                null,
                $"Deluno hasn't checked {facts.Latest} yet, so it won't update {name} for you. To update now, update it in {name} itself. Deluno offers Update now here once a Deluno release has checked {facts.Latest}; the release notes say what changed.");
        }

        return new ConnectionUpdateOption(ConnectionUpdateModes.Manual, null, LooksLikeDocker(facts.Host) ? DockerHowTo : ElsewhereHowTo);
    }

    /// <summary>
    /// A host that reads like a container: a compose service name (one word, no dots), Docker's own names, or an
    /// address in the range Docker hands containers (172.16.0.0/12).
    /// </summary>
    public static bool LooksLikeDocker(string? host)
    {
        var text = host?.Trim().Trim('[', ']');
        if (string.IsNullOrEmpty(text) || DownloadClientConnector.IsLoopback(text))
        {
            return false;
        }

        if (text.Contains("docker", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(text, out var address))
        {
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
        }

        return !text.Contains('.');
    }

    /// <summary>
    /// One notice per app per newer version (#1118): shown while a newer stable version is out and the person has
    /// not dismissed it, and it comes back only for a version newer than the one dismissed. It clears itself the
    /// moment a check reads a version that is no longer out of date.
    /// </summary>
    public static bool NoticeShown(string? installed, string? latest, string? dismissed)
        => ReleaseVersions.IsOutOfDate(installed, latest)
            && (string.IsNullOrWhiteSpace(dismissed) || ReleaseVersions.IsOutOfDate(dismissed, latest));
}

/// <summary>One connection with a newer stable version out, as the bell and the connection's own screen read it.</summary>
public sealed record ConnectionUpdateItem(
    string Kind,
    string Id,
    string Name,
    string Project,
    string AppName,
    string Version,
    string LatestVersion,
    string? ReleaseUrl,
    string? DismissedVersion,
    bool Notice,
    string Mode,
    string? TargetVersion,
    string Message);

/// <param name="Outcome"><c>updated</c>, <c>unchanged</c> or <c>failed</c>.</param>
public sealed record ConnectionUpdateResult(string Outcome, string Message, string? Version);

/// <summary>Lists the notices, dismisses one, and runs Update now (#1118).</summary>
public sealed class ConnectionUpdateService(
    IConnectionVersionStore versions,
    ILatestReleaseService latest,
    IConnectionsRepository connections,
    IProcessorRepository processors,
    IConnectionHealthTester tester,
    IEnumerable<ICompanionTool> tools,
    IActivityFeedRepository activity,
    IOptions<StoragePathOptions> storage,
    ILogger<ConnectionUpdateService> logger)
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>How long Update now waits between looks for the new version. Tests make it instant.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Whether this Deluno can run installers at all: Windows with a person at the desktop. Tests say yes.</summary>
    public Func<bool> CompanionsAvailable { get; init; } = () => CompanionToolsService.Availability().Available;

    /// <summary>How many times Update now looks for the new version after the install.</summary>
    public int RecheckAttempts { get; init; } = 10;

    public async Task<IReadOnlyList<ConnectionUpdateItem>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await versions.ListAsync(cancellationToken);
        var items = new List<ConnectionUpdateItem>();
        IReadOnlyDictionary<string, Where>? where = null;
        foreach (var row in rows)
        {
            // Deluno replaces Prowlarr and Jackett, so an indexer never carries a version or an update notice.
            if (row.Kind == ConnectionKinds.Indexer || row.Project is null || latest.Peek(row.Project) is not { } release || !ReleaseVersions.IsOutOfDate(row.Version, release.Version))
            {
                continue;
            }

            where ??= await WhereAsync(cancellationToken);
            where.TryGetValue(Key(row.Kind, row.Id), out var found);
            var appName = ConnectionProjects.DisplayName(row.Project);
            var tool = ToolFor(row.Project);
            var option = ConnectionUpdateDecision.Decide(new ConnectionUpdateFacts(
                row.Project,
                row.Version,
                release.Version,
                found?.Host,
                tool?.PinnedVersion,
                tool is not null && tool.IsInstalled(),
                CompanionsAvailable()));
            items.Add(new ConnectionUpdateItem(
                row.Kind,
                row.Id,
                found?.Name ?? appName,
                row.Project,
                appName,
                row.Version,
                release.Version,
                release.Url,
                row.DismissedVersion,
                ConnectionUpdateDecision.NoticeShown(row.Version, release.Version, row.DismissedVersion),
                option.Mode,
                option.TargetVersion,
                option.Message));
        }

        return items;
    }

    /// <summary>"Dismiss this version": the newest version Deluno knows of for the connection. Returns the version dismissed, or null when there is none to dismiss.</summary>
    public async Task<string?> DismissAsync(string kind, string id, CancellationToken cancellationToken)
    {
        var row = (await versions.ListAsync(cancellationToken)).FirstOrDefault(item => Same(item.Kind, kind) && item.Id == id);
        if (row?.Project is null || latest.Peek(row.Project) is not { } release)
        {
            return null;
        }

        return await versions.DismissAsync(row.Kind, row.Id, release.Version, cancellationToken) ? release.Version : null;
    }

    /// <summary>
    /// Update now. Re-decides on the spot (never trusts the screen), runs the tool's own checked install, waits for
    /// the app to answer, runs a check straight away and reports in plain words.
    /// </summary>
    public async Task<ConnectionUpdateResult> UpdateNowAsync(string kind, string id, string delunoBaseUrl, CancellationToken cancellationToken)
    {
        var item = (await ListAsync(cancellationToken)).FirstOrDefault(entry => Same(entry.Kind, kind) && entry.Id == id);
        if (item is null)
        {
            return new ConnectionUpdateResult("unchanged", "Nothing changed: no newer version is waiting for this connection.", null);
        }

        if (item.Mode != ConnectionUpdateModes.UpdateNow || ToolFor(item.Project) is not { } tool)
        {
            return new ConnectionUpdateResult("failed", $"Failed: Deluno can't update {item.AppName} itself. {item.Message}", item.Version);
        }

        if (!await OneAtATime.WaitAsync(0, cancellationToken))
        {
            return new ConnectionUpdateResult("failed", "Failed: another update is already running. Wait for it to finish.", item.Version);
        }

        try
        {
            var progress = new LastStep();
            var folder = Path.Combine(storage.Value.DataRoot, "companions", "downloads");
            try
            {
                await tool.UpdateAsync(new CompanionRunContext(delunoBaseUrl, folder, progress), cancellationToken);
            }
            catch (Exception exception) when (exception is CompanionStepException or CompanionDownloadException)
            {
                return await FailedAsync(item, exception.Message, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Never the exception's text: it can carry a path or a response body.
                logger.LogWarning(exception, "Updating {App} failed.", item.AppName);
                return await FailedAsync(item, $"{item.AppName} could not be updated. Deluno's log has the details.", cancellationToken);
            }

            // The check runs straight away, and again until the app answers with its new version.
            string? now = null;
            for (var attempt = 0; attempt < Math.Max(1, RecheckAttempts); attempt++)
            {
                try
                {
                    await tester.TestAsync(item.Kind, item.Id, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogDebug(exception, "Checking {App} after its update failed.", item.AppName);
                }

                now = (await versions.ListAsync(cancellationToken)).FirstOrDefault(row => Same(row.Kind, item.Kind) && row.Id == item.Id)?.Version;
                if (now is not null && now != item.Version)
                {
                    break;
                }

                if (attempt < RecheckAttempts - 1)
                {
                    await Delay(TimeSpan.FromSeconds(3), cancellationToken);
                }
            }

            if (now is not null && now != item.Version)
            {
                var message = $"Updated to {now}.";
                await TellActivityAsync(item, $"{item.AppName} was updated from {item.Version} to {now}, because you chose Update now.", cancellationToken);
                return new ConnectionUpdateResult("updated", message, now);
            }

            await TellActivityAsync(item, $"{item.AppName} was updated, but it still reports {item.Version}. You chose Update now.", cancellationToken);
            return new ConnectionUpdateResult("unchanged", $"Nothing changed: {item.AppName} still reports {now ?? item.Version}. It may need a moment, then Test it again.", now ?? item.Version);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private async Task<ConnectionUpdateResult> FailedAsync(ConnectionUpdateItem item, string why, CancellationToken cancellationToken)
    {
        await TellActivityAsync(item, $"Updating {item.AppName} did not finish: {why}", cancellationToken);
        return new ConnectionUpdateResult("failed", $"Failed: {why}", item.Version);
    }

    private async Task TellActivityAsync(ConnectionUpdateItem item, string message, CancellationToken cancellationToken)
    {
        try
        {
            await activity.RecordActivityAsync("connection.updated", message, null, null, item.Kind, item.Id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Could not write the update to Activity.");
        }
    }

    private IUpdatableCompanion? ToolFor(string project)
        => tools.OfType<IUpdatableCompanion>().FirstOrDefault(tool => string.Equals(tool.Project, project, StringComparison.OrdinalIgnoreCase));

    private sealed record Where(string Name, string? Host);

    private static string Key(string kind, string id) => $"{kind.ToLowerInvariant()}:{id}";

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name and address of each connection whose address Deluno holds, to decide where the app runs.</summary>
    private async Task<IReadOnlyDictionary<string, Where>> WhereAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, Where>();
        foreach (var client in await connections.ListDownloadClientsAsync(cancellationToken))
        {
            map[Key(ConnectionKinds.DownloadClient, client.Id)] = new Where(client.Name, client.Host ?? HostOf(client.EndpointUrl));
        }


        foreach (var processor in await processors.ListProcessorConnectionsAsync(cancellationToken))
        {
            map[Key(ConnectionKinds.Processor, processor.Id)] = new Where(processor.Name, HostOf(processor.SubmissionUrl));
        }

        return map;
    }

    private static string? HostOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.Trim((char)91, (char)93) : null;

    private sealed class LastStep : ICompanionProgress
    {
        public void Report(string step, string message)
        {
        }
    }
}
