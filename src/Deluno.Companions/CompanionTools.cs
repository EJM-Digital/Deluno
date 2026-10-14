using System.Net;
using Deluno.Contracts;
using Deluno.Infrastructure.Http;
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

    /// <summary>Where the tool answers on this computer (for an "Open" link), or null when that is not known.</summary>
    string? LocalUrl() => null;

    Task RunAsync(CompanionRunContext context, CancellationToken cancellationToken);
}

/// <summary>
/// A tool Deluno installed that it can also bring up to date (#1118): the same download, checksum and install
/// path as the first install, then a restart. <see cref="Project"/> names the release feed it is compared with.
/// </summary>
public interface IUpdatableCompanion : ICompanionTool
{
    string Project { get; }

    /// <summary>The version Deluno installs, or null when it installs whatever the project's latest release is (Weir).</summary>
    string? PinnedVersion { get; }

    /// <summary>Downloads the build, refuses it unless its checksum matches, installs it and starts the tool again.</summary>
    Task UpdateAsync(CompanionRunContext context, CancellationToken cancellationToken);
}

/// <summary>What the download-client tools share: find the client in Deluno, add it, and test it the way a person would.</summary>
public abstract class DownloadClientCompanion(
    ICompanionSystem system,
    IConnectionsRepository connections,
    IInstalledClientLogins logins) : IUpdatableCompanion
{
    public abstract string Project { get; }

    public abstract string? PinnedVersion { get; }

    public abstract Task UpdateAsync(CompanionRunContext context, CancellationToken cancellationToken);

    /// <summary>Downloads the pinned build and checks it, closes the running copy, installs the build over it (elevated, checked again) and leaves it to the caller to start it again.</summary>
    protected async Task ReplaceAsync(CompanionArtifact artifact, string installedPath, string processName, ICompanionDownloader downloader, CompanionRunContext context, CancellationToken cancellationToken)
    {
        if (!IsInstalled())
        {
            throw new CompanionStepException($"Deluno could not find {Name} on this computer to update it.");
        }

        // Downloaded and checked first: a build that does not match is refused while the app is still running.
        context.Progress.Report("download", $"Downloading {Name} {PinnedVersion} from its official release and checking it is that build.");
        var installer = await downloader.DownloadAsync(artifact, context.DownloadFolder, cancellationToken);
        context.Progress.Report("stop", $"Closing {Name}. Its downloads pause until it is back.");
        System.StopProcess(processName);
        await InstallDownloadedAsync(artifact, installer, installedPath, context, cancellationToken);
    }

    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    protected abstract string Protocol { get; }

    protected ICompanionSystem System => system;

    protected IInstalledClientLogins Logins => logins;

    public abstract bool IsInstalled();

    public virtual string? LocalUrl() => null;

    public abstract Task RunAsync(CompanionRunContext context, CancellationToken cancellationToken);

    public async Task<string?> ConnectionNameAsync(CancellationToken cancellationToken)
        => (await connections.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(client => string.Equals(client.Protocol, Protocol, StringComparison.OrdinalIgnoreCase) && IsLoopback(client.Host))
            ?.Name;

    protected async Task InstallAsync(CompanionArtifact artifact, string installedPath, ICompanionDownloader downloader, CompanionRunContext context, CancellationToken cancellationToken)
    {
        context.Progress.Report("download", $"Downloading {Name} from its official release and checking it is that build.");
        var installer = await downloader.DownloadAsync(artifact, context.DownloadFolder, cancellationToken);
        await InstallDownloadedAsync(artifact, installer, installedPath, context, cancellationToken);
    }

    private async Task InstallDownloadedAsync(CompanionArtifact artifact, string installer, string installedPath, CompanionRunContext context, CancellationToken cancellationToken)
    {
        context.Progress.Report("install", $"Installing {Name}. Windows will ask for permission, because it installs for everyone on this computer.");
        // Never straight from the download folder: the installer is copied into a fresh folder only administrators
        // can write, checked there against the pinned hash, run from there, and the folder is deleted (#1061).
        var script = ElevatedInstallScript.Build(installer, artifact.Sha256, "/S", null, asExe: true);
        var result = await system.RunElevatedAsync(ElevatedInstallScript.PowerShellPath, ElevatedInstallScript.PowerShellArguments(script), cancellationToken);
        TryDeleteInstaller(installer);
        if (result.ExitCode is ElevatedInstallScript.HashMismatchExitCode)
        {
            throw new CompanionStepException($"The {Name} installer changed after it was checked, so Deluno did not run it. Nothing was installed.");
        }

        if (result.ExitCode is ElevatedInstallScript.ReparsePointExitCode or ElevatedInstallScript.FolderExitCode)
        {
            throw new CompanionStepException($"Deluno could not make a safe folder to run the {Name} installer from, so it did not run it. Nothing was installed.");
        }

        if (result.Declined)
        {
            throw new CompanionStepException($"Windows asked for permission to install {Name} and it was not given. Nothing was installed.");
        }

        if (result.ExitCode != 0 || !system.FileExists(installedPath))
        {
            throw new CompanionStepException($"The {Name} installer finished with code {result.ExitCode} and {Name} is not where it should be.");
        }
    }

    private static void TryDeleteInstaller(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left behind; it is only the vendor's own installer, and nothing runs it again.
        }
    }

    private static bool IsLoopback(string? host) => DownloadClientConnector.IsLoopback(host);
}

public sealed class QbittorrentCompanion(
    ICompanionSystem system,
    IConnectionsRepository connections,
    IInstalledClientLogins logins,
    ICompanionDownloader downloader) : DownloadClientCompanion(system, connections, logins)
{
    private const string Username = "deluno";

    public override string Id => CompanionToolIds.Qbittorrent;

    public override string Name => "qBittorrent";

    public override string Description => "Torrent client. Installed for this computer, started when you sign in, with a Web UI only this computer can reach.";

    protected override string Protocol => "qbittorrent";

    public override string Project => Deluno.Connections.Versions.ConnectionProjects.Qbittorrent;

    public override string? PinnedVersion => CompanionCatalog.QbittorrentVersion;

    public override async Task UpdateAsync(CompanionRunContext context, CancellationToken cancellationToken)
    {
        await ReplaceAsync(CompanionCatalog.Qbittorrent, Executable, "qbittorrent", downloader, context, cancellationToken);
        context.Progress.Report("start", $"Starting {Name} again.");
        System.Start(Executable, "--confirm-legal-notice");
    }


    private string Executable => Path.Combine(System.ProgramFiles, "qBittorrent", "qbittorrent.exe");

    private string SettingsPath => Path.Combine(System.RoamingAppData, "qBittorrent", "qBittorrent.ini");

    public override bool IsInstalled() => System.FileExists(Executable);
    public override string? LocalUrl()
    {
        var match = global::System.Text.RegularExpressions.Regex.Match(System.ReadText(SettingsPath) ?? string.Empty, @"WebUI\\Port=(\d{2,5})");
        return match.Success ? $"http://localhost:{match.Groups[1].Value}" : null;
    }


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
        Logins.Keep(Id, Username, password);

        context.Progress.Report("start", $"Starting {Name}.");
        System.Start(Executable, "--confirm-legal-notice");
        System.StartAtSignIn("qBittorrent", $"\"{Executable}\"");

        context.Progress.Report("done", $"{Name} is installed and running. Connect it from the setup guide or Connections.");
    }
}

public sealed class SabnzbdCompanion(
    ICompanionSystem system,
    IConnectionsRepository connections,
    IInstalledClientLogins logins,
    ICompanionDownloader downloader) : DownloadClientCompanion(system, connections, logins)
{
    public override string Id => CompanionToolIds.Sabnzbd;

    public override string Name => "SABnzbd";

    public override string Description => "Usenet client. Installed for this computer, started when you sign in, answering only this computer. Add your news server in SABnzbd afterwards.";

    protected override string Protocol => "sabnzbd";

    public override string Project => Deluno.Connections.Versions.ConnectionProjects.Sabnzbd;

    public override string? PinnedVersion => CompanionCatalog.SabnzbdVersion;

    public override async Task UpdateAsync(CompanionRunContext context, CancellationToken cancellationToken)
    {
        await ReplaceAsync(CompanionCatalog.Sabnzbd, Executable, "SABnzbd", downloader, context, cancellationToken);
        context.Progress.Report("start", $"Starting {Name} again.");
        System.Start(Executable, $"-f \"{SettingsPath}\" -b 0");
    }


    private string Executable => Path.Combine(System.ProgramFiles, "SABnzbd", "SABnzbd.exe");

    private string SettingsPath => Path.Combine(System.LocalAppData, "sabnzbd", "sabnzbd.ini");

    public override bool IsInstalled() => System.FileExists(Executable);
    public override string? LocalUrl()
        => int.TryParse(CompanionConfigFiles.SabnzbdValue(System.ReadText(SettingsPath), "port"), out var port) ? $"http://localhost:{port}" : null;


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

        Logins.Keep(Id, null, apiKey);
        var arguments = $"-f \"{SettingsPath}\" -b 0";
        if (!System.IsProcessRunning("SABnzbd"))
        {
            context.Progress.Report("start", $"Starting {Name}.");
            System.Start(Executable, arguments);
        }

        System.StartAtSignIn("SABnzbd", $"\"{Executable}\" {arguments}");
        context.Progress.Report("done", $"{Name} is installed and running. Connect it from the setup guide or Connections.");
    }
}

public sealed class WeirCompanion(
    ICompanionSystem system,
    IProcessorRepository processors,
    IProcessorConnectionService processorConnections,
    ISecurityRepository security,
    ICompanionDownloader downloader,
    IHttpClientFactory httpClientFactory) : IUpdatableCompanion, IWeirConnector
{
    /// <summary>Weir's own default port (W-E-I-R on a phone keypad).</summary>
    private const int WeirDefaultPort = 9347;

    /// <summary>Weir's published readiness route (its docs: GET /ready, no sign-in).</summary>
    public const string WeirReadyPath = "/ready";

    /// <summary>The clock the wait for Weir to answer runs on; a test supplies its own.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How the wait between two looks at Weir passes; a test supplies one that moves its own clock.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public string Id => CompanionToolIds.Weir;

    public string Name => "Weir";

    public string Description => "Cleans finished downloads before Deluno imports them. Installed for your Windows account; no administrator permission needed.";

    public string Project => Deluno.Connections.Versions.ConnectionProjects.Weir;

    /// <summary>Weir is installed from its latest release, not a build pinned in Deluno.</summary>
    public string? PinnedVersion => null;

    /// <summary>
    /// #1118: downloads Weir's latest release, checks it against that release's SHA256SUMS, installs it silently
    /// and starts Weir again if it is not answering. A checksum that does not match stops it before anything runs.
    /// </summary>
    public async Task UpdateAsync(CompanionRunContext context, CancellationToken cancellationToken)
    {
        if (!IsInstalled())
        {
            throw new CompanionStepException($"Deluno could not find {Name} on this computer to update it.");
        }

        var artifact = await LatestReleaseAsync(context, cancellationToken);
        context.Progress.Report("download", $"Downloading {Name} {artifact.FileName} and checking it against the checksum its release publishes.");
        var setup = await downloader.DownloadAsync(artifact, context.DownloadFolder, cancellationToken);
        context.Progress.Report("install", $"Installing the new {Name} for your Windows account. It restarts when it is done.");
        var result = await system.RunAsync(setup, "--silent", cancellationToken);
        try
        {
            File.Delete(setup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        if (result.ExitCode != 0)
        {
            throw new CompanionStepException($"The {Name} installer finished with code {result.ExitCode}, so {Name} was not updated.");
        }

        context.Progress.Report("start", Deluno.Contracts.WeirStartWait.Notice);
        _ = await FindRunningAsync(null, cancellationToken)
            ?? throw new CompanionStepException($"{Name} was updated but did not start answering after waiting {Deluno.Contracts.WeirStartWait.BudgetSeconds / 60} minutes. Start it from the Start menu.");
    }

    private string Executable => Path.Combine(system.LocalAppData, "Weir", "Weir.exe");

    /// <summary>
    /// What Deluno starts Weir with (#553). Never the browser: Deluno connects to
    /// Weir through its API, and the computer Deluno runs on may have nobody at
    /// it, so a window there would only be left behind. The port only on the
    /// first start of a copy Deluno just installed; Weir saves it.
    /// </summary>
    public static string StartArguments(int? port) =>
        port is { } chosen ? $"--port {chosen} --no-browser" : "--no-browser";

    public bool IsInstalled() => system.FileExists(Executable) || system.FileExists(Path.Combine(system.LocalAppData, "Weir", "current", "Weir.exe"));
    public string? LocalUrl()
        => system.ListeningPortsOf(Path.Combine(system.LocalAppData, "Weir")).OrderBy(port => port).Select(port => $"http://localhost:{port}").FirstOrDefault();


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

        string? firstStart = null;
        // Another Weir running on this computer is no reason not to install one
        // for this account. Weir's installer stopped every copy on the
        // computer; Weir's stops only its own (Weir #618), and Weir chooses its
        // port once and keeps it. The rig runs two Weir servers of its own and
        // refusing beside them was wrong. If this account's Weir shares a home
        // that already has an administrator, the sign-in below says so.
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

            // A silent install does not start Weir, and Weir's first start on a
            // desktop asks which port to use. Its unattended answer is --port,
            // which it saves: Deluno gives it its own default, or the first free
            // port above it.
            firstStart = StartArguments(CompanionConfigFiles.FreePort(system, WeirDefaultPort));
        }
        else
        {
            context.Progress.Report("install", $"{Name} is already installed, so it is not installed again.");
        }

        context.Progress.Report("start", Deluno.Contracts.WeirStartWait.Notice);
        _ = await FindRunningAsync(firstStart, cancellationToken)
            ?? throw new CompanionStepException($"{Name} is installed but did not start answering after waiting {Deluno.Contracts.WeirStartWait.BudgetSeconds / 60} minutes. Start it from the Start menu and run this again.");

        context.Progress.Report("done", $"{Name} is installed and running. Create your own {Name} account and connect it from the setup guide or Connections.");
    }

    /// <summary>
    /// Whether Weir has an account yet, asked of the Weir this account installed if it is answering
    /// right now. Never starts Weir and never waits: a status screen must stay quick.
    /// </summary>
    public async Task<WeirAccountState> AccountStateAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalled())
        {
            return new WeirAccountState(false, false, false);
        }

        var installFolder = Path.Combine(system.LocalAppData, "Weir");
        using var probe = new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler()) { Timeout = TimeSpan.FromSeconds(3) };
        foreach (var port in system.ListeningPortsOf(installFolder))
        {
            try
            {
                var status = await probe.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:{port}/api/v1/auth/bootstrap/status", cancellationToken);
                if (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("bootstrap_allowed", out var allowed))
                {
                    var open = allowed.ValueKind == JsonValueKind.True;
                    return new WeirAccountState(true, open, !open);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
            {
            }
        }

        return new WeirAccountState(true, false, false);
    }

    /// <summary>
    /// Links Weir and Deluno with the account the person gives here: creates it when Weir has none
    /// yet, otherwise signs in with it, then makes Deluno's API key, registers Deluno in Weir and adds
    /// Weir as Deluno's processor. The username and password live in memory for this call only: they
    /// are never stored, logged, written to Activity, or put in an exception's text.
    /// </summary>
    public async Task<WeirConnectOutcome> ConnectAsync(string? username, string? password, string delunoBaseUrl, CancellationToken cancellationToken)
    {
        if (!IsInstalled())
        {
            return new WeirConnectOutcome(false, $"Deluno could not find {Name} on this computer to connect it.", false);
        }

        if (await ConnectionNameAsync(cancellationToken) is not null)
        {
            return new WeirConnectOutcome(true, $"{Name} is already connected to Deluno.", false);
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return new WeirConnectOutcome(false, $"Enter the username and password for your {Name} account.", true);
        }

        var baseUrl = await FindRunningAsync(null, cancellationToken);
        if (baseUrl is null)
        {
            return new WeirConnectOutcome(false, $"{Name} is installed but did not start answering after waiting {Deluno.Contracts.WeirStartWait.BudgetSeconds / 60} minutes. Start it from the Start menu and try again.", false);
        }

        ProcessorConnectionItem? processor = null;
        using var cookies = new SocketsHttpHandler { ConnectCallback = Deluno.Infrastructure.Http.LoopbackFirstConnection.ConnectAsync, CookieContainer = new CookieContainer(), UseCookies = true };
        using var http = new HttpClient(cookies) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var status = await http.GetFromJsonAsync<JsonElement>("/api/v1/auth/bootstrap/status", cancellationToken);
            if (status.TryGetProperty("bootstrap_allowed", out var allowed) && allowed.ValueKind == JsonValueKind.True)
            {
                if (!await TryPostAccountAsync(http, "/api/v1/auth/bootstrap", csrf => new { username = username.Trim(), password, csrf_token = csrf }, cancellationToken))
                {
                    return new WeirConnectOutcome(false, $"{Name} did not accept those details for a new account. Check {Name}'s password rules and try again.", false);
                }
            }

            if (!await TryPostAccountAsync(http, "/api/v1/auth/login", csrf => new { username = username.Trim(), password, csrf_token = csrf }, cancellationToken))
            {
                return new WeirConnectOutcome(false, $"{Name} did not accept that username and password.", false);
            }

            var key = await security.CreateApiKeyAsync(new CreateApiKeyRequest("Weir (set up by Deluno)", "read,imports"), cancellationToken);
            var manager = await PostAsync(http, "/api/v1/media-managers/connections",
                csrf => new { csrf_token = csrf, kind = "deluno", name = "Deluno", base_url = delunoBaseUrl, api_key = key.ApiKey, enabled = true },
                cancellationToken);
            var managerId = manager.GetProperty("id").GetInt32();
            var secret = await PostAsync(http, $"/api/v1/media-managers/connections/{managerId}/webhook-secret", csrf => new { csrf_token = csrf }, cancellationToken);

            processor = await AddProcessorConnectionAsync(
                baseUrl,
                secret.GetProperty("webhook_url_path").GetString()!,
                secret.TryGetProperty("header_name", out var header) ? header.GetString() : "X-Webhook-Secret",
                secret.GetProperty("webhook_secret").GetString(),
                cancellationToken);
        }
        catch (CompanionStepException exception)
        {
            return new WeirConnectOutcome(false, exception.Message, false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            // Never the exception's text: the request that failed carried the person's password.
            return new WeirConnectOutcome(false, $"{Name} did not answer as expected. Check it is running and try again.", false);
        }

        return new WeirConnectOutcome(true, $"{Name} is connected to Deluno both ways. Libraries that refine before import now hand their downloads to it.", false, processor?.Id, processor?.Name);
    }
    /// <summary>
    /// Adds Weir as a processor connection with Weir's own health address, tests it, and says
    /// so when the test does not come back healthy. The connection is kept either way.
    /// </summary>
    public async Task<ProcessorConnectionItem> AddProcessorConnectionAsync(string baseUrl, string webhookUrlPath, string? headerName, string? webhookSecret, CancellationToken cancellationToken)
    {
        var machineName = await processorConnections.GetMachineNameAsync($"{baseUrl}{webhookUrlPath}", headerName, webhookSecret, cancellationToken);
        // Its own name when it says one; else the name this setup installed it under (#1144), so it is never listed as a generic processor.
        var productName = await processorConnections.GetProductNameAsync($"{baseUrl}{webhookUrlPath}", headerName, webhookSecret, cancellationToken) ?? Name;
        var connection = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest(
                Name,
                "generic-webhook",
                $"{baseUrl}{webhookUrlPath}",
                headerName,
                webhookSecret,
                true,
                // Weir publishes GET /ready ({"ready": true} once its database is open) at
                // its root. Its hand-off route only takes POST, so this is how Deluno asks
                // whether it is up (#1055). The generic processor test just follows the address.
                $"{baseUrl}{WeirReadyPath}") { MachineName = machineName, ProductName = productName },
            cancellationToken);
        var test = await processorConnections.TestAsync(connection, cancellationToken);
        await processors.RecordProcessorConnectionHealthAsync(connection.Id, test.Status, test.Message, cancellationToken);
        if (test.Status != "healthy")
        {
            throw new CompanionStepException($"{Name} is installed and added to Deluno, but its test did not pass: {test.Message}");
        }

        return connection;
    }

    private async Task<CompanionArtifact> LatestReleaseAsync(CompanionRunContext context, CancellationToken cancellationToken)
    {
        context.Progress.Report("download", $"Finding {Name}'s latest release.");
        var http = httpClientFactory.CreateClient(CompanionDownloader.ClientName);
        try
        {
            // The free route first (#1181): the newest tags, release candidates included and drafts never, from the
            // release feed on github.com, which GitHub's 60 API calls an hour do not cover; the release files
            // are then fetched from the release download address. The API is only for a feed that cannot be read.
            // The newest release is not always the newest one with its files (rc.17 was published with none, #1264), so
            // the walk is the one the tray's picker uses: the newest of the last few releases that has its installer
            // and checksum file, and one plain sentence naming the newest looked at when none does.
            ReleaseFiles? files = null;
            var feedTags = await TagsFromReleaseFeedAsync(http, cancellationToken);
            if (feedTags is not null)
            {
                var walk = await Deluno.Contracts.ReleaseFallback.WalkAsync(feedTags, null,
                    tag => Deluno.Contracts.ReleaseFallback.TryDownloadFolderAsync(http, CompanionCatalog.WeirRepository, tag, CompanionCatalog.WeirSetupAsset, CompanionCatalog.WeirChecksumsAsset, ChecksumFor, probeInstaller: true, cancellationToken));
                if (!walk.Unreadable)
                {
                    files = walk.Value ?? throw NoInstallerYet(walk.NewestLooked);
                }
            }

            if (files is null)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{CompanionCatalog.WeirRepository}/releases?per_page=100");
                    request.Headers.UserAgent.ParseAdd("Deluno");
                    using var response = await http.SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    // Newest by version order, release candidates included, drafts never (#1123).
                    var releases = Deluno.Contracts.ReleaseVersions.GitHubReleasesNewestFirst(await response.Content.ReadAsStringAsync(cancellationToken), includePreReleases: true)
                        .Where(release => release.TryGetProperty("tag_name", out var name) && name.ValueKind == JsonValueKind.String)
                        .ToDictionary(release => release.GetProperty("tag_name").GetString()!, StringComparer.Ordinal);
                    if (releases.Count == 0)
                    {
                        throw new CompanionStepException($"{Name} has no published release for Deluno to install.");
                    }

                    var walk = await Deluno.Contracts.ReleaseFallback.WalkAsync(releases.Keys, null, async tag =>
                    {
                        var assets = releases[tag].TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : [];
                        var setup = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString() == CompanionCatalog.WeirSetupAsset);
                        var sums = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString() == CompanionCatalog.WeirChecksumsAsset);
                        if (setup.ValueKind != JsonValueKind.Object)
                        {
                            return ReleaseAttempt<ReleaseFiles>.Skip($"{tag} has no {CompanionCatalog.WeirSetupAsset} yet");
                        }

                        if (sums.ValueKind != JsonValueKind.Object)
                        {
                            return ReleaseAttempt<ReleaseFiles>.Skip($"{tag} has no {CompanionCatalog.WeirChecksumsAsset} yet");
                        }

                        using var sumsResponse = await http.GetAsync(sums.GetProperty("browser_download_url").GetString()!, cancellationToken);
                        if (sumsResponse.StatusCode == HttpStatusCode.NotFound)
                        {
                            return ReleaseAttempt<ReleaseFiles>.Skip($"{tag} has no {CompanionCatalog.WeirChecksumsAsset} yet");
                        }

                        sumsResponse.EnsureSuccessStatusCode();
                        return ChecksumFor(await sumsResponse.Content.ReadAsStringAsync(cancellationToken), CompanionCatalog.WeirSetupAsset) is { } hash
                            ? ReleaseAttempt<ReleaseFiles>.Found(new ReleaseFiles(setup.GetProperty("browser_download_url").GetString()!, hash))
                            : ReleaseAttempt<ReleaseFiles>.Skip($"{tag}'s {CompanionCatalog.WeirChecksumsAsset} does not list {CompanionCatalog.WeirSetupAsset}");
                    });
                    files = walk.Value ?? throw NoInstallerYet(walk.NewestLooked);
                }
                catch (Deluno.Infrastructure.Http.GitHubRateLimitException limit)
                {
                    // Both routes failed and the API said why: GitHub's hourly allowance is used up (#1181).
                    throw new CompanionStepException(limit.Message);
                }
            }

            return new CompanionArtifact(CompanionCatalog.WeirSetupAsset, new Uri(files.AssetUrl), files.Sha256!, MustBeSigned: false, MinimumBytes: 5_000_000);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new CompanionStepException($"Deluno could not reach GitHub to find {Name}'s latest release. Check this computer's internet connection.");
        }

        CompanionStepException NoInstallerYet(string? newestLooked)
            => new(Deluno.Contracts.ReleaseFallback.NoInstallerYetSentence(Name, newestLooked));

        static string? ChecksumFor(string checksums, string asset)
            => checksums.Split('\n')
                .Select(line => line.Trim().Split((char[])[' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(parts => parts.Length == 2 && parts[1] == asset)?[0];
    }

    /// <summary>
    /// The published releases' tags, newest first by version order, release candidates included and drafts never (a draft
    /// is not in the public feed), from the release feed on github.com. Null when the feed cannot be read.
    /// </summary>
    private async Task<IReadOnlyList<string>?> TagsFromReleaseFeedAsync(HttpClient http, CancellationToken cancellationToken)
    {
        string atom;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{CompanionCatalog.WeirRepository}/releases.atom");
            request.Headers.UserAgent.ParseAdd("Deluno");
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A private or missing repository: the API would answer 404 too and still spend one of the hourly calls (#1181).
                throw new CompanionStepException($"Couldn't check for {Name}'s latest release: the release feed isn't reachable (GitHub says it isn't there, so the repository may be private).");
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            atom = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var tags = Deluno.Contracts.ReleaseAtomFeed.TagsNewestFirst(atom, includePreReleases: true);
        return tags.Count == 0 ? null : tags;
    }

    /// <summary>Reads the tags out of a GitHub <c>releases.atom</c> feed and returns the highest by version order; null when none is readable.</summary>
    public static string? NewestTagInReleaseFeed(string atom)
        => Deluno.Contracts.ReleaseAtomFeed.NewestTag(atom);

    /// <summary>
    /// The Weir this account installed, answering.
    ///
    /// <para>Found by the process that owns the port, whatever port Weir chose,
    /// not by the first port that answers like Weir: another copy running on
    /// the same computer - a portable one, or someone else's - answered first,
    /// and Deluno would have set itself up against that one (seen on the rig).
    /// An installed copy that is not running is started.</para>
    /// </summary>
    /// <param name="firstStart">Arguments for the first start of a copy Deluno just installed, which is started at once; null for one already installed, which is started only if it is not answering.</param>
    private async Task<string?> FindRunningAsync(string? firstStart, CancellationToken cancellationToken)
    {
        using var probe = new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler()) { Timeout = TimeSpan.FromSeconds(3) };
        var installFolder = Path.Combine(system.LocalAppData, "Weir");
        var started = false;
        // Weir's supervised start can retry with a back-off from 2 s to 60 s, so it is given three minutes (#1258).
        var deadline = Time.GetUtcNow() + Deluno.Contracts.WeirStartWait.Budget;
        for (var attempt = 0; ; attempt++)
        {
            var ports = system.ListeningPortsOf(installFolder);
            if (ports.Count == 0 && !started && (firstStart is not null || attempt >= 3))
            {
                var executable = system.FileExists(Path.Combine(installFolder, "current", "Weir.exe"))
                    ? Path.Combine(installFolder, "current", "Weir.exe")
                    : Executable;
                system.Start(executable, firstStart ?? StartArguments(port: null));
                started = true;
            }

            foreach (var port in ports)
            {
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

            if (Time.GetUtcNow() >= deadline)
            {
                return null;
            }

            await Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    /// <summary>Posts the person's account details. False when Weir refuses them; never keeps or reports what was sent or what Weir answered.</summary>
    private static async Task<bool> TryPostAccountAsync(HttpClient http, string path, Func<string, object> body, CancellationToken cancellationToken)
    {
        var csrf = (await http.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf", cancellationToken)).GetProperty("csrf_token").GetString()!;
        using var response = await http.PostSizedJsonAsync(path, body(csrf), cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        if ((int)response.StatusCode is >= 400 and < 500)
        {
            return false;
        }

        throw new CompanionStepException($"Weir could not be reached properly to check your account (HTTP {(int)response.StatusCode}). Try again in a moment.");
    }

    private static async Task<JsonElement> PostAsync(HttpClient http, string path, Func<string, object> body, CancellationToken cancellationToken)
    {
        var csrf = (await http.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf", cancellationToken)).GetProperty("csrf_token").GetString()!;
        using var response = await http.PostSizedJsonAsync(path, body(csrf), cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new CompanionStepException($"Weir refused {path} with HTTP {(int)response.StatusCode}: {text[..Math.Min(text.Length, 300)]}");
        }

        return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
