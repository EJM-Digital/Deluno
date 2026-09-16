using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Security.Contracts;
using Deluno.Security.Data;

namespace Deluno.Companions;

/// <summary>Where a step writes what it is doing, in words the screen shows.</summary>
public interface ICompanionProgress
{
    void Report(string step, string message);
}

public sealed class CompanionStepException(string message) : Exception(message);

/// <param name="DelunoBaseUrl">How the tool reaches Deluno back, when it needs to.</param>
public sealed record CompanionRunContext(string DelunoBaseUrl, string DownloadFolder, ICompanionProgress Progress);

public interface ICompanionTool
{
    string Id { get; }

    string Name { get; }

    string Description { get; }

    bool IsInstalled();

    Task<string?> ConnectionNameAsync(CancellationToken cancellationToken);

    Task RunAsync(CompanionRunContext context, CancellationToken cancellationToken);
}

/// <summary>What the download-client tools share: find the client in Deluno, add it, and test it the way a person would.</summary>
public abstract class DownloadClientCompanion(
    ICompanionSystem system,
    IConnectionsRepository connections,
    ICompanionLoginStore logins) : ICompanionTool
{
    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    protected abstract string Protocol { get; }

    protected ICompanionSystem System => system;

    protected ICompanionLoginStore Logins => logins;

    public abstract bool IsInstalled();

    public abstract Task RunAsync(CompanionRunContext context, CancellationToken cancellationToken);

    public async Task<string?> ConnectionNameAsync(CancellationToken cancellationToken)
        => (await connections.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(client => string.Equals(client.Protocol, Protocol, StringComparison.OrdinalIgnoreCase) && IsLoopback(client.Host))
            ?.Name;

    protected async Task InstallAsync(CompanionArtifact artifact, string installedPath, ICompanionDownloader downloader, CompanionRunContext context, CancellationToken cancellationToken)
    {
        context.Progress.Report("download", $"Downloading {Name} from its official release and checking it is that build.");
        var installer = await downloader.DownloadAsync(artifact, context.DownloadFolder, cancellationToken);

        context.Progress.Report("install", $"Installing {Name}. Windows will ask for permission, because it installs for everyone on this computer.");
        var result = await system.RunElevatedAsync(installer, "/S", cancellationToken);
        if (result.Declined)
        {
            throw new CompanionStepException($"Windows asked for permission to install {Name} and it was not given. Nothing was installed.");
        }

        if (result.ExitCode != 0 || !system.FileExists(installedPath))
        {
            throw new CompanionStepException($"The {Name} installer finished with code {result.ExitCode} and {Name} is not where it should be.");
        }
    }

    protected async Task ConnectAsync(string host, int port, string? username, string secret, string moviesCategory, string tvCategory, CompanionRunContext context, CancellationToken cancellationToken)
    {
        context.Progress.Report("connect", $"Connecting {Name} to Deluno.");
        var existing = (await connections.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(client => string.Equals(client.Protocol, Protocol, StringComparison.OrdinalIgnoreCase) && IsLoopback(client.Host));
        var client = existing ?? await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest(Name, Protocol, host, port, username, secret, null, moviesCategory, tvCategory, null, 10, true),
            cancellationToken);

        // Waiting for it to answer, then the same test the drawer runs.
        (string healthStatus, string message, string? failureCategory) test = ("unreachable", "Not tested.", null);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            test = await ConnectionsEndpointRouteBuilderExtensions.TestDownloadClientAsync(client, cancellationToken);
            if (test.healthStatus == "healthy")
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        await connections.UpdateDownloadClientHealthAsync(client.Id, test.healthStatus, test.message, test.failureCategory, null, cancellationToken);
        if (test.healthStatus != "healthy")
        {
            throw new CompanionStepException($"{Name} is installed and added to Deluno, but its test did not pass: {test.message}");
        }

        context.Progress.Report("done", $"{Name} is installed, running and connected. Its test passed.");
    }

    private static bool IsLoopback(string? host)
        => host is "127.0.0.1" or "localhost" or "::1";
}

public sealed class QbittorrentCompanion(
    ICompanionSystem system,
    IConnectionsRepository connections,
    ICompanionLoginStore logins,
    ICompanionDownloader downloader) : DownloadClientCompanion(system, connections, logins)
{
    private const string Username = "deluno";

    public override string Id => CompanionToolIds.Qbittorrent;

    public override string Name => "qBittorrent";

    public override string Description => "Torrent client. Installed for this computer, started when you sign in, with a Web UI only this computer can reach.";

    protected override string Protocol => "qbittorrent";

    private string Executable => Path.Combine(System.ProgramFiles, "qBittorrent", "qbittorrent.exe");

    private string SettingsPath => Path.Combine(System.RoamingAppData, "qBittorrent", "qBittorrent.ini");

    public override bool IsInstalled() => System.FileExists(Executable);

    public override async Task RunAsync(CompanionRunContext context, CancellationToken cancellationToken)
    {
        if (await ConnectionNameAsync(cancellationToken) is { } already)
        {
            context.Progress.Report("done", $"{Name} is already connected to Deluno as “{already}”.");
            return;
        }

        if (!IsInstalled())
        {
            await InstallAsync(CompanionCatalog.Qbittorrent, Executable, downloader, context, cancellationToken);
        }
        else
        {
            context.Progress.Report("install", $"{Name} is already installed, so it is not installed again.");
        }

        var settings = System.ReadText(SettingsPath);
        if (CompanionConfigFiles.QbittorrentHasWebUiLogin(settings))
        {
            throw new CompanionStepException($"{Name} already has a Web UI login that Deluno does not know. Add it under Download Clients with that login.");
        }

        if (System.IsProcessRunning("qbittorrent"))
        {
            throw new CompanionStepException($"Close {Name} and run this again: its Web UI has to be switched on while it is closed.");
        }

        context.Progress.Report("configure", $"Switching on {Name}'s Web UI for this computer only, with a login Deluno generates.");
        var port = CompanionConfigFiles.FreePort(System, 8080);
        var password = CompanionConfigFiles.NewPassword();
        System.WriteText(SettingsPath, (settings is null ? string.Empty : settings.TrimEnd() + Environment.NewLine + Environment.NewLine)
            + CompanionConfigFiles.QbittorrentIni(port, Username, CompanionConfigFiles.QbittorrentPasswordHash(password)));
        await Logins.SaveAsync(new CompanionLogin(Id, $"http://127.0.0.1:{port}", Username, password, "Password"), cancellationToken);

        context.Progress.Report("start", $"Starting {Name}.");
        System.Start(Executable, "--confirm-legal-notice");
        System.StartAtSignIn("qBittorrent", $"\"{Executable}\"");

        await ConnectAsync("127.0.0.1", port, Username, password, "deluno-movies", "deluno-tv", context, cancellationToken);
    }
}

public sealed class SabnzbdCompanion(
    ICompanionSystem system,
    IConnectionsRepository connections,
    ICompanionLoginStore logins,
    ICompanionDownloader downloader) : DownloadClientCompanion(system, connections, logins)
{
    public override string Id => CompanionToolIds.Sabnzbd;

    public override string Name => "SABnzbd";

    public override string Description => "Usenet client. Installed for this computer, started when you sign in, answering only this computer. Add your news server in SABnzbd afterwards.";

    protected override string Protocol => "sabnzbd";

    private string Executable => Path.Combine(System.ProgramFiles, "SABnzbd", "SABnzbd.exe");

    private string SettingsPath => Path.Combine(System.LocalAppData, "sabnzbd", "sabnzbd.ini");

    public override bool IsInstalled() => System.FileExists(Executable);

    public override async Task RunAsync(CompanionRunContext context, CancellationToken cancellationToken)
    {
        if (await ConnectionNameAsync(cancellationToken) is { } already)
        {
            context.Progress.Report("done", $"{Name} is already connected to Deluno as “{already}”.");
            return;
        }

        if (!IsInstalled())
        {
            await InstallAsync(CompanionCatalog.Sabnzbd, Executable, downloader, context, cancellationToken);
        }
        else
        {
            context.Progress.Report("install", $"{Name} is already installed, so it is not installed again.");
        }

        // An existing SABnzbd keeps its own settings; Deluno reads its API key.
        var settings = System.ReadText(SettingsPath);
        var apiKey = CompanionConfigFiles.SabnzbdValue(settings, "api_key");
        var port = int.TryParse(CompanionConfigFiles.SabnzbdValue(settings, "port"), out var configured) ? configured : 0;
        if (apiKey is null)
        {
            if (System.IsProcessRunning("SABnzbd"))
            {
                throw new CompanionStepException($"Close {Name} and run this again, so Deluno can give it settings before it starts.");
            }

            context.Progress.Report("configure", $"Giving {Name} its first settings: this computer only, with an API key Deluno generates.");
            port = CompanionConfigFiles.FreePort(System, 8085);
            apiKey = CompanionConfigFiles.NewHexKey();
            System.WriteText(SettingsPath, CompanionConfigFiles.SabnzbdIni(port, apiKey, CompanionConfigFiles.NewHexKey()));
        }

        await Logins.SaveAsync(new CompanionLogin(Id, $"http://127.0.0.1:{port}", null, apiKey, "API key"), cancellationToken);
        var arguments = $"-f \"{SettingsPath}\" -b 0";
        if (!System.IsProcessRunning("SABnzbd"))
        {
            context.Progress.Report("start", $"Starting {Name}.");
            System.Start(Executable, arguments);
        }

        System.StartAtSignIn("SABnzbd", $"\"{Executable}\" {arguments}");
        await ConnectAsync("127.0.0.1", port, null, apiKey, "Movies", "TV", context, cancellationToken);
    }
}

public sealed class WeirCompanion(
    ICompanionSystem system,
    IProcessorRepository processors,
    IProcessorConnectionService processorConnections,
    ISecurityRepository security,
    ICompanionLoginStore logins,
    ICompanionDownloader downloader,
    IHttpClientFactory httpClientFactory) : ICompanionTool
{
    private const string AdminName = "deluno-admin";

    public string Id => CompanionToolIds.Weir;

    public string Name => "Weir";

    public string Description => "Cleans finished downloads before Deluno imports them. Installed for your Windows account; no administrator permission needed.";

    private string Executable => Path.Combine(system.LocalAppData, "Weir", "Weir.exe");

    public bool IsInstalled() => system.FileExists(Executable) || system.FileExists(Path.Combine(system.LocalAppData, "Weir", "current", "Weir.exe"));

    public async Task<string?> ConnectionNameAsync(CancellationToken cancellationToken)
        => (await processors.ListProcessorConnectionsAsync(cancellationToken))
            .FirstOrDefault(connection => connection.SubmissionUrl.Contains("/intake/webhook/", StringComparison.OrdinalIgnoreCase))
            ?.Name;

    public async Task RunAsync(CompanionRunContext context, CancellationToken cancellationToken)
    {
        if (await ConnectionNameAsync(cancellationToken) is { } already)
        {
            context.Progress.Report("done", $"{Name} is already connected to Deluno as “{already}”.");
            return;
        }

        if (!IsInstalled())
        {
            var artifact = await LatestReleaseAsync(context, cancellationToken);
            context.Progress.Report("download", $"Downloading {Name} {artifact.FileName} and checking it against the checksum its release publishes.");
            var setup = await downloader.DownloadAsync(artifact, context.DownloadFolder, cancellationToken);
            context.Progress.Report("install", $"Installing {Name} for your Windows account.");
            var result = await system.RunAsync(setup, "--silent", cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new CompanionStepException($"The {Name} installer finished with code {result.ExitCode}.");
            }
        }
        else
        {
            context.Progress.Report("install", $"{Name} is already installed, so it is not installed again.");
        }

        context.Progress.Report("start", $"Waiting for {Name} to start.");
        var baseUrl = await FindRunningAsync(cancellationToken)
            ?? throw new CompanionStepException($"{Name} is installed but did not start answering. Start it from the Start menu and run this again.");

        using var cookies = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        using var http = new HttpClient(cookies) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        var status = await http.GetFromJsonAsync<JsonElement>("/api/v1/auth/bootstrap/status", cancellationToken);
        var stored = await logins.GetAsync(Id, cancellationToken);
        if (status.TryGetProperty("bootstrap_allowed", out var allowed) && allowed.GetBoolean())
        {
            context.Progress.Report("configure", $"Creating {Name}'s administrator login. Deluno keeps it, encrypted, and shows it when you ask.");
            var password = CompanionConfigFiles.NewPassword();
            await PostAsync(http, "/api/v1/auth/bootstrap", csrf => new { username = AdminName, password, csrf_token = csrf }, cancellationToken);
            stored = new CompanionLogin(Id, baseUrl, AdminName, password, "Password");
            await logins.SaveAsync(stored, cancellationToken);
        }
        else if (stored is null)
        {
            throw new CompanionStepException($"{Name} already has an administrator Deluno does not know. Connect it under Media Management → Processor connections.");
        }

        await PostAsync(http, "/api/v1/auth/login", csrf => new { username = stored.Username, password = stored.Secret, csrf_token = csrf }, cancellationToken);

        context.Progress.Report("connect", $"Connecting {Name} and Deluno to each other.");
        var key = await security.CreateApiKeyAsync(new CreateApiKeyRequest("Weir (set up by Deluno)", "read,imports"), cancellationToken);
        var manager = await PostAsync(http, "/api/v1/media-managers/connections",
            csrf => new { csrf_token = csrf, kind = "deluno", name = "Deluno", base_url = context.DelunoBaseUrl, api_key = key.ApiKey, enabled = true },
            cancellationToken);
        var managerId = manager.GetProperty("id").GetInt32();
        var secret = await PostAsync(http, $"/api/v1/media-managers/connections/{managerId}/webhook-secret", csrf => new { csrf_token = csrf }, cancellationToken);

        var connection = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest(
                Name,
                "generic-webhook",
                $"{baseUrl}{secret.GetProperty("webhook_url_path").GetString()}",
                secret.TryGetProperty("header_name", out var header) ? header.GetString() : "X-Webhook-Secret",
                secret.GetProperty("webhook_secret").GetString(),
                true),
            cancellationToken);
        var test = await processorConnections.TestAsync(connection, cancellationToken);
        await processors.RecordProcessorConnectionHealthAsync(connection.Id, test.Status, test.Message, cancellationToken);
        if (!test.IsReachable)
        {
            throw new CompanionStepException($"{Name} is installed and added to Deluno, but its test did not pass: {test.Message}");
        }

        context.Progress.Report("done", $"{Name} is installed, running and connected both ways. Choose it as the processor on a library that should be cleaned before import.");
    }

    private async Task<CompanionArtifact> LatestReleaseAsync(CompanionRunContext context, CancellationToken cancellationToken)
    {
        context.Progress.Report("download", $"Finding {Name}'s latest release.");
        var http = httpClientFactory.CreateClient(CompanionDownloader.ClientName);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{CompanionCatalog.WeirRepository}/releases/latest");
            request.Headers.UserAgent.ParseAdd("Deluno");
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var assets = release.RootElement.GetProperty("assets").EnumerateArray().ToArray();
            var setup = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString() == CompanionCatalog.WeirSetupAsset);
            var sums = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString() == CompanionCatalog.WeirChecksumsAsset);
            if (setup.ValueKind != JsonValueKind.Object || sums.ValueKind != JsonValueKind.Object)
            {
                throw new CompanionStepException($"{Name}'s latest release does not publish {CompanionCatalog.WeirSetupAsset} with a checksum, so Deluno will not install it.");
            }

            var checksums = await http.GetStringAsync(sums.GetProperty("browser_download_url").GetString(), cancellationToken);
            var hash = checksums.Split('\n')
                .Select(line => line.Trim().Split((char[])[' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(parts => parts.Length == 2 && parts[1] == CompanionCatalog.WeirSetupAsset)?[0]
                ?? throw new CompanionStepException($"{Name}'s checksum file does not list {CompanionCatalog.WeirSetupAsset}, so Deluno will not install it.");
            return new CompanionArtifact(CompanionCatalog.WeirSetupAsset, new Uri(setup.GetProperty("browser_download_url").GetString()!), hash, MustBeSigned: false, MinimumBytes: 5_000_000);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new CompanionStepException($"Deluno could not reach GitHub to find {Name}'s latest release. Check this computer's internet connection.");
        }
    }

    private async Task<string?> FindRunningAsync(CancellationToken cancellationToken)
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (var attempt = 0; attempt < 40; attempt++)
        {
            for (var port = 8788; port <= 8807; port++)
            {
                if (!system.IsPortInUse(port))
                {
                    continue;
                }

                try
                {
                    using var response = await probe.GetAsync($"http://127.0.0.1:{port}/api/v1/auth/bootstrap/status", cancellationToken);
                    if (response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync(cancellationToken)).Contains("bootstrap_allowed", StringComparison.Ordinal))
                    {
                        return $"http://127.0.0.1:{port}";
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return null;
    }

    private static async Task<JsonElement> PostAsync(HttpClient http, string path, Func<string, object> body, CancellationToken cancellationToken)
    {
        var csrf = (await http.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf", cancellationToken)).GetProperty("csrf_token").GetString()!;
        using var response = await http.PostAsJsonAsync(path, body(csrf), cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new CompanionStepException($"Weir refused {path} with HTTP {(int)response.StatusCode}: {text[..Math.Min(text.Length, 300)]}");
        }

        return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
