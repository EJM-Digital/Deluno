using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Deluno.Api.Network;

namespace Deluno.Tray;

/// <summary>
/// The two folders the installer asks for (#1133): one root for finished downloads, with a folder
/// per category inside it (<c>Completed\Movies</c>, <c>Completed\TV</c>), and one root for
/// downloads still in progress, with a folder per client inside it (<c>Incomplete\qBittorrent</c>).
/// Every client the picker puts on is set to these, so the folders agree with the library and
/// processor folders instead of each client saving to its own default.
/// </summary>
public sealed record DownloadLayout(string CompletedRoot, string IncompleteRoot)
{
    /// <summary>The same drive as Windows, which every computer has, so the default never names a drive that is not there.</summary>
    public static DownloadLayout Default()
    {
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var root = string.IsNullOrEmpty(drive) ? @"C:\" : drive;
        return new DownloadLayout(Path.Combine(root, "Downloads", "Completed"), Path.Combine(root, "Downloads", "Incomplete"));
    }

    public string CompletedFolder(string category) => Path.Combine(CompletedRoot, category);

    /// <summary>One incomplete folder per client, named for the client.</summary>
    public string IncompleteFolder(string clientName) => Path.Combine(IncompleteRoot, clientName);

    /// <summary>Why this layout cannot be used, or null: both must be full folder paths and neither may sit inside the other.</summary>
    public string? Problem()
    {
        foreach (var (label, value) in new[] { ("finished downloads", CompletedRoot), ("downloads in progress", IncompleteRoot) })
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value.Trim()))
            {
                return $"The folder for {label} must be a full path, such as C:\\Downloads.";
            }
        }

        var completed = Path.GetFullPath(CompletedRoot.Trim()).TrimEnd('\\', '/');
        var incomplete = Path.GetFullPath(IncompleteRoot.Trim()).TrimEnd('\\', '/');
        if (string.Equals(completed, incomplete, StringComparison.OrdinalIgnoreCase)
            || incomplete.StartsWith(completed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || completed.StartsWith(incomplete + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "Finished downloads and downloads in progress need two separate folders, neither inside the other.";
        }

        return null;
    }
}

/// <summary>Sets up a download client the picker just installed so Deluno can use it as it is (#1133).</summary>
public interface IDownloadClientConfigurator
{
    /// <summary>Null when the client now has the Movies and TV categories and the layout's folders; otherwise what could not be done, in plain words.</summary>
    Task<string?> ConfigureAsync(BundledComponentSpec spec, DownloadLayout layout, BundledComponentCredentials? credentials, CancellationToken cancellationToken);
}

/// <summary>
/// Creates the Movies and TV categories in each client (<see cref="InstalledClientCategories"/>),
/// switches on Deluge's Label plugin, and points each client's finished and in-progress folders at
/// the <see cref="DownloadLayout"/>, through the client's own Web UI or RPC on this computer. Nothing
/// else in the client is touched.
/// </summary>
public sealed class DownloadClientConfigurator(Func<HttpMessageHandler>? handlerFactory = null, TimeSpan? retryDelay = null, IInstallLog? log = null) : IDownloadClientConfigurator
{
    private readonly IInstallLog? _log = log;
    private readonly Func<HttpMessageHandler> _handler = handlerFactory ?? (() => new SocketsHttpHandler { ConnectCallback = Deluno.Infrastructure.Http.LoopbackFirstConnection.ConnectAsync, UseCookies = true, CookieContainer = new CookieContainer() });
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);

    public async Task<string?> ConfigureAsync(BundledComponentSpec spec, DownloadLayout layout, BundledComponentCredentials? credentials, CancellationToken cancellationToken)
    {
        if (layout.Problem() is { } problem)
        {
            return problem;
        }

        var (movies, tv) = InstalledClientCategories.For(spec.Kind);
        var incomplete = layout.IncompleteFolder(spec.DisplayName);
        try
        {
            foreach (var folder in new[] { layout.CompletedRoot, layout.CompletedFolder("Movies"), layout.CompletedFolder("TV"), incomplete })
            {
                Directory.CreateDirectory(folder);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return $"Deluno could not make the folders for {spec.DisplayName}'s downloads: {exception.Message}";
        }

        using var http = new HttpClient(_handler(), disposeHandler: true) { BaseAddress = new Uri($"http://127.0.0.1:{spec.DefaultPort}/"), Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            var result = spec.Kind.ToLowerInvariant() switch
            {
                "qbittorrent" => await QbittorrentAsync(http, layout, incomplete, movies, tv, credentials, cancellationToken),
                "transmission" => await TransmissionAsync(http, layout, incomplete, cancellationToken),
                "deluge" => await DelugeAsync(http, layout, incomplete, movies, tv, credentials, cancellationToken),
                "sabnzbd" => await SabnzbdAsync(http, layout, incomplete, credentials, cancellationToken),
                "nzbget" => await NzbgetAsync(http, layout, incomplete, credentials, cancellationToken),
                _ => null
            };
            if (result is null && string.Equals(spec.Kind, "transmission", StringComparison.OrdinalIgnoreCase))
            {
                // Kept in the data root so a lost Transmission folder can be written again with these folders (#1177).
                if (DownloadLayoutRecord.Save(ComponentPaths.DataRoot(), layout) is { } notKept)
                {
                    _log?.Write($"{spec.DisplayName}: Deluno could not remember the download folders, so a later recovery will ask the running client instead. {notKept}");
                }
            }

            return result is null ? null : $"Deluno could not finish setting up {spec.DisplayName}: {result}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or IOException)
        {
            return $"Deluno could not finish setting up {spec.DisplayName}: {exception.Message}";
        }
    }

    // ---- qBittorrent: a category with its own save folder, and the in-progress folder -----------

    private async Task<string?> QbittorrentAsync(HttpClient http, DownloadLayout layout, string incomplete, string movies, string tv, BundledComponentCredentials? credentials, CancellationToken cancellationToken)
    {
        if (credentials is null)
        {
            return "Deluno does not have the sign-in it set up for qBittorrent.";
        }

        // qBittorrent refuses a request whose Origin or Referer is not its own address.
        http.DefaultRequestHeaders.Referrer = http.BaseAddress;
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", http.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using (var login = await http.PostAsync("api/v2/auth/login", Form(("username", credentials.Username), ("password", credentials.Password)), cancellationToken))
        {
            // qBittorrent 4.x answers 200 "Ok." or "Fails."; 5.2.1 answers a right login with 204 and no body.
            // So a 2xx counts unless the body says it failed, and an answer that does not say "Ok." is settled
            // by asking for the version with the session just made, which a wrong login cannot do.
            var body = await login.Content.ReadAsStringAsync(cancellationToken);
            if (!login.IsSuccessStatusCode || body.Contains("Fails", StringComparison.OrdinalIgnoreCase))
            {
                return "qBittorrent did not accept the sign-in Deluno set up for it.";
            }

            if (!body.Contains("Ok", StringComparison.OrdinalIgnoreCase))
            {
                using var version = await http.GetAsync("api/v2/app/version", cancellationToken);
                if (!version.IsSuccessStatusCode)
                {
                    return "qBittorrent did not accept the sign-in Deluno set up for it.";
                }
            }
        }

        foreach (var (category, folder) in new[] { (movies, layout.CompletedFolder("Movies")), (tv, layout.CompletedFolder("TV")) })
        {
            using var create = await http.PostAsync("api/v2/torrents/createCategory", Form(("category", category), ("savePath", folder)), cancellationToken);
            if (create.StatusCode == HttpStatusCode.Conflict)
            {
                // Already there (a re-run): make its folder the one we want.
                using var edit = await http.PostAsync("api/v2/torrents/editCategory", Form(("category", category), ("savePath", folder)), cancellationToken);
                if (!edit.IsSuccessStatusCode)
                {
                    return $"qBittorrent would not change the folder of its {category} category ({(int)edit.StatusCode}).";
                }
            }
            else if (!create.IsSuccessStatusCode)
            {
                return $"qBittorrent would not create its {category} category ({(int)create.StatusCode}).";
            }
        }

        var preferences = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["save_path"] = layout.CompletedRoot,
            ["temp_path_enabled"] = true,
            ["temp_path"] = incomplete,
            // A category's save folder only applies to a torrent qBittorrent manages automatically; with this
            // left on Manual every grab landed in the completed root, not in Movies or TV. Deluno also asks
            // for automatic management on every torrent it adds, so this holds on any qBittorrent.
            ["auto_tmm_enabled"] = true,
            ["torrent_changed_tmm_enabled"] = true,
            ["category_changed_tmm_enabled"] = true,
            ["save_path_changed_tmm_enabled"] = true
        });
        using var prefs = await http.PostAsync("api/v2/app/setPreferences", Form(("json", preferences)), cancellationToken);
        return prefs.IsSuccessStatusCode ? null : $"qBittorrent would not take its download folders ({(int)prefs.StatusCode}).";
    }

    // ---- Transmission: no categories, a download folder with Movies and TV inside it ----------

    private async Task<string?> TransmissionAsync(HttpClient http, DownloadLayout layout, string incomplete, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["method"] = "session-set",
            ["arguments"] = new Dictionary<string, object>
            {
                ["download-dir"] = layout.CompletedRoot,
                ["incomplete-dir"] = incomplete,
                ["incomplete-dir-enabled"] = true
            }
        });

        string? sessionId = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "transmission/rpc") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (sessionId is not null)
            {
                request.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", sessionId);
            }

            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict && response.Headers.TryGetValues("X-Transmission-Session-Id", out var ids))
            {
                sessionId = ids.First();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return $"Transmission would not take its download folders ({(int)response.StatusCode}).";
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("result", out var result) && result.GetString() == "success"
                ? null
                : $"Transmission would not take its download folders ({(document.RootElement.TryGetProperty("result", out var why) ? why.GetString() : "no answer")}).";
        }

        return "Transmission did not accept the session it was asked to use.";
    }

    // ---- Deluge: the Label plugin on, one label per category, each moving finished downloads --------

    private async Task<string?> DelugeAsync(HttpClient http, DownloadLayout layout, string incomplete, string movies, string tv, BundledComponentCredentials? credentials, CancellationToken cancellationToken)
    {
        var id = 0;
        async Task<JsonElement> Call(string method, params object?[] args)
        {
            using var response = await http.PostAsync("json",
                new StringContent(JsonSerializer.Serialize(new { method, @params = args, id = ++id }), Encoding.UTF8, "application/json"), cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.Clone();
        }

        static bool Failed(JsonElement answer) => answer.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;

        // Deluge ships with the password "deluge" (#1061); the installer never changes it.
        var login = await Call("auth.login", string.IsNullOrEmpty(credentials?.Password) ? "deluge" : credentials!.Password);
        if (!login.TryGetProperty("result", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            return "Deluge did not accept its password.";
        }

        // The Web UI is up before the torrent engine behind it is connected; wait for it.
        var connected = false;
        for (var attempt = 0; attempt < 30 && !connected; attempt++)
        {
            var state = await Call("web.connected");
            connected = state.TryGetProperty("result", out var value) && value.ValueKind == JsonValueKind.True;
            if (connected)
            {
                break;
            }

            var hosts = await Call("web.get_hosts");
            if (hosts.TryGetProperty("result", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
            {
                await Call("web.connect", list[0][0].GetString());
            }

            await Task.Delay(_retryDelay, cancellationToken);
        }

        if (!connected)
        {
            return "Deluge's Web UI could not connect to its torrent engine.";
        }

        await Call("core.enable_plugin", "Label");
        JsonElement labels = default;
        var labelPluginOn = false;
        for (var attempt = 0; attempt < 20 && !labelPluginOn; attempt++)
        {
            labels = await Call("label.get_labels");
            labelPluginOn = !Failed(labels) && labels.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array;
            if (!labelPluginOn)
            {
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }

        if (!labelPluginOn)
        {
            return "Deluge's Label plugin would not switch on. Turn it on in Deluge, Preferences, Plugins.";
        }

        var existing = labels.GetProperty("result").EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, folder) in new[] { (movies, layout.CompletedFolder("Movies")), (tv, layout.CompletedFolder("TV")) })
        {
            if (!existing.Contains(label) && Failed(await Call("label.add", label)))
            {
                return $"Deluge would not create its {label} label.";
            }

            var options = await Call("label.set_options", label, new Dictionary<string, object>
            {
                ["apply_move_completed"] = true,
                ["move_completed"] = true,
                ["move_completed_path"] = folder
            });
            if (Failed(options))
            {
                return $"Deluge would not set the completed folder of its {label} label.";
            }
        }

        var config = await Call("core.set_config", new Dictionary<string, object>
        {
            ["download_location"] = incomplete,
            ["move_completed"] = true,
            ["move_completed_path"] = layout.CompletedRoot
        });
        return Failed(config) ? "Deluge would not take its download folders." : null;
    }

    // ---- SABnzbd: its two folders, and a category with a folder of its own ----------------------

    private async Task<string?> SabnzbdAsync(HttpClient http, DownloadLayout layout, string incomplete, BundledComponentCredentials? credentials, CancellationToken cancellationToken)
    {
        var apiKey = credentials?.Password;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "Deluno could not read the API key SABnzbd made for itself.";
        }

        async Task<bool> SetAsync(string query)
        {
            using var response = await http.GetAsync($"api?mode=set_config&output=json&apikey={Uri.EscapeDataString(apiKey)}&{query}", cancellationToken);
            return response.IsSuccessStatusCode;
        }

        async Task<JsonElement?> GetAsync(string section)
        {
            try
            {
                using var response = await http.GetAsync($"api?mode=get_config&output=json&apikey={Uri.EscapeDataString(apiKey)}&section={section}", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return document.RootElement.Clone();
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException)
            {
                return null;
            }
        }

        static string E(string value) => Uri.EscapeDataString(value);
        static string Norm(string? path) => (path ?? string.Empty).Trim().Replace('/', '\\').TrimEnd('\\');

        var (movies, tv) = InstalledClientCategories.For("sabnzbd");
        var wanted = new[] { (movies, layout.CompletedFolder("Movies")), (tv, layout.CompletedFolder("TV")) };

        // SABnzbd answers "ok" to a write and can still lose it (it may not be ready, or it rewrites its own
        // default categories on its first start). So write, read the settings back, and only call it done when
        // they hold and still hold a moment later; otherwise write again, for a bounded time.
        var lastRead = "nothing read";
        for (var attempt = 1; attempt <= SabnzbdAttempts; attempt++)
        {
            var written = await SetAsync($"section=misc&keyword=download_dir&value={E(incomplete)}")
                && await SetAsync($"section=misc&keyword=complete_dir&value={E(layout.CompletedRoot)}");
            foreach (var (category, folder) in wanted)
            {
                written &= await SetAsync($"section=categories&name={E(category)}&dir={E(folder)}");
            }

            if (written)
            {
                for (var look = 0; look < 2; look++)
                {
                    var (holds, read) = SabnzbdHolds(await GetAsync("misc"), await GetAsync("categories"), incomplete, layout.CompletedRoot, wanted, Norm);
                    lastRead = read;
                    if (!holds)
                    {
                        break;
                    }

                    if (look == 1)
                    {
                        _log?.Write($"SABnzbd: read back after {attempt} attempt(s): {read}");
                        return null;
                    }

                    await Task.Delay(_retryDelay * 4, cancellationToken);
                }
            }

            await Task.Delay(_retryDelay * 2, cancellationToken);
        }

        _log?.Write($"SABnzbd: the settings did not hold; last read: {lastRead}");
        return $"SABnzbd would not keep its download folders and categories (it showed: {lastRead}).";
    }

    private const int SabnzbdAttempts = 12;

    /// <summary>Whether SABnzbd's own answer shows both download folders and a folder on each wanted category, and a short plain account of what it showed.</summary>
    private static (bool Holds, string Read) SabnzbdHolds(
        JsonElement? misc, JsonElement? categories, string incomplete, string completed,
        IReadOnlyList<(string Category, string Folder)> wanted, Func<string?, string> norm)
    {
        static JsonElement? Find(JsonElement? root, string section)
            => root is { ValueKind: JsonValueKind.Object } element
               && element.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object
               && config.TryGetProperty(section, out var found) ? found : null;

        var parts = new List<string>();
        var holds = true;
        var settings = Find(misc, "misc");
        string? Setting(string key) => settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        foreach (var (key, expected) in new[] { ("download_dir", incomplete), ("complete_dir", completed) })
        {
            var actual = Setting(key);
            parts.Add($"{key}={(string.IsNullOrEmpty(actual) ? "(none)" : actual)}");
            holds &= string.Equals(norm(actual), norm(expected), StringComparison.OrdinalIgnoreCase);
        }

        var list = Find(categories, "categories");
        foreach (var (category, folder) in wanted)
        {
            string? actual = null;
            if (list is { ValueKind: JsonValueKind.Array } array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name)
                        && string.Equals(name.GetString(), category, StringComparison.OrdinalIgnoreCase))
                    {
                        actual = item.TryGetProperty("dir", out var dir) && dir.ValueKind == JsonValueKind.String ? dir.GetString() : string.Empty;
                        break;
                    }
                }
            }

            parts.Add($"{category}={(actual is null ? "(no category)" : string.IsNullOrEmpty(actual) ? "(no folder)" : actual)}");
            holds &= actual is not null && string.Equals(norm(actual), norm(folder), StringComparison.OrdinalIgnoreCase);
        }

        return (holds, string.Join("; ", parts));
    }

    // ---- NZBGet: read the whole config, change the folders and two categories, save, reload ---------

    private async Task<string?> NzbgetAsync(HttpClient http, DownloadLayout layout, string incomplete, BundledComponentCredentials? credentials, CancellationToken cancellationToken)
    {
        // NZBGet ships with this login (#1061); the installer never changes it.
        var username = string.IsNullOrEmpty(credentials?.Username) ? "nzbget" : credentials!.Username;
        var password = string.IsNullOrEmpty(credentials?.Password) ? "tegbzn6789" : credentials!.Password;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

        async Task<JsonElement> Call(string method, params object[] args)
        {
            using var response = await http.PostAsync("jsonrpc",
                new StringContent(JsonSerializer.Serialize(new { method, @params = args, id = 1 }), Encoding.UTF8, "application/json"), cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.Clone();
        }

        // The save starts from what nzbget.conf holds (loadconfig), never from `config`, which adds NZBGet's runtime-only
        // values (ConfigFile, AppBin, AppDir, Version): saved back, they make NZBGet pause all downloads (#1257). When
        // loadconfig cannot be answered, `config` stands in for it, and either way the runtime-only values are dropped.
        JsonElement result = default;
        foreach (var method in new[] { "loadconfig", "config" })
        {
            try
            {
                var current = await Call(method);
                if (current.TryGetProperty("result", out var found) && found.ValueKind == JsonValueKind.Array)
                {
                    result = found;
                    break;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                // Try the other call.
            }
        }

        if (result.ValueKind != JsonValueKind.Array)
        {
            return "NZBGet did not return its settings.";
        }

        var options = new List<KeyValuePair<string, string>>();
        foreach (var item in result.EnumerateArray())
        {
            if (item.TryGetProperty("Name", out var name) && name.GetString() is { Length: > 0 } key)
            {
                options.Add(new(key, item.TryGetProperty("Value", out var value) ? value.GetString() ?? string.Empty : string.Empty));
            }
        }

        var changes = NzbgetSettings.Plan(options, layout, incomplete);
        // One parameter: the whole list of settings.
        var saved = await Call("saveconfig", new object[] { changes.Select(change => new Dictionary<string, string> { ["Name"] = change.Key, ["Value"] = change.Value }).ToArray() });
        if (!saved.TryGetProperty("result", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            return "NZBGet would not save its settings.";
        }

        // Settings are read once at start; reload makes NZBGet read them now.
        try
        {
            await Call("reload");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            // NZBGet drops the connection while it restarts its own services.
        }

        // NZBGet answers "reload" and keeps serving from the OLD instance for a moment, then restarts: and a
        // restarted NZBGet starts paused again. Resuming against the old instance (as this once did, straight
        // after the first answer) is undone by that restart. So wait until the settings it has loaded in memory
        // are the ones just saved, which only the new instance reports, and only then resume.
        var answered = false;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(_retryDelay, cancellationToken);
            try
            {
                var loaded = await Call("config");
                if (loaded.TryGetProperty("result", out var loadedOptions) && loadedOptions.ValueKind == JsonValueKind.Array)
                {
                    answered = true;
                    if (NzbgetSettings.Applied(loadedOptions, changes))
                    {
                        return await ResumeAsync();
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                // Not back yet.
            }
        }

        // It answered, but never showed the new settings (a path it writes differently): resume anyway, and let the checks below decide.
        if (answered)
        {
            return await ResumeAsync();
        }

        return "NZBGet did not come back after saving its settings.";

        // A fresh NZBGet starts with its download, post-processing and scan queues all paused, which looks like
        // "Paused" in Deluno and downloads nothing. A new install must not be paused: resume them and read the
        // status back until none is, twice in a row, so a restart that undoes it a moment later is caught.
        async Task<string?> ResumeAsync()
        {
            var clean = 0;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                foreach (var method in new[] { "resumedownload", "resumepost", "resumescan" })
                {
                    await Call(method);
                }

                var status = await Call("status");
                if (status.TryGetProperty("result", out var state) && state.ValueKind == JsonValueKind.Object
                    && !IsTrue(state, "DownloadPaused") && !IsTrue(state, "PostPaused") && !IsTrue(state, "ScanPaused") && !IsTrue(state, "ServerPaused"))
                {
                    if (++clean >= 2)
                    {
                        return null;
                    }
                }
                else
                {
                    clean = 0;
                }

                await Task.Delay(_retryDelay, cancellationToken);
            }

            return "NZBGet is still paused. Press Resume in NZBGet.";
        }

        static bool IsTrue(JsonElement state, string name) => state.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields)
        => new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
}

/// <summary>What NZBGet's settings become: its completed and in-progress folders, and a Movies and a TV category with a folder each.</summary>
public static class NzbgetSettings
{
    /// <summary>What NZBGet's <c>config</c> call adds to the settings (#1257). They are not options of nzbget.conf and are never saved.</summary>
    public static readonly string[] RuntimeOnlyOptions = ["ConfigFile", "AppBin", "AppDir", "Version"];

    /// <summary>True when the settings NZBGet has loaded in memory carry the folders just saved: the reload has taken effect.</summary>
    public static bool Applied(JsonElement loaded, IReadOnlyList<KeyValuePair<string, string>> saved)
    {
        static string Same(string value) => value.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in loaded.EnumerateArray())
        {
            if (item.TryGetProperty("Name", out var name) && name.GetString() is { Length: > 0 } key)
            {
                values[key] = item.TryGetProperty("Value", out var value) ? value.GetString() ?? string.Empty : string.Empty;
            }
        }

        foreach (var key in new[] { "DestDir", "InterDir" })
        {
            var want = saved.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (want.Key is null)
            {
                continue;
            }

            if (!values.TryGetValue(key, out var have) || Same(have) != Same(want.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The full settings list with the changes applied (NZBGet's save takes every setting, not just the
    /// changed ones). A category already named Movies or TV is reused. NZBGet's own TV category is called
    /// Series, so that one is renamed rather than left beside a new one. Otherwise the first free slot is used.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Plan(IReadOnlyList<KeyValuePair<string, string>> current, DownloadLayout layout, string incompleteFolder)
    {
        var values = current.Where(item => !RuntimeOnlyOptions.Contains(item.Key, StringComparer.OrdinalIgnoreCase)).ToList();

        void Set(string name, string value)
        {
            var index = values.FindIndex(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                values[index] = new(values[index].Key, value);
            }
            else
            {
                values.Add(new(name, value));
            }
        }

        string? Get(string name) => values.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

        Set("DestDir", layout.CompletedRoot);
        Set("InterDir", incompleteFolder);

        var (movies, tv) = InstalledClientCategories.For("nzbget");
        var claimed = new HashSet<int>();
        int SlotFor(string wanted, params string[] renameFrom)
        {
            var named = values
                .Where(item => item.Key.StartsWith("Category", StringComparison.OrdinalIgnoreCase) && item.Key.EndsWith(".Name", StringComparison.OrdinalIgnoreCase))
                .Select(item => (Slot: int.TryParse(item.Key["Category".Length..^".Name".Length], out var n) ? n : 0, item.Value))
                .Where(item => item.Slot > 0 && !claimed.Contains(item.Slot))
                .ToList();
            var slot = named.FirstOrDefault(item => string.Equals(item.Value, wanted, StringComparison.OrdinalIgnoreCase)).Slot;
            if (slot == 0)
            {
                slot = named.FirstOrDefault(item => renameFrom.Any(old => string.Equals(item.Value, old, StringComparison.OrdinalIgnoreCase))).Slot;
            }

            if (slot == 0)
            {
                slot = 1;
                while (claimed.Contains(slot) || !string.IsNullOrWhiteSpace(Get($"Category{slot}.Name")))
                {
                    slot++;
                }
            }

            claimed.Add(slot);
            return slot;
        }

        var movieSlot = SlotFor(movies);
        var tvSlot = SlotFor(tv, "Series", "TV Shows", "Television");
        Set($"Category{movieSlot}.Name", movies);
        Set($"Category{movieSlot}.DestDir", layout.CompletedFolder("Movies"));
        Set($"Category{tvSlot}.Name", tv);
        Set($"Category{tvSlot}.DestDir", layout.CompletedFolder("TV"));
        return values;
    }
}
