using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Api.Network;

namespace Deluno.Tray;

/// <summary>
/// Where a bundled component keeps its own settings and state (#1177). It is the data root
/// (<c>%LocalAppData%\DelunoData\components\&lt;name&gt;</c> by default), never the install root
/// (<c>%LocalAppData%\Deluno</c>): Velopack's Setup replaces the install root on a reinstall or repair,
/// which used to delete Transmission's settings, resume files and .torrent files.
/// </summary>
public static class ComponentPaths
{
    /// <summary>Stands for Deluno's data root in <c>bundled-components.json</c> paths and arguments.</summary>
    public const string DataToken = "%DelunoData%";

    /// <summary>The data root. Replaceable so a test never reads the real profile.</summary>
    public static Func<string> DataRoot { get; set; } = () => AppSettings.Load().DataRoot;

    /// <summary>Environment variables and the <see cref="DataToken"/> expanded.</summary>
    public static string Expand(string value)
        => Environment.ExpandEnvironmentVariables(value.Replace(DataToken, DataRoot(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Velopack's install root, which the installer replaces. Only what Velopack owns (the program and its logs) may live here.</summary>
    public static string InstallRoot()
        => Path.Combine(
            Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA") is { Length: > 0 } testRoot
                ? testRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Deluno");

    public static string ComponentsFolder(string dataRoot) => Path.Combine(dataRoot, "components");

    public static string ComponentFolder(string dataRoot, string name) => Path.Combine(ComponentsFolder(dataRoot), name);
}

/// <summary>The folders Deluno set a download client to, kept so a lost client folder can be written again.</summary>
public static class DownloadLayoutRecord
{
    private sealed record Stored(string CompletedRoot, string IncompleteRoot);

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "config", "download-layout.json");

    public static void Save(string dataRoot, DownloadLayout layout)
    {
        try
        {
            var path = PathFor(dataRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Stored(layout.CompletedRoot, layout.IncompleteRoot)));
        }
        catch
        {
            // Not remembering it only means a recovery asks the running client instead.
        }
    }

    public static DownloadLayout? Load(string dataRoot)
    {
        try
        {
            var path = PathFor(dataRoot);
            var stored = File.Exists(path) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) : null;
            return stored is { CompletedRoot.Length: > 0, IncompleteRoot.Length: > 0 }
                ? new DownloadLayout(stored.CompletedRoot, stored.IncompleteRoot)
                : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>One torrent read from the running client so it can be added back.</summary>
public sealed record TorrentToReadd(string Name, string MagnetLink, string? DownloadDir);

/// <summary>What is waiting for the person after a lost Transmission folder was written again.</summary>
public sealed record TransmissionReaddNotice(DateTimeOffset CreatedUtc, bool DaemonNeedsRestart, IReadOnlyList<TorrentToReadd> Torrents, string? Summary = null);

/// <summary>Transmission's RPC on this computer: the few calls recovery needs.</summary>
public sealed class TransmissionRpc(int port, BundledComponentCredentials? login, Func<HttpMessageHandler>? handlerFactory = null)
{
    private readonly Func<HttpMessageHandler> _handler = handlerFactory ?? (() => new SocketsHttpHandler { ConnectCallback = Deluno.Infrastructure.Http.LoopbackFirstConnection.ConnectAsync });

    public async Task<JsonElement?> CallAsync(string method, object arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient(_handler(), disposeHandler: true)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(15)
            };
            if (login is not null)
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login.Username}:{login.Password}")));
            }

            var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["method"] = method, ["arguments"] = arguments });
            string? sessionId = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "transmission/rpc") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (sessionId is not null)
                {
                    request.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", sessionId);
                }

                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Conflict && response.Headers.TryGetValues("X-Transmission-Session-Id", out var ids))
                {
                    sessionId = ids.First();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                return document.RootElement.TryGetProperty("result", out var result) && result.GetString() == "success"
                    ? document.RootElement.Clone()
                    : null;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Not running, or not answering: the caller says so.
        }

        return null;
    }

    /// <summary>The folders the running client is using right now, or null when it is not answering.</summary>
    public async Task<(string? DownloadDir, string? IncompleteDir, bool IncompleteEnabled)?> ReadFoldersAsync(CancellationToken cancellationToken)
    {
        var answer = await CallAsync("session-get", new Dictionary<string, object>(), cancellationToken).ConfigureAwait(false);
        if (answer is not { } root || !root.TryGetProperty("arguments", out var args))
        {
            return null;
        }

        string? Text(string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var enabled = args.TryGetProperty("incomplete-dir-enabled", out var flag) && flag.ValueKind == JsonValueKind.True;
        return (Text("download-dir"), Text("incomplete-dir"), enabled);
    }

    /// <summary>Every torrent the running client holds, with its magnet link; null when it is not answering.</summary>
    public async Task<IReadOnlyList<TorrentToReadd>?> ReadTorrentsAsync(CancellationToken cancellationToken)
    {
        var answer = await CallAsync("torrent-get", new Dictionary<string, object>
        {
            ["fields"] = new[] { "name", "magnetLink", "downloadDir" }
        }, cancellationToken).ConfigureAwait(false);
        if (answer is not { } root || !root.TryGetProperty("arguments", out var args) || !args.TryGetProperty("torrents", out var torrents))
        {
            return null;
        }

        var list = new List<TorrentToReadd>();
        foreach (var torrent in torrents.EnumerateArray())
        {
            var magnet = torrent.TryGetProperty("magnetLink", out var m) ? m.GetString() : null;
            if (string.IsNullOrWhiteSpace(magnet))
            {
                continue;
            }

            list.Add(new TorrentToReadd(
                torrent.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                magnet,
                torrent.TryGetProperty("downloadDir", out var d) ? d.GetString() : null));
        }

        return list;
    }

    public async Task<bool> AddAsync(TorrentToReadd torrent, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, object> { ["filename"] = torrent.MagnetLink, ["paused"] = false };
        if (!string.IsNullOrWhiteSpace(torrent.DownloadDir))
        {
            arguments["download-dir"] = torrent.DownloadDir;
        }

        return await CallAsync("torrent-add", arguments, cancellationToken).ConfigureAwait(false) is not null;
    }
}

/// <summary>What a startup check of the component folders did (#1177).</summary>
public sealed record ComponentStateMigrationResult(IReadOnlyList<string> Moved, bool TransmissionFolderRecreated);

/// <summary>
/// At start-up, before any bundled client is started: moves component folders out of the install root into the
/// data root with nothing lost, and writes Transmission's settings again when its folder is gone altogether
/// (an install a reinstall already hit), leaving a notice so the person can add the torrents back.
/// </summary>
public static class ComponentStateMigration
{
    public const string Transmission = "transmission";

    public static string ReaddNoticePath(string dataRoot) => Path.Combine(ComponentPaths.ComponentsFolder(dataRoot), "transmission-readd.json");

    public static async Task<ComponentStateMigrationResult> RunAsync(
        string installRoot,
        string dataRoot,
        IEnumerable<string> installedKinds,
        BundledComponentCredentials? transmissionLogin,
        IStartedComponentsRegistry? startedRegistry,
        int transmissionPort,
        Func<HttpMessageHandler>? rpcHandler,
        IInstallLog? log,
        CancellationToken cancellationToken)
    {
        var moved = new List<string>();
        var oldComponents = Path.Combine(installRoot, "components");
        try
        {
            if (Directory.Exists(oldComponents))
            {
                foreach (var folder in Directory.GetDirectories(oldComponents))
                {
                    var name = Path.GetFileName(folder);
                    MoveOrMerge(folder, ComponentPaths.ComponentFolder(dataRoot, name));
                    moved.Add(name);
                    log?.Write($"Moved the {name} folder out of the install folder to {ComponentPaths.ComponentFolder(dataRoot, name)}.");
                }

                if (!Directory.EnumerateFileSystemEntries(oldComponents).Any())
                {
                    Directory.Delete(oldComponents);
                }
            }

            MoveFileIfAbsent(Path.Combine(installRoot, "config", "started-components.json"), Path.Combine(dataRoot, "config", "started-components.json"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Write($"Could not move a component folder out of the install folder: {exception.Message}");
        }

        var recreated = false;
        var transmissionFolder = ComponentPaths.ComponentFolder(dataRoot, Transmission);
        if (installedKinds.Contains(Transmission, StringComparer.OrdinalIgnoreCase) && !Directory.Exists(transmissionFolder))
        {
            recreated = await RecreateTransmissionAsync(transmissionFolder, dataRoot, transmissionLogin, startedRegistry, transmissionPort, rpcHandler, log, cancellationToken).ConfigureAwait(false);
        }

        return new ComponentStateMigrationResult(moved, recreated);
    }

    private static async Task<bool> RecreateTransmissionAsync(
        string folder, string dataRoot, BundledComponentCredentials? login, IStartedComponentsRegistry? startedRegistry,
        int port, Func<HttpMessageHandler>? rpcHandler, IInstallLog? log, CancellationToken cancellationToken)
    {
        try
        {
            var rpc = new TransmissionRpc(port, login, rpcHandler);
            // A client still running from memory knows its own folders and holds the torrent list; ask it first.
            var running = await rpc.ReadFoldersAsync(cancellationToken).ConfigureAwait(false);
            var torrents = running is null ? null : await rpc.ReadTorrentsAsync(cancellationToken).ConfigureAwait(false);

            var layout = DownloadLayoutRecord.Load(dataRoot) ?? DownloadLayout.Default();
            var download = running?.DownloadDir is { Length: > 0 } d ? d : layout.CompletedRoot;
            var incomplete = running?.IncompleteDir is { Length: > 0 } i ? i : layout.IncompleteFolder("Transmission");
            var incompleteEnabled = running is null ? true : running.Value.IncompleteEnabled;

            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "settings.json"), TransmissionSettings(download, incomplete, incompleteEnabled, port, login));

            var notice = new TransmissionReaddNotice(DateTimeOffset.UtcNow, running is not null, torrents ?? []);
            File.WriteAllText(ReaddNoticePath(dataRoot), JsonSerializer.Serialize(notice));
            startedRegistry?.Add(Transmission);
            log?.Write("Transmission's folder was missing (a reinstall removed it), so Deluno wrote its settings again. Torrents added before may need adding back.");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Write($"Could not write Transmission's settings again: {exception.Message}");
            return false;
        }
    }

    /// <summary>Transmission's settings.json as Deluno sets it up: folders, the RPC bound to this computer, and the sign-in when one is stored.</summary>
    public static string TransmissionSettings(string downloadDir, string incompleteDir, bool incompleteEnabled, int port, BundledComponentCredentials? login, string? watchDir = null)
    {
        var settings = new JsonObject
        {
            ["download-dir"] = downloadDir,
            ["incomplete-dir"] = incompleteDir,
            ["incomplete-dir-enabled"] = incompleteEnabled,
            ["rpc-enabled"] = true,
            ["rpc-bind-address"] = "127.0.0.1",
            ["rpc-port"] = port,
            ["rpc-whitelist-enabled"] = true,
            ["rpc-whitelist"] = "127.0.0.1,::1",
            ["rpc-authentication-required"] = login is not null
        };
        if (login is not null)
        {
            settings["rpc-username"] = login.Username;
            settings["rpc-password"] = login.Password;
        }

        if (!string.IsNullOrWhiteSpace(watchDir))
        {
            settings["watch-dir"] = watchDir;
            settings["watch-dir-enabled"] = true;
        }

        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void MoveFileIfAbsent(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to);
        File.Delete(from);
    }

    internal static void MoveOrMerge(string from, string to, bool overwrite = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (!Directory.Exists(to))
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (IOException)
            {
                // Another drive, or something holding a file: copy then remove.
            }
        }

        CopyMissing(from, to, overwrite);
        Directory.Delete(from, recursive: true);
    }

    private static void CopyMissing(string from, string to, bool overwrite)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (overwrite || !File.Exists(target))
            {
                File.Copy(file, target, overwrite);
            }
        }

        foreach (var directory in Directory.GetDirectories(from))
        {
            CopyMissing(directory, Path.Combine(to, Path.GetFileName(directory)), overwrite);
        }
    }
}

/// <summary>The one-click re-add that follows a recreated Transmission folder.</summary>
public static class TransmissionReadd
{
    public static TransmissionReaddNotice? Pending(string dataRoot)
    {
        try
        {
            var path = ComponentStateMigration.ReaddNoticePath(dataRoot);
            return File.Exists(path) ? JsonSerializer.Deserialize<TransmissionReaddNotice>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The notice in plain words.</summary>
    public const string NoticeText = "Transmission's settings were lost when Deluno was reinstalled, so Deluno wrote them again. Torrents added before may need adding back.";

    /// <summary>
    /// Adds the torrents back. <paramref name="restartDaemon"/> restarts Transmission so it uses the recreated folder; it
    /// is only called when the client was already running when the folder was written again. Returns what happened, in plain words.
    /// </summary>
    public static async Task<string> RunAsync(string dataRoot, TransmissionRpc rpc, Func<CancellationToken, Task<bool>> restartDaemon, CancellationToken cancellationToken)
    {
        var notice = Pending(dataRoot);
        if (notice is null)
        {
            return "There is nothing to add back.";
        }

        var torrents = notice.Torrents.ToList();
        if (torrents.Count == 0)
        {
            var live = await rpc.ReadTorrentsAsync(cancellationToken).ConfigureAwait(false);
            if (live is null)
            {
                return "Transmission is not running, so Deluno cannot read the torrents it had. Start Transmission, then try again.";
            }

            torrents = live.ToList();
        }

        if (notice.DaemonNeedsRestart && !await restartDaemon(cancellationToken).ConfigureAwait(false))
        {
            return "Deluno could not restart Transmission. Restart it yourself, then try again.";
        }

        var added = 0;
        foreach (var torrent in torrents)
        {
            if (await rpc.AddAsync(torrent, cancellationToken).ConfigureAwait(false))
            {
                added++;
            }
        }

        if (added < torrents.Count)
        {
            return $"Deluno added {added} of {torrents.Count} torrents back. Is Transmission running? Try again to add the rest.";
        }

        try
        {
            File.Delete(ComponentStateMigration.ReaddNoticePath(dataRoot));
        }
        catch (IOException)
        {
            // The notice shows once more; adding again is harmless, Transmission skips what it has.
        }

        return added == 0 ? "Transmission had no torrents to add back." : $"Deluno added {added} torrents back to Transmission.";
    }
}

/// <summary>Stops and starts Transmission's daemon the way Deluno starts it, so it picks up the folder that was written again.</summary>
public static class TransmissionDaemon
{
    public static async Task<bool> RestartAsync(BundledComponentSpec spec, CancellationToken cancellationToken)
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("transmission-daemon"))
        {
            try
            {
                process.Kill();
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch
            {
                // Already gone.
            }
        }

        return await StartAsync(spec, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> StartAsync(BundledComponentSpec spec, CancellationToken cancellationToken)
    {
        new RealProcessStarter().Start(ComponentPaths.Expand(spec.PostInstallCommand!), ComponentAutoStarter.ExpandArgs(spec.PostInstallArgs), spec.StartHidden);
        var probe = new LoopbackPortProbe();
        for (var i = 0; i < 60; i++)
        {
            if (await probe.IsOpenAsync("127.0.0.1", spec.DefaultPort, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}

/// <summary>The web app's view of the Transmission notice: the same notice file the tray menu item uses (#1177).</summary>
public sealed class TrayComponentRecoveryNotices(
    Func<string>? dataRoot = null,
    Func<string, BundledComponentCredentials?>? login = null,
    Func<BundledComponentSpec, TransmissionRpc>? rpcFor = null,
    Func<BundledComponentSpec, CancellationToken, Task<bool>>? restart = null) : IComponentRecoveryNotices
{
    private string Root => (dataRoot ?? ComponentPaths.DataRoot)();

    private static BundledComponentSpec Spec() => BundledComponents.Load().First(s => s.Kind == ComponentStateMigration.Transmission);

    public Task<IReadOnlyList<ComponentRecoveryNotice>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ComponentRecoveryNotice> list = [];
        if (TransmissionReadd.Pending(Root) is { } notice)
        {
            var count = notice.Torrents.Count;
            var message = notice.Summary is not null ? notice.Summary : count > 0
                ? $"Transmission's settings were rebuilt after Deluno was reinstalled; {count} torrent{(count == 1 ? "" : "s")} may need adding back."
                : "Transmission's settings were rebuilt after Deluno was reinstalled; torrents it had before may need adding back.";
            list = [new ComponentRecoveryNotice(ComponentStateMigration.Transmission, "Transmission", message, count, CanReadd: notice.Summary is null || count > 0)];
        }

        return Task.FromResult(list);
    }

    public async Task<ComponentRecoveryResult?> ReaddAsync(string kind, CancellationToken cancellationToken)
    {
        if (!IsTransmission(kind) || TransmissionReadd.Pending(Root) is null)
        {
            return null;
        }

        var spec = Spec();
        var rpc = rpcFor?.Invoke(spec) ?? new TransmissionRpc(spec.DefaultPort, (login ?? (k => BundledComponentCredentialStores.CreateForDataRoot(Root).TryGet(k)))(ComponentStateMigration.Transmission));
        var message = await TransmissionReadd.RunAsync(Root, rpc, ct => (restart ?? TransmissionDaemon.RestartAsync)(spec, ct), cancellationToken).ConfigureAwait(false);
        return new ComponentRecoveryResult(TransmissionReadd.Pending(Root) is null, message);
    }

    public Task<bool> DismissAsync(string kind, CancellationToken cancellationToken)
    {
        if (!IsTransmission(kind) || TransmissionReadd.Pending(Root) is null)
        {
            return Task.FromResult(false);
        }

        try
        {
            File.Delete(ComponentStateMigration.ReaddNoticePath(Root));
            return Task.FromResult(true);
        }
        catch (IOException)
        {
            return Task.FromResult(false);
        }
    }

    private static bool IsTransmission(string kind) => string.Equals(kind, ComponentStateMigration.Transmission, StringComparison.OrdinalIgnoreCase);
}
