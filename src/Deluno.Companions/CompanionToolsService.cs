using System.Collections.Concurrent;
using Deluno.Infrastructure.Storage;
using Deluno.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Companions;

public interface ICompanionLoginStore
{
    Task SaveAsync(CompanionLogin login, CancellationToken cancellationToken);

    Task<CompanionLogin?> GetAsync(string toolId, CancellationToken cancellationToken);
}

/// <summary>
/// Logins Deluno generated for the tools it set up. Protected at rest with the
/// same secret protector as every other credential; the purpose label
/// <c>companion-tool:secret</c> must not change.
/// </summary>
public sealed class SqliteCompanionLoginStore(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    ISecretProtector secretProtector,
    TimeProvider timeProvider) : ICompanionLoginStore
{
    private const string Purpose = "companion-tool:secret";

    public async Task SaveAsync(CompanionLogin login, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO companion_tool_logins (tool_id, url, username, secret, secret_label, updated_utc)
            VALUES (@toolId, @url, @username, @secret, @label, @now)
            ON CONFLICT (tool_id) DO UPDATE SET url = excluded.url, username = excluded.username, secret = excluded.secret, secret_label = excluded.secret_label, updated_utc = excluded.updated_utc;
            """;
        AddParameter(command, "@toolId", login.ToolId);
        AddParameter(command, "@url", login.Url);
        AddParameter(command, "@username", login.Username);
        AddParameter(command, "@secret", secretProtector.Protect(Purpose, login.Secret));
        AddParameter(command, "@label", login.SecretLabel);
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CompanionLogin?> GetAsync(string toolId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT url, username, secret, secret_label FROM companion_tool_logins WHERE tool_id = @toolId;";
        AddParameter(command, "@toolId", toolId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CompanionLogin(toolId, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), secretProtector.Unprotect(Purpose, reader.GetString(2)) ?? string.Empty, reader.GetString(3))
            : null;
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
    ILogger<CompanionToolsService> logger)
{
    public const string UninstallNote = "Uninstalling Deluno leaves SABnzbd, qBittorrent and Weir installed, with their settings and downloads. Remove them separately in Windows Settings if you no longer want them.";

    private DateTimeOffset Now => timeProvider.GetUtcNow();

    private readonly ConcurrentDictionary<string, CompanionJobState> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private int _running;

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
        var logins = scope.ServiceProvider.GetRequiredService<ICompanionLoginStore>();
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
                await logins.GetAsync(tool.Id, cancellationToken) is not null,
                _jobs.TryGetValue(tool.Id, out var job) ? job : null));
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
            _jobs[id] = new CompanionJobState("queued", "queued", "Waiting its turn.", timeProvider.GetUtcNow());
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
            _jobs[id] = _jobs[id] with { Status = "done", UpdatedUtc = timeProvider.GetUtcNow() };
        }
        catch (Exception exception) when (exception is CompanionStepException or CompanionDownloadException)
        {
            _jobs[id] = new CompanionJobState("failed", _jobs[id].Step, exception.Message, timeProvider.GetUtcNow());
        }
        catch (Exception exception)
        {
            // Never the exception's text: it can carry a path or a response body.
            logger.LogWarning(exception, "Setting up {Tool} failed.", tool.Name);
            _jobs[id] = new CompanionJobState("failed", _jobs[id].Step, $"Setting up {tool.Name} stopped unexpectedly. Deluno's log has the details.", timeProvider.GetUtcNow());
        }
    }

    private sealed class Progress(CompanionToolsService service, string id) : ICompanionProgress
    {
        public void Report(string step, string message)
            => service._jobs[id] = new CompanionJobState("running", step, message, service.Now);
    }
}
