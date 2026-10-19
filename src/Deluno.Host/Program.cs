using Deluno.Api;
using Deluno.Api.Backup;
using Deluno.Api.Downloads;
using Deluno.Api.Monitoring;
using Deluno.Api.Network;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations;
using Deluno.Intake;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search;
using Deluno.Jobs;
using Deluno.Libraries;
using Deluno.Movies;
using Deluno.Connections;
using Deluno.Notifications;
using Deluno.Platform;
using Deluno.Quality;
using Deluno.Recovery;
using Deluno.Security;
using Deluno.Security.Hardening;
using Microsoft.AspNetCore.HttpOverrides;
using Deluno.Realtime;
using Deluno.Series;
using Deluno.Worker;
using Microsoft.AspNetCore.DataProtection;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});

// Console logging stays (docker logs reads it), and a standard output nobody reads drops lines instead of stopping the host (#1258).
builder.Services.MakeNonBlocking();

// Keep the normal local port while allowing isolated browser tests and packaged
// side-by-side diagnostics to select a different loopback endpoint.
var listenPort = builder.Configuration.GetValue<int?>("Server:Port") ?? 5099;

// Loopback only unless someone deliberately opts in. Deluno is a single-user
// local control plane, and ListenAnyIP put the whole API — including the
// unauthenticated setup endpoints on a fresh install — on every interface.
var allowLan = builder.Configuration.GetValue<bool>("Server:AllowLan");
builder.WebHost.ConfigureKestrel(options =>
{
    if (allowLan)
    {
        options.ListenAnyIP(listenPort);
    }
    else
    {
        options.ListenLocalhost(listenPort);
    }
});

// A Deluno open to the network on Windows looks after its firewall rule, and
// System says when other devices are turned away (#623).
if (allowLan && OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<ILanAccess>(new WindowsFirewallLanAccess(listenPort));
}

// Detecting an already-installed download client or Weir needs no LAN
// exposure -- it only reads the local registry and probes loopback (#640).
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IBundledComponentDetection, WindowsBundledComponentDetection>();
}

// Forwarded headers, response compression and the rate limiters are the same in
// every host (#1176): see DelunoRequestPipeline.
builder.Services.AddDelunoInfrastructure(builder.Configuration);
builder.Services.AddDelunoWebPipeline(builder.Configuration);
builder.Services.AddDelunoApplicationModules();

var configuredDataRoot = builder.Configuration["Storage:DataRoot"];
var dataRoot = Path.GetFullPath(
    string.IsNullOrWhiteSpace(configuredDataRoot) ? "data" : configuredDataRoot,
    builder.Environment.ContentRootPath);
// A data folder Deluno cannot write is told in one plain sentence, before any database opens (#1361).
if (DataFolderCheck.Problem(dataRoot) is { } dataFolderProblem)
{
    Console.Error.WriteLine(dataFolderProblem);
    return 1;
}

builder.Services
    .AddDataProtection()
    .SetApplicationName("Deluno")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "protection-keys")));

// ISecretProtector pipeline. Backend is selected at first resolution
// (Windows → DPAPI, Linux/macOS → AES-GCM with master key from
// DELUNO_MASTER_KEY env var or <dataRoot>/secrets/master.key file).
// Legacy DataProtection reads continue to work via the composite reader.
builder.Services.AddDelunoPlatformSecrets(
    Path.Combine(dataRoot, "secrets", "master.key"));

var app = builder.Build();

app.UseDelunoRequestPipeline();

try
{
    app.Run();
}
catch (NewerDatabaseException refusal)
{
    // A database a newer Deluno has updated: stop in plain words, nothing changed (#1361).
    Console.Error.WriteLine(refusal.Message);
    return 1;
}

return 0;
