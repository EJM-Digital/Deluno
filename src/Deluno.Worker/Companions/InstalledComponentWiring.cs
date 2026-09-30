using Deluno.Api.Network;
using Deluno.Companions;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
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
    IActivityFeedRepository activity,
    IDownloadClientRegistry? registry = null,
    IBundledComponentDetection? detection = null)
    : IInstalledComponentConnector, IDownloadClientDeletedHook
{
    public const string ActivityCategory = "installer.components.connected";

    /// <param name="DefaultUsername">The app's own shipped login, used when the installer generated none (#1061).</param>
    /// <param name="DefaultPassword">The app's own shipped password, used when the installer generated none (#1061).</param>
    private sealed record ClientRecipe(string Name, string Protocol, string MoviesCategory, string TvCategory, bool NeedsSignIn, string? DefaultUsername = null, string? DefaultPassword = null);

    private static readonly IReadOnlyDictionary<string, ClientRecipe> Clients = new Dictionary<string, ClientRecipe>(StringComparer.OrdinalIgnoreCase)
    {
        ["qbittorrent"] = new("qBittorrent", "qbittorrent", "deluno-movies", "deluno-tv", true),
        ["transmission"] = new("Transmission", "transmission", "deluno-movies", "deluno-tv", false),
        ["deluge"] = new("Deluge", "deluge", "deluno-movies", "deluno-tv", false, null, "deluge"),
        ["sabnzbd"] = new("SABnzbd", "sabnzbd", "Movies", "TV", true),
        ["nzbget"] = new("NZBGet", "nzbget", "Movies", "TV", false, "nzbget", "tegbzn6789")
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
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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
        // Only qBittorrent gets a login from the installer. NZBGet and Deluge keep the login
        // they ship with, and Deluno connects with that (#1061).
        var username = string.IsNullOrEmpty(saved?.Username) ? recipe.DefaultUsername : saved.Username;
        var secret = saved?.Password ?? recipe.DefaultPassword;
        if (secret is null && kind == "sabnzbd")
        {
            // The install session could not read SABnzbd's key; try where this account can.
            secret = SabnzbdKey();
        }

        var result = await connector.ConnectAsync(
            new DownloadClientWiring(recipe.Name, recipe.Protocol, "localhost", entry.Port, username, secret, recipe.MoviesCategory, recipe.TvCategory),
            attempts, delay, cancellationToken);
        // The installer's copy stays while the app is installed: nothing serves it to a caller
        // (#1061), and it is what lets "Use <app>" connect again after the connection is deleted.

        if (result.Healthy)
        {
            await PrepareSaveFoldersAsync(recipe, result.Client, cancellationToken);
            return (recipe.Name, true, null);
        }

        var problem = result.Message;
        if (recipe.NeedsSignIn && secret is null)
        {
            problem = $"Deluno does not have the sign-in the installer set up for {recipe.Name}. {problem}";
        }

        return (recipe.Name, false, problem);
    }

    private void ForgetSavedSignIn(string kind)
    {
        try
        {
            credentials.Remove(kind);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left in place; the next sweep tries again. Nothing serves it to a caller.
        }
    }

    /// <summary>
    /// The person clicked "Use X" on an app the installer added: connects it now, on this
    /// computer, with the sign-in Deluno holds, and never returns that sign-in (#1061).
    /// </summary>
    public async Task<InstalledComponentConnectResult?> ConnectNowAsync(string kind, CancellationToken cancellationToken)
    {
        kind = kind.ToLowerInvariant();
        if (!Clients.ContainsKey(kind))
        {
            return null;
        }

        var entry = record.Read().FirstOrDefault(item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
            ?? new InstalledComponentEntry(kind, "localhost", BundledComponentCatalog.All.First(spec => spec.Kind == kind).DefaultPort, DateTimeOffset.UtcNow);
        var (name, ok, problem) = await WireClientAsync(kind, entry, 3, TimeSpan.FromSeconds(2), cancellationToken);
        record.MarkWired(kind);
        return new InstalledComponentConnectResult(name, ok, ok ? $"{name} is connected." : problem ?? $"{name} did not answer its test.");
    }

    /// <summary>
    /// Forgets the stored sign-ins whose app is gone (#1061). A stored sign-in stays for as
    /// long as the app is on this computer, even after its connection is deleted, because it
    /// is what "Use &lt;app&gt;" connects with and Deluno's API never returns it. It is removed
    /// for a kind Deluno cannot connect, and for an app that has been uninstalled: neither
    /// installed nor answering on its port.
    /// </summary>
    public async Task<int> SweepStoredSignInsAsync(CancellationToken cancellationToken)
    {
        var kinds = credentials.Kinds();
        if (kinds.Count == 0)
        {
            return 0;
        }

        var recorded = record.Read().ToDictionary(entry => entry.Kind, StringComparer.OrdinalIgnoreCase);
        var found = detection is null
            ? null
            : (await detection.DetectAsync(cancellationToken)).ToDictionary(status => status.Kind, StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var kind in kinds)
        {
            if (Clients.ContainsKey(kind) && StillOnThisComputer(kind, recorded, found))
            {
                continue;
            }

            ForgetSavedSignIn(kind);
            removed++;
        }

        return removed;
    }

    private static bool StillOnThisComputer(string kind, Dictionary<string, InstalledComponentEntry> recorded, Dictionary<string, BundledComponentStatus>? found)
    {
        var hasRecord = recorded.ContainsKey(kind);
        if (found is null)
        {
            // Nothing to look with: the record alone decides.
            return hasRecord;
        }

        return found.TryGetValue(kind, out var status) ? status.Installed || status.Reachable : hasRecord;
    }

    /// <summary>A connection to an app on this computer was deleted. Its stored sign-in stays while the app is installed, so "Use" can connect it again (#1061).</summary>
    public Task DownloadClientDeletedAsync(string protocol, string? host, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// A client that names a folder per category (NZBGet, Transmission) reports
    /// "download folder / category" before anything has been downloaded, and that folder
    /// does not exist yet, which Health then lists as a problem on a clean install (#1060).
    /// So each missing category folder is created, but only as the category's own folder
    /// directly inside the download folder the client itself reports; a path that does not
    /// end in the category name, or sits at a drive root, is left alone. Nothing is moved or
    /// deleted, and every folder created is written to Activity.
    /// </summary>
    private async Task PrepareSaveFoldersAsync(ClientRecipe recipe, Deluno.Connections.Contracts.DownloadClientItem? client, CancellationToken cancellationToken)
    {
        if (registry is null || client is null || !registry.TryGet(recipe.Protocol, out var implementation))
        {
            return;
        }

        var created = new List<string>();
        foreach (var category in new[] { recipe.MoviesCategory, recipe.TvCategory }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var check = await implementation.CheckCategoryAsync(client, category, cancellationToken);
                if (SafeCategoryFolderToCreate(check.SavePath, category) is { } folder && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    created.Add(folder);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Not being able to prepare a folder is not a failed connection; Health still reports a folder that is missing.
            }
        }

        if (created.Count > 0)
        {
            await activity.RecordActivityAsync(
                ActivityCategory,
                $"Created {(created.Count == 1 ? "the folder" : "the folders")} {Join(created)} for {recipe.Name}'s downloads, inside the download folder {recipe.Name} itself uses, so {(created.Count == 1 ? "it is" : "they are")} there before the first download.",
                null, null, null, null, cancellationToken);
        }
    }

    /// <summary>The folder to create for a category, or null when it is not exactly a category-named folder inside a client download folder.</summary>
    internal static string? SafeCategoryFolderToCreate(string? savePath, string category)
    {
        if (string.IsNullOrWhiteSpace(savePath) || string.IsNullOrWhiteSpace(category)
            || category.IndexOfAny(['/', '\\', ':']) >= 0 || category is "." or "..")
        {
            return null;
        }

        var path = savePath.Trim().TrimEnd('/', '\\');
        if (!ClientPaths.IsFullClientPath(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var name = path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];
        if (!string.Equals(name, category, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parent = path[..(path.Length - name.Length)].TrimEnd('/', '\\');
        // The download folder itself must be a real folder, never a drive root.
        return parent.Length == 0 || (parent.Length == 2 && parent[1] == ':') ? null : path;
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
    private bool _healthAddressChecked;

    /// <summary>Wait before the first pass, so the web server is answering.</summary>
    internal TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Wait between passes.</summary>
    internal TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync(stoppingToken);
                await Task.Delay(TickInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown. A request that timed out inside a pass is not a shutdown (#1080):
            // TickAsync logs it and the loop carries on, so a starved machine cannot end it for good.
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            if (!_healthAddressChecked)
            {
                // Once per start: gives an older Weir connection the address that lets it test healthy (#1055).
                await scope.ServiceProvider.GetRequiredService<WeirHealthAddressBackfill>().RunAsync(cancellationToken);
                _healthAddressChecked = true;
            }

            var wirer = scope.ServiceProvider.GetRequiredService<InstalledComponentWirer>();
            // Any sign-in kept for an app whose connection exists, or never will, is forgotten (#1061).
            await wirer.SweepStoredSignInsAsync(cancellationToken);
            if (!wirer.HasWork() || OwnAddress() is not { } baseUrl)
            {
                return;
            }

            await wirer.RunOnceAsync(baseUrl, 10, TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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
