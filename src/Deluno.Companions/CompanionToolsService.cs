using System.Collections.Concurrent;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Companions;

public interface ICompanionLoginCleanup
{
    /// <summary>Deletes every login an earlier Deluno stored for the tools it set up. Nothing serves them any more.</summary>
    Task ForgetAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Earlier versions kept the administrator login they generated for Weir (and each client's
/// generated login) in the <c>companion_tool_logins</c> table and showed it on request. Deluno
/// no longer keeps any: this empties the table on start. The table itself stays, because a
/// migration that has run is never edited.
/// </summary>
public sealed class SqliteCompanionLoginCleanup(IDelunoDatabaseConnectionFactory databaseConnectionFactory) : ICompanionLoginCleanup
{
    public async Task ForgetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM companion_tool_logins;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// Runs the "get the tools you need" step (#506): one tool at a time, each
/// reporting what it is doing, and a failure in one never stopping the others.
/// </summary>
public sealed class CompanionToolsService(
    IServiceScopeFactory scopes,
    IOptions<StoragePathOptions> storage,
    TimeProvider timeProvider,
    ILogger<CompanionToolsService> logger,
    IRealtimeEventPublisher? realtime = null)
{
    public const string UninstallNote = "Uninstalling Deluno leaves SABnzbd, qBittorrent and Weir installed, with their settings and downloads. Remove them separately in Windows Settings if you no longer want them.";

    private DateTimeOffset Now => timeProvider.GetUtcNow();

    private readonly ConcurrentDictionary<string, CompanionJobState> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private int _running;

    /// <summary>Records where a tool's setup is and tells an open Companion tools screen, so it never has to ask (#1179).</summary>
    private void SetJob(string id, CompanionJobState state)
    {
        _jobs[id] = state;
        _ = realtime.PublishCompanionJobChangedAsync(id);
    }

    /// <summary>
    /// Installing runs vendor installers on this computer's desktop, where
    /// Windows asks for permission. That needs a person signed in to it: not a
    /// Windows service, not a container.
    /// </summary>
    public static CompanionAvailability Availability()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new CompanionAvailability(false, "Deluno can install these tools only on Windows. On this system, install them yourself and add them under Download Clients.");
        }

        if (!Environment.UserInteractive)
        {
            return new CompanionAvailability(false, "Deluno is running as a Windows service, where nobody can answer Windows' permission prompt. Install the tools yourself, or run Deluno from the tray to use this.");
        }

        return new CompanionAvailability(true, null);
    }

    public async Task<IReadOnlyList<CompanionToolStatus>> ListAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var statuses = new List<CompanionToolStatus>();
        foreach (var tool in scope.ServiceProvider.GetServices<ICompanionTool>())
        {
            var connection = await tool.ConnectionNameAsync(cancellationToken);
            statuses.Add(new CompanionToolStatus(
                tool.Id,
                tool.Name,
                tool.Description,
                connection is not null ? "connected" : OperatingSystem.IsWindows() && tool.IsInstalled() ? "installed" : "not-installed",
                null,
                connection,
                _jobs.TryGetValue(tool.Id, out var job) ? job : null,
                OperatingSystem.IsWindows() && tool.IsInstalled() ? tool.LocalUrl() : null));
        }

        return statuses;
    }

    public bool TryStart(IReadOnlyList<string> toolIds, string delunoBaseUrl)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        var ordered = CompanionToolIds.All.Where(id => toolIds.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (var id in ordered)
        {
            SetJob(id, new CompanionJobState("queued", "queued", "Waiting its turn.", timeProvider.GetUtcNow()));
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var id in ordered)
                {
                    await RunOneAsync(id, delunoBaseUrl);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
        return true;
    }

    private async Task RunOneAsync(string id, string delunoBaseUrl)
    {
        using var scope = scopes.CreateScope();
        var tool = scope.ServiceProvider.GetServices<ICompanionTool>().First(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        var progress = new Progress(this, id);
        var folder = Path.Combine(storage.Value.DataRoot, "companions", "downloads");
        try
        {
            progress.Report("starting", $"Starting {tool.Name}.");
            await tool.RunAsync(new CompanionRunContext(delunoBaseUrl, folder, progress), CancellationToken.None);
            SetJob(id, _jobs[id] with { Status = "done", UpdatedUtc = timeProvider.GetUtcNow() });
        }
        catch (Exception exception) when (exception is CompanionStepException or CompanionDownloadException)
        {
            SetJob(id, new CompanionJobState("failed", _jobs[id].Step, exception.Message, timeProvider.GetUtcNow()));
        }
        catch (Exception exception)
        {
            // Never the exception's text: it can carry a path or a response body.
            logger.LogWarning(exception, "Setting up {Tool} failed.", tool.Name);
            SetJob(id, new CompanionJobState("failed", _jobs[id].Step, $"Setting up {tool.Name} stopped unexpectedly. Deluno's log has the details.", timeProvider.GetUtcNow()));
        }
    }

    private sealed class Progress(CompanionToolsService service, string id) : ICompanionProgress
    {
        public void Report(string step, string message)
            => service.SetJob(id, new CompanionJobState("running", step, message, service.Now));
    }
}
