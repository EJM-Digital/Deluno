using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.Network;

/// <summary>
/// Whether a download client or Weir is already on this machine, so the
/// installer's component picker can skip it and the setup wizard can offer a
/// pre-filled connection instead of asking for a host and port (#640).
///
/// <para>Detection is local and read-only: a registry scan for the program's
/// own uninstall entry, plus a loopback probe of its well-known default port.
/// Neither needs administrator rights, and neither writes anything. Actually
/// installing a ticked component is the tray's job — see
/// <c>apps/windows-tray/BundledComponents.cs</c> — because it means running a
/// vendor's own installer as a separate process, which only makes sense from
/// the interactive, per-user install session, not from this always-on API.</para>
/// </summary>
public sealed record BundledComponentStatus(
    string Kind,
    string DisplayName,
    string Category,
    bool Installed,
    bool Reachable,
    string DefaultHost,
    int DefaultPort);

public interface IBundledComponentDetection
{
    Task<IReadOnlyList<BundledComponentStatus>> DetectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The six components #640 lists: the five download clients Deluno can
/// dispatch to (<see cref="Deluno.Connections.DownloadClientProtocolCatalog"/>
/// carries the same five protocol keys) and Weir. Ports match the defaults
/// already shown in the connections screen presets
/// (apps/web/src/routes/connections/presets.ts CLIENT_PRESETS) and Weir's own
/// documented default (README "Weir asks which port to use... Keep 9347"),
/// except qBittorrent: a Deluno-installed qBittorrent is started on 8081
/// (installer/bundled-components.json's own configFileContent seeds this),
/// since SABnzbd's own default is also 8080 and only one of the two can bind
/// it (#968). The connections screen preset for a manually-added qBittorrent
/// stays 8080 -- that is still the vendor's own default for one the person
/// installed themselves.
/// </summary>
public static class BundledComponentCatalog
{
    public const string DownloadClientCategory = "download-client";
    public const string ProcessorCategory = "processor";

    public sealed record Spec(
        string Kind,
        string DisplayName,
        string Category,
        int DefaultPort,
        IReadOnlyList<string> UninstallDisplayNameHints);

    public static readonly IReadOnlyList<Spec> All = new[]
    {
        new Spec("qbittorrent", "qBittorrent", DownloadClientCategory, 8081, new[] { "qBittorrent" }),
        new Spec("transmission", "Transmission", DownloadClientCategory, 9091, new[] { "Transmission" }),
        new Spec("deluge", "Deluge", DownloadClientCategory, 8112, new[] { "Deluge" }),
        new Spec("sabnzbd", "SABnzbd", DownloadClientCategory, 8080, new[] { "SABnzbd" }),
        new Spec("nzbget", "NZBGet", DownloadClientCategory, 6789, new[] { "NZBGet" }),
        new Spec("weir", "Weir", ProcessorCategory, 9347, new[] { "Weir" })
    };
}

/// <summary>A host with nothing for Deluno to detect against: not Windows.</summary>
public sealed class UncheckedBundledComponentDetection : IBundledComponentDetection
{
    public Task<IReadOnlyList<BundledComponentStatus>> DetectAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<BundledComponentStatus>>(BundledComponentCatalog.All
            .Select(spec => new BundledComponentStatus(spec.Kind, spec.DisplayName, spec.Category, false, false, "localhost", spec.DefaultPort))
            .ToArray());
}

public interface IInstalledProgramsReader
{
    IReadOnlyList<string> ReadDisplayNames();
}

public interface IPortProbe
{
    Task<bool> IsOpenAsync(string host, int port, CancellationToken cancellationToken);
}

public sealed class WindowsBundledComponentDetection(
    IInstalledProgramsReader? programs = null,
    IPortProbe? portProbe = null) : IBundledComponentDetection
{
    private readonly IInstalledProgramsReader _programs = programs ?? new RegistryInstalledProgramsReader();
    private readonly IPortProbe _portProbe = portProbe ?? new LoopbackPortProbe();

    public async Task<IReadOnlyList<BundledComponentStatus>> DetectAsync(CancellationToken cancellationToken)
    {
        var installedNames = _programs.ReadDisplayNames();
        var results = new List<BundledComponentStatus>(BundledComponentCatalog.All.Count);

        foreach (var spec in BundledComponentCatalog.All)
        {
            var installed = installedNames.Any(name =>
                spec.UninstallDisplayNameHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase)));
            var reachable = await _portProbe.IsOpenAsync("127.0.0.1", spec.DefaultPort, cancellationToken);
            results.Add(new BundledComponentStatus(spec.Kind, spec.DisplayName, spec.Category, installed, reachable, "localhost", spec.DefaultPort));
        }

        return results;
    }
}

/// <summary>
/// Reads every program's own uninstall entry from the registry. This is what
/// "Programs and Features" reads, so it recognises an install this session
/// never ran, from before Deluno existed or from the vendor's own installer
/// the picker just launched. No administrator rights are needed to read it.
/// </summary>
public sealed class RegistryInstalledProgramsReader : IInstalledProgramsReader
{
    private static readonly (string Hive, string Path)[] Roots =
    {
        ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        ("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        ("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
    };

    public IReadOnlyList<string> ReadDisplayNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        return ReadOnWindows();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> ReadOnWindows()
    {
        var names = new List<string>();
        foreach (var (hiveName, path) in Roots)
        {
            var hive = hiveName == "HKLM" ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey?.GetValue("DisplayName") is string displayName && !string.IsNullOrWhiteSpace(displayName))
                        {
                            names.Add(displayName);
                        }
                    }
                    catch
                    {
                        // One malformed subkey should not stop reading the rest.
                    }
                }
            }
            catch
            {
                // A locked-down registry hive degrades to "not found", never a thrown 500.
            }
        }

        return names;
    }
}

/// <summary>Whether something answers a TCP connect on the given loopback port, within a short budget.</summary>
public sealed class LoopbackPortProbe : IPortProbe
{
    public async Task<bool> IsOpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(300));
            await client.ConnectAsync(host, port, timeout.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

public static class BundledComponentsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoBundledComponentsEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/system/bundled-components", async (IBundledComponentDetection detection, [Microsoft.AspNetCore.Mvc.FromServices] IInstalledComponentConnector connector, CancellationToken cancellationToken) =>
            {
                var detected = await detection.DetectAsync(cancellationToken);
                var states = await connector.StatesAsync(detected, cancellationToken);
                return Results.Ok(detected.Select(status =>
                {
                    states.TryGetValue(status.Kind, out var state);
                    return new BundledComponentView(
                        status.Kind, status.DisplayName, status.Category,
                        state?.Installed ?? status.Installed, status.Reachable,
                        status.DefaultHost, status.DefaultPort,
                        state?.Connected ?? false, state?.NeedsAccount ?? false, state?.AccountExists ?? false);
                }).ToArray());
            })
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        return api;
    }
}

/// <summary>One installed-app row of GET /api/system/bundled-components. Carries no password and no sign-in of any kind.</summary>
public sealed record BundledComponentView(
    string Kind,
    string DisplayName,
    string Category,
    bool Installed,
    bool Reachable,
    string DefaultHost,
    int DefaultPort,
    bool Connected,
    bool NeedsAccount,
    bool AccountExists);
