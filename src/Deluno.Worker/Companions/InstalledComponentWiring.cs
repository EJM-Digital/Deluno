using Deluno.Api.Network;
using Deluno.Companions;
using Deluno.Jobs.Data;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Companions;

/// <summary>
/// Connects the apps the installer's picker installed, once each, with no clicks (#1054):
/// qBittorrent, SABnzbd, NZBGet, Deluge and Transmission as download clients, Weir as the
/// processor. Each goes through the one shared path (<see cref="DownloadClientConnector"/>
/// for clients, the Weir companion for Weir), is tested, and is written to Activity in
/// plain words. Every app is tried once: the record marks it wired after the first attempt
/// whatever the outcome, so a connection the person deletes is not made again and a failed
/// test leaves the connection in place, showing what failed, for them to fix.
/// </summary>
public sealed class InstalledComponentWirer(
    IInstalledComponentRecord record,
    IBundledComponentCredentialStore credentials,
    DownloadClientConnector connector,
    IEnumerable<ICompanionTool> tools,
    ICompanionSystem system,
    IActivityFeedRepository activity)
{
    public const string ActivityCategory = "installer.components.connected";

    private sealed record ClientRecipe(string Name, string Protocol, string MoviesCategory, string TvCategory, bool NeedsSignIn);

    private static readonly IReadOnlyDictionary<string, ClientRecipe> Clients = new Dictionary<string, ClientRecipe>(StringComparer.OrdinalIgnoreCase)
    {
        ["qbittorrent"] = new("qBittorrent", "qbittorrent", "deluno-movies", "deluno-tv", true),
        ["transmission"] = new("Transmission", "transmission", "deluno-movies", "deluno-tv", false),
        ["deluge"] = new("Deluge", "deluge", "deluno-movies", "deluno-tv", true),
        ["sabnzbd"] = new("SABnzbd", "sabnzbd", "Movies", "TV", true),
        ["nzbget"] = new("NZBGet", "nzbget", "Movies", "TV", true)
    };

    /// <summary>Weir last: it can take a while to answer, and the clients should not wait behind it.</summary>
    private static readonly string[] Order = ["qbittorrent", "transmission", "deluge", "sabnzbd", "nzbget", "weir"];

    public bool HasWork() => record.Read().Any(entry => entry.WiredUtc is null && IsKnown(entry.Kind));

    private static bool IsKnown(string kind) => Clients.ContainsKey(kind) || string.Equals(kind, CompanionToolIds.Weir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Connects every installed, not yet connected app. Returns how many were tried.</summary>
    public async Task<int> RunOnceAsync(string delunoBaseUrl, int attempts, TimeSpan delay, CancellationToken cancellationToken)
    {
        var pending = record.Read()
            .Where(entry => entry.WiredUtc is null && IsKnown(entry.Kind))
            .OrderBy(entry => Array.IndexOf(Order, entry.Kind.ToLowerInvariant()))
            .ToArray();
        if (pending.Length == 0)
        {
            return 0;
        }

        var connected = new List<string>();
        var failed = new List<string>();
        Exception? unexpected = null;
        foreach (var entry in pending)
        {
            var kind = entry.Kind.ToLowerInvariant();
            (string Name, bool Ok, string? Problem) outcome;
            try
            {
                outcome = kind == CompanionToolIds.Weir
                    ? await WireWeirAsync(delunoBaseUrl, cancellationToken)
                    : await WireClientAsync(kind, entry, attempts, delay, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Something unexpected (the database not ready yet): not marked, so the next pass tries again.
                unexpected = exception;
                break;
            }

            record.MarkWired(entry.Kind);
            if (outcome.Ok)
            {
                connected.Add(outcome.Name);
            }
            else
            {
                failed.Add(outcome.Name);
                await activity.RecordActivityAsync(
                    ActivityCategory,
                    $"{outcome.Name} was installed and added to Deluno, but its connection test did not pass: {outcome.Problem} The connection is kept; fix it, then test it again from its own screen.",
                    null, null, null, null, cancellationToken);
            }
        }

        if (connected.Count > 0)
        {
            await activity.RecordActivityAsync(
                ActivityCategory,
                // Only what passed is claimed as passed; whatever did not is named beside it (and has its own line with the reason).
                failed.Count == 0
                    ? $"Connected {Join(connected)} to Deluno after installing {(connected.Count == 1 ? "it" : "them")}. Each connection was tested and passed."
                    : $"Connected {Join(connected)} to Deluno after installing {(connected.Count == 1 ? "it" : "them")}, and {(connected.Count == 1 ? "its test" : "their tests")} passed. {Join(failed)} did not pass its test; the reason is in its own line.",
                null, null, null, null, cancellationToken);
        }

        if (unexpected is not null)
        {
            throw new InvalidOperationException("Connecting the installed apps did not finish.", unexpected);
        }

        return pending.Length;
    }

    private async Task<(string Name, bool Ok, string? Problem)> WireClientAsync(string kind, InstalledComponentEntry entry, int attempts, TimeSpan delay, CancellationToken cancellationToken)
    {
        var recipe = Clients[kind];
        var saved = credentials.TryGet(kind);
        var username = string.IsNullOrEmpty(saved?.Username) ? null : saved.Username;
        var secret = saved?.Password;
        if (secret is null && kind == "sabnzbd")
        {
            // The install session could not read SABnzbd's key; try where this account can.
            secret = SabnzbdKey();
        }

        var result = await connector.ConnectAsync(
            new DownloadClientWiring(recipe.Name, recipe.Protocol, "localhost", entry.Port, username, secret, recipe.MoviesCategory, recipe.TvCategory),
            attempts, delay, cancellationToken);
        if (result.Healthy)
        {
            return (recipe.Name, true, null);
        }

        var problem = result.Message;
        if (recipe.NeedsSignIn && secret is null)
        {
            problem = $"Deluno does not have the sign-in the installer set up for {recipe.Name}. {problem}";
        }

        return (recipe.Name, false, problem);
    }

    private string? SabnzbdKey()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(system.LocalAppData, "sabnzbd", "sabnzbd.ini"),
                     Path.Combine(system.RoamingAppData, "SABnzbd", "sabnzbd.ini")
                 })
        {
            var key = CompanionConfigFiles.SabnzbdValue(system.ReadText(path), "api_key");
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key;
            }
        }

        return null;
    }

    private async Task<(string Name, bool Ok, string? Problem)> WireWeirAsync(string delunoBaseUrl, CancellationToken cancellationToken)
    {
        var weir = tools.First(tool => tool.Id == CompanionToolIds.Weir);
        if (!weir.IsInstalled())
        {
            return (weir.Name, false, $"Deluno could not find {weir.Name} on this computer to connect it.");
        }

        try
        {
            // Installed already, so this connects it (creating its administrator login if it has none yet) and tests it.
            await weir.RunAsync(new CompanionRunContext(delunoBaseUrl, Path.GetTempPath(), new SilentProgress()), cancellationToken);
            return (weir.Name, true, null);
        }
        catch (CompanionStepException exception)
        {
            return (weir.Name, false, exception.Message);
        }
    }

    private static string Join(IReadOnlyList<string> names)
        => names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    private sealed class SilentProgress : ICompanionProgress
    {
        public void Report(string step, string message) { }
    }
}

/// <summary>
/// Runs <see cref="InstalledComponentWirer"/> in the background while Deluno is up. It
/// does not wait for first run: the connections it makes need no signed-in person, so an
/// app is connected as soon as this Deluno is answering and has been told about it.
/// </summary>
public sealed class InstalledComponentWiringService(
    IServiceScopeFactory scopes,
    IServiceProvider services,
    ILogger<InstalledComponentWiringService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var wirer = scope.ServiceProvider.GetRequiredService<InstalledComponentWirer>();
            if (!wirer.HasWork() || OwnAddress() is not { } baseUrl)
            {
                return;
            }

            await wirer.RunOnceAsync(baseUrl, 10, TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Connecting the apps the installer added did not finish; it will be tried again.");
        }
    }

    /// <summary>Where Weir reaches this Deluno back: its own listening address, on this computer.</summary>
    private string? OwnAddress()
    {
        var address = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var uri = new Uri(address.Replace("*", "127.0.0.1").Replace("+", "127.0.0.1").Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1"));
        return $"{uri.Scheme}://127.0.0.1:{uri.Port}";
    }
}
