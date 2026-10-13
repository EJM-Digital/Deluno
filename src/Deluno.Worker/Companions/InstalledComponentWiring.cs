using Deluno.Api.Network;
using Deluno.Companions;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Companions;

/// <summary>
/// Connects the apps the installer's picker installed, only when the person asks (#1061, #968):
/// qBittorrent, SABnzbd, NZBGet, Deluge and Transmission as download clients, and Weir as the
/// processor. The installer installs the ticked apps with only what each needs to start safely;
/// Deluno connects nothing by itself. The setup guide and Connections offer a Connect button per
/// app and "Connect all", which call this. Each download client goes through the one shared path
/// (<see cref="DownloadClientConnector"/>) and a connection whose test fails is never saved. Weir
/// needs the person's own account, which is used in memory for that one call and never kept.
/// Nothing here returns an app's password to a caller.
/// </summary>
public sealed class InstalledComponentWirer(
    IInstalledComponentRecord record,
    IBundledComponentCredentialStore credentials,
    DownloadClientConnector connector,
    IConnectionsRepository connections,
    IWeirConnector? weir,
    ICompanionSystem system,
    IActivityFeedRepository activity,
    IDownloadClientRegistry? registry = null,
    IBundledComponentDetection? detection = null,
    Deluno.Contracts.IHealthCheckRepository? healthChecks = null,
    IRealtimeEventPublisher? realtime = null,
    Deluno.Libraries.Data.ILibrariesRepository? libraries = null)
    : IInstalledComponentConnector, IDownloadClientDeletedHook
{
    public const string ActivityCategory = "installer.components.connected";

    /// <param name="DefaultUsername">The app's own shipped login, used when the installer generated none (#1061).</param>
    /// <param name="DefaultPassword">The app's own shipped password, used when the installer generated none (#1061).</param>
    private sealed record ClientRecipe(string Name, string Protocol, string MoviesCategory, string TvCategory, bool NeedsSignIn, string? DefaultUsername = null, string? DefaultPassword = null);

    /// <summary>The categories come from <see cref="InstalledClientCategories"/>, the ones the installer creates in the client (#1133).</summary>
    private static ClientRecipe Recipe(string name, string protocol, string kind, bool needsSignIn, string? username = null, string? password = null)
    {
        var (movies, tv) = InstalledClientCategories.For(kind);
        return new ClientRecipe(name, protocol, movies, tv, needsSignIn, username, password);
    }

    private static readonly IReadOnlyDictionary<string, ClientRecipe> Clients = new Dictionary<string, ClientRecipe>(StringComparer.OrdinalIgnoreCase)
    {
        ["qbittorrent"] = Recipe("qBittorrent", "qbittorrent", "qbittorrent", true),
        ["transmission"] = Recipe("Transmission", "transmission", "transmission", false),
        ["deluge"] = Recipe("Deluge", "deluge", "deluge", false, null, "deluge"),
        ["sabnzbd"] = Recipe("SABnzbd", "sabnzbd", "sabnzbd", true),
        ["nzbget"] = Recipe("NZBGet", "nzbget", "nzbget", false, "nzbget", "tegbzn6789")
    };


    private static readonly string[] Order = ["qbittorrent", "transmission", "deluge", "sabnzbd", "nzbget"];

    private InstalledComponentEntry EntryFor(string kind)
        => record.Read().FirstOrDefault(item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
            ?? new InstalledComponentEntry(kind, "localhost", BundledComponentCatalog.All.First(spec => spec.Kind == kind).DefaultPort, DateTimeOffset.UtcNow);

    /// <summary>Per kind: installed, connected (a Deluno connection to it on this computer and port exists), and for Weir whether it has an account yet.</summary>
    public async Task<IReadOnlyDictionary<string, InstalledComponentState>> StatesAsync(IReadOnlyList<BundledComponentStatus> detected, CancellationToken cancellationToken)
    {
        var recorded = record.Read().Select(entry => entry.Kind).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clients = await connections.ListDownloadClientsAsync(cancellationToken);
        var states = new Dictionary<string, InstalledComponentState>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in detected)
        {
            var installed = status.Installed || recorded.Contains(status.Kind);
            if (status.Kind == CompanionToolIds.Weir)
            {
                var connected = weir is not null && await weir.ConnectionNameAsync(cancellationToken) is not null;
                var account = installed && !connected && weir is not null ? await weir.AccountStateAsync(cancellationToken) : new WeirAccountState(false, false, false);
                states[status.Kind] = new InstalledComponentState(installed, connected, installed && !connected && account.NeedsAccount, account.AccountExists);
                continue;
            }

            if (!Clients.TryGetValue(status.Kind, out var recipe))
            {
                states[status.Kind] = new InstalledComponentState(installed, false, false, false);
                continue;
            }

            var port = EntryFor(status.Kind).Port;
            var isConnected = clients.Any(client =>
                string.Equals(client.Protocol, recipe.Protocol, StringComparison.OrdinalIgnoreCase)
                && DownloadClientConnector.IsLoopback(client.Host)
                && (client.Port ?? DownloadClientConnector.DefaultPortFor(client.Protocol)) == port);
            states[status.Kind] = new InstalledComponentState(installed, isConnected, false, false);
        }

        return states;
    }

    private async Task<(string Name, bool Ok, string? Problem)> WireClientAsync(string kind, InstalledComponentEntry entry, int attempts, TimeSpan delay, CancellationToken cancellationToken, bool saveOnlyWhenHealthy = false)
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
            attempts, delay, cancellationToken, saveOnlyWhenHealthy);
        // The installer's copy stays while the app is installed: nothing serves it to a caller
        // (#1061), and it is what lets "Use <app>" connect again after the connection is deleted.

        if (result.Healthy || result.NoProvider)
        {
            await PrepareSaveFoldersAsync(recipe, result.Client, cancellationToken);
            if (result.Created)
            {
                await ResumeNewClientAsync(recipe, result.Client, cancellationToken);
            }

            // A Usenet client that is connected but has no news server yet is connected, and says what is missing (#1142).
            return (recipe.Name, true, result.NoProvider ? result.Message : null);
        }

        var problem = result.Message;
        if (recipe.NeedsSignIn && secret is null)
        {
            problem = $"Deluno does not have the sign-in the installer set up for {recipe.Name}. {problem}";
        }

        return (recipe.Name, false, problem);
    }

    /// <summary>
    /// A new NZBGet starts with its queues paused, and a restart after the installer's own resume (NZBGet restarts
    /// when its settings are reloaded) puts that back. Connecting it for the first time makes sure it is not left
    /// paused; only a client Deluno has just created is touched, never one somebody may have paused on purpose.
    /// </summary>
    private async Task ResumeNewClientAsync(ClientRecipe recipe, Deluno.Connections.Contracts.DownloadClientItem? client, CancellationToken cancellationToken)
    {
        if (registry is null || client is null || !string.Equals(recipe.Protocol, "nzbget", StringComparison.OrdinalIgnoreCase)
            || !registry.TryGet(recipe.Protocol, out var implementation))
        {
            return;
        }

        try
        {
            var resumed = await implementation.ResumeClientAsync(client, cancellationToken);
            if (resumed?.Succeeded == true)
            {
                await activity.RecordActivityAsync(ActivityCategory, $"Made sure {recipe.Name} is not paused. A new {recipe.Name} starts with its queues paused, and nothing would have downloaded.", null, null, null, null, cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            // Not being able to resume it is not a failed connection; its row says it is paused.
        }
    }

    /// <summary>
    /// Notes that the app has been connected once. If the note cannot be kept (the list is busy or damaged), say so in Activity
    /// instead of dropping it: the connection itself stands (#1260).
    /// </summary>
    private async Task MarkWiredAsync(string kind, string name, CancellationToken cancellationToken)
    {
        try
        {
            record.MarkWired(kind);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await activity.RecordActivityAsync(ActivityCategory, $"{name} is connected, but Deluno could not note that in its list of installed apps. {exception.Message}", null, null, null, null, cancellationToken);
        }
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
    /// The person asked to connect an app installed on this computer. A download client is connected
    /// with the sign-in Deluno holds (or the app's shipped login) and never returns it; a connection
    /// whose test fails is never saved and the screen is told to open the sign-in form. Weir is
    /// connected with the person's own account, held in memory for this call only.
    /// </summary>
    public async Task<InstalledComponentConnectResult?> ConnectAsync(string kind, string? username, string? password, string delunoBaseUrl, CancellationToken cancellationToken)
    {
        kind = kind.ToLowerInvariant();
        if (kind == CompanionToolIds.Weir)
        {
            return await ConnectWeirAsync(username, password, delunoBaseUrl, cancellationToken);
        }

        if (!Clients.TryGetValue(kind, out var recipe))
        {
            return null;
        }

        var entry = EntryFor(kind);
        // A connection whose test fails is never saved. The screen opens the connection form
        // pre-filled with the host and port so the person can type their own sign-in.
        var (name, ok, problem) = await WireClientAsync(kind, entry, 3, TimeSpan.FromSeconds(2), cancellationToken, saveOnlyWhenHealthy: true);
        if (ok)
        {
            await MarkWiredAsync(kind, name, cancellationToken);
            await realtime.PublishBundledComponentChangedAsync(kind, cancellationToken);
            await RefreshHealthAsync(cancellationToken);
            await activity.RecordActivityAsync(ActivityCategory, problem is null ? $"You connected {name} to Deluno from the apps you installed. Its connection test passed." : $"You connected {name} to Deluno from the apps you installed. It signed in, but it has no Usenet provider yet.", null, null, null, null, cancellationToken);
            return new InstalledComponentConnectResult(kind, name, true, problem is null ? $"{name} is connected." : $"{name} is connected, but it has nothing to download with yet. {problem}");
        }

        return new InstalledComponentConnectResult(
            kind,
            name,
            false,
            $"{name} is on this computer, but Deluno could not sign in to it with the login it ships with. {problem} Nothing was saved: enter your own login and save it.",
            NeedsSignIn: true,
            Protocol: recipe.Protocol,
            Host: entry.Host,
            Port: entry.Port);
    }

    private async Task<InstalledComponentConnectResult> ConnectWeirAsync(string? username, string? password, string delunoBaseUrl, CancellationToken cancellationToken)
    {
        if (weir is null)
        {
            return new InstalledComponentConnectResult(CompanionToolIds.Weir, "Weir", false, "Deluno can connect Weir only on the Windows computer it runs on.");
        }

        var outcome = await weir.ConnectAsync(username, password, delunoBaseUrl, cancellationToken);
        if (outcome.Healthy)
        {
            await MarkWiredAsync(CompanionToolIds.Weir, "Weir", cancellationToken);
            await realtime.PublishBundledComponentChangedAsync(CompanionToolIds.Weir, cancellationToken);
            await activity.RecordActivityAsync(ActivityCategory, "You connected Weir to Deluno from the apps you installed, with your own Weir account. Deluno did not keep the account's password.", null, null, null, null, cancellationToken);
            await LinkRefiningLibrariesAsync(outcome.ProcessorConnectionId, outcome.ProcessorConnectionName, cancellationToken);
        }

        return new InstalledComponentConnectResult(CompanionToolIds.Weir, "Weir", outcome.Healthy, outcome.Message, NeedsSignIn: outcome.NeedsSignIn);
    }

    /// <summary>
    /// Every library that refines before import and has no processor yet is linked to the Weir just connected,
    /// so its hand-offs go there (Weir's own wizard looks for exactly these libraries). A library already linked
    /// to a processor, or named for a different one, keeps it. Each link is written to Activity.
    /// </summary>
    private async Task LinkRefiningLibrariesAsync(string? connectionId, string? connectionName, CancellationToken cancellationToken)
    {
        if (libraries is null || string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        foreach (var library in await libraries.ListLibrariesAsync(cancellationToken))
        {
            if (!ImportWorkflows.HandsToProcessor(library.ImportWorkflow) || !string.IsNullOrWhiteSpace(library.ProcessorConnectionId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(library.ProcessorName) && !string.Equals(library.ProcessorName, connectionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var linked = await libraries.UpdateLibraryWorkflowAsync(
                library.Id,
                new Deluno.Libraries.Contracts.UpdateLibraryWorkflowRequest(
                    library.ImportWorkflow,
                    connectionName,
                    library.ProcessorOutputPath,
                    library.ProcessorTimeoutMinutes,
                    library.ProcessorFailureMode,
                    library.CleanupMode,
                    library.RemoveEmptySourceFolders,
                    connectionId),
                cancellationToken);
            if (linked is not null)
            {
                await activity.RecordActivityAsync(ActivityCategory, $"Linked the {library.Name} library to {connectionName ?? "Weir"}, so its downloads are refined before they are imported.", null, null, null, null, cancellationToken);
            }
        }
    }

    /// <summary>
    /// "Connect all": every installed download client that is not connected yet. Weir needs the
    /// person's own account, so it is never connected here; when it is installed and not connected its
    /// result says needsAccount.
    /// </summary>
    public async Task<IReadOnlyList<InstalledComponentConnectResult>> ConnectAllAsync(CancellationToken cancellationToken)
    {
        var detected = detection is null
            ? BundledComponentCatalog.All.Select(spec => new BundledComponentStatus(spec.Kind, spec.DisplayName, spec.Category, false, false, "localhost", spec.DefaultPort)).ToArray()
            : await detection.DetectAsync(cancellationToken);
        var states = await StatesAsync(detected, cancellationToken);
        var results = new List<InstalledComponentConnectResult>();
        foreach (var kind in Order)
        {
            if (states.TryGetValue(kind, out var state) && state.Installed && !state.Connected
                && await ConnectAsync(kind, null, null, string.Empty, cancellationToken) is { } result)
            {
                results.Add(result);
            }
        }

        if (states.TryGetValue(CompanionToolIds.Weir, out var weirState) && weirState.Installed && !weirState.Connected)
        {
            results.Add(new InstalledComponentConnectResult(
                CompanionToolIds.Weir, "Weir", false,
                "Weir needs your own account to connect. Create it in Weir's own step.",
                NeedsAccount: true));
        }

        return results;
    }


    /// <summary>
    /// Forgets the stored sign-ins whose app is gone (#1061). A stored sign-in stays for as
    /// long as the app is on this computer, even after its connection is deleted, because it
    /// is what "Use &lt;app&gt;" connects with and Deluno's API never returns it. It is removed
    /// for a kind Deluno cannot connect, and for an app that has been uninstalled: neither
    /// installed nor answering on its port.
    /// </summary>
    public async Task<int> SweepStoredSignInsAsync(CancellationToken cancellationToken, ISet<string>? missedLastSweep = null)
    {
        var kinds = credentials.Kinds();
        if (kinds.Count == 0)
        {
            return 0;
        }

        // An unread list is not an empty one (#1260): with the installer's record or the saved sign-ins busy or damaged, the
        // sweep cannot tell "gone" from "not readable just now", and forgetting a sign-in is not undoable. Try again next sweep.
        var recordRead = record.ReadChecked();
        if (!recordRead.CanBuildOn || credentials.Availability() is StateFileKind.Busy or StateFileKind.Unreadable)
        {
            return 0;
        }

        var recorded = (recordRead.Value ?? []).ToDictionary(entry => entry.Kind, StringComparer.OrdinalIgnoreCase);
        var found = detection is null
            ? null
            : (await detection.DetectAsync(cancellationToken)).ToDictionary(status => status.Kind, StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var kind in kinds)
        {
            var known = Clients.ContainsKey(kind);
            if (known && StillOnThisComputer(kind, recorded, found))
            {
                missedLastSweep?.Remove(kind);
                continue;
            }

            // A detection miss (an entry this account cannot read, a name that did not match) is not
            // proof the app is gone: an app that looks gone is forgotten only when it looks gone on two
            // sweeps in a row. A kind Deluno cannot connect at all is forgotten at once.
            if (known && missedLastSweep is not null && missedLastSweep.Add(kind))
            {
                continue;
            }

            missedLastSweep?.Remove(kind);

            ForgetSavedSignIn(kind);
            removed++;
        }

        return removed;
    }

    private static bool StillOnThisComputer(string kind, Dictionary<string, InstalledComponentEntry> recorded, Dictionary<string, BundledComponentStatus>? found)
    {
        // The installer's record is proof enough: it is never overridden by a detection miss.
        if (recorded.ContainsKey(kind))
        {
            return true;
        }

        // No record: only detection can say. With nothing to look with, or an app it cannot place, it is gone.
        return found is not null && found.TryGetValue(kind, out var status) && (status.Installed || status.Reachable);
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

        // Transmission and Deluge are given the library downloads folder on every grab (#1073), so a
        // category folder inside their download folder is never used and is not made (#1060).
        if (implementation.SetsSaveFolderPerGrab)
        {
            return;
        }

        var created = new List<string>();
        foreach (var category in new[] { recipe.MoviesCategory, recipe.TvCategory }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var check = await implementation.CheckCategoryAsync(client, category, cancellationToken);
                if ((SafeCategoryFolderToCreate(check.SavePath, category) ?? SafeDownloadFolderToCreate(check.SavePath, Directory.Exists)) is { } folder
                    && !Directory.Exists(folder) && !created.Contains(folder, StringComparer.OrdinalIgnoreCase))
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

    /// <summary>
    /// A client that keeps every category in its own download folder (NZBGet's DestDir, #1060)
    /// reports that folder, which it only creates on its first download. It is made one level deep
    /// only: a local folder whose parent already exists (the client's own data folder), never a
    /// drive root, a share or a path with "..".
    /// </summary>
    internal static string? SafeDownloadFolderToCreate(string? savePath, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return null;
        }

        var path = savePath.Trim().TrimEnd('/', '\\');
        if (!ClientPaths.IsFullClientPath(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        if (cut <= 0)
        {
            return null;
        }

        var parent = path[..cut];
        if (parent.Length == 0 || (parent.Length == 2 && parent[1] == ':'))
        {
            return null;
        }

        return directoryExists(parent) ? path : null;
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
    /// A Health pass can be in the middle of its run while an app is being connected, and a row it
    /// worked out a moment before the connection existed (or before its folders did) would then stay
    /// until the next full pass a quarter of an hour later. Asking for the two affected categories
    /// again now means a clean install ends with a clean Health screen (#1060).
    /// </summary>
    private async Task RefreshHealthAsync(CancellationToken cancellationToken)
    {
        if (healthChecks is null)
        {
            return;
        }

        await healthChecks.RequestImmediateRunAsync(Deluno.Contracts.HealthCheckCategories.DownloadClient, cancellationToken);
        await healthChecks.RequestImmediateRunAsync(Deluno.Contracts.HealthCheckCategories.RemotePathMapping, cancellationToken);
    }

    private static string Join(IReadOnlyList<string> names)
        => names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

}

/// <summary>
/// Runs in the background while Deluno is up. It connects nothing: the person connects the apps
/// they installed from the setup guide or Connections. Each start it forgets every login an earlier
/// Deluno stored for the tools it set up, gives an older Weir connection its health address, and
/// forgets the installer's stored logins for apps that are gone.
/// </summary>
public sealed class InstalledComponentWiringService(
    IServiceScopeFactory scopes,
    ILogger<InstalledComponentWiringService> logger) : BackgroundService
{
    private bool _startupWorkDone;
    private readonly HashSet<string> _missedLastSweep = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Wait before the first pass, so the database is ready.</summary>
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
            if (!_startupWorkDone)
            {
                // Once per start. Deluno no longer keeps any tool's login (#1061), so anything an
                // earlier version stored (Weir's generated administrator login included) is deleted.
                await scope.ServiceProvider.GetRequiredService<ICompanionLoginCleanup>().ForgetAllAsync(cancellationToken);
                // Gives an older Weir connection the address that lets it test healthy (#1055).
                await scope.ServiceProvider.GetRequiredService<WeirHealthAddressBackfill>().RunAsync(cancellationToken);
                _startupWorkDone = true;
            }

            var wirer = scope.ServiceProvider.GetRequiredService<InstalledComponentWirer>();
            // Any sign-in kept for an app that is gone, or a kind Deluno cannot connect, is forgotten (#1061).
            await wirer.SweepStoredSignInsAsync(cancellationToken, _missedLastSweep);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Tidying up after the apps the installer added did not finish; it will be tried again.");
        }
    }
}

/// <summary>Keeps a login the "get the tools you need" install generated in the installer's own store, so the person's Connect click can use it. Never served to a caller.</summary>
public sealed class InstalledClientLoginKeeper(IBundledComponentCredentialStore store) : IInstalledClientLogins
{
    public void Keep(string kind, string? username, string secret)
        => store.Save(kind, new BundledComponentCredentials(username ?? string.Empty, secret));
}
