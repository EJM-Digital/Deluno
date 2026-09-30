using System.Text.Json;
using Deluno.Security;
using Deluno.Security.Hardening.Backends;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.Network;

/// <summary>
/// The sign-in an installed download client was given (#1050). qBittorrent
/// 5.2.1 refuses to start its Web UI with no username and password, so the
/// installer seeds a generated pair. The installer (the tray) writes the plain
/// password here, protected; the running Deluno reads it back when the person
/// clicks "Use qBittorrent" and connects it there and then, server-side, with no
/// typing. Once the connection exists, Deluno's own secret store holds the
/// password like any other client's.
///
/// <para>Lifetime (#1061): the plain value is kept for as long as the app is
/// installed, even after its connection is deleted, because "Use" connects with
/// it. It is never returned by Deluno's API, to anyone: the connection is made
/// server-side. It is removed only when the app is uninstalled, or for a kind
/// Deluno does not connect (see
/// <c>InstalledComponentWirer.SweepStoredSignInsAsync</c>).</para>
/// </summary>
public sealed record BundledComponentCredentials(string Username, string Password);

public interface IBundledComponentCredentialStore
{
    /// <summary>Whether a saved sign-in exists for <paramref name="kind"/>.</summary>
    bool Has(string kind);

    /// <summary>The saved sign-in, or null when there is none (or it cannot be read).</summary>
    BundledComponentCredentials? TryGet(string kind);

    /// <summary>Saves the sign-in, replacing an earlier one for the same kind.</summary>
    void Save(string kind, BundledComponentCredentials credentials);

    /// <summary>Forgets the saved sign-in for <paramref name="kind"/> (no error when there is none).</summary>
    void Remove(string kind);

    /// <summary>Every kind that has a saved sign-in, so a leftover one can be swept.</summary>
    IReadOnlyList<string> Kinds() => [];
}

/// <summary>What a "connect this installed app now" request did. Never carries the sign-in.</summary>
public sealed record InstalledComponentConnectResult(string Name, bool Healthy, string Message, bool NeedsSignIn = false, string? Protocol = null, string? Host = null, int? Port = null);

/// <summary>Connects an app the installer added, on this computer (#1061). Implemented by the worker.</summary>
public interface IInstalledComponentConnector
{
    /// <returns>Null when <paramref name="kind"/> is not an app Deluno can connect this way.</returns>
    Task<InstalledComponentConnectResult?> ConnectNowAsync(string kind, CancellationToken cancellationToken);
}

/// <summary>A host with no installer sign-ins: not Windows.</summary>
public sealed class NoBundledComponentCredentialStore : IBundledComponentCredentialStore
{
    public bool Has(string kind) => false;
    public BundledComponentCredentials? TryGet(string kind) => null;
    public void Save(string kind, BundledComponentCredentials credentials) { }
    public void Remove(string kind) { }
}

/// <summary>
/// Keeps each kind's password, protected, in one JSON file under the data
/// root's <c>secrets</c> folder. The tray (which installs) and the API (which
/// reads) may run as different Windows accounts (service mode), so on Windows
/// the protection is DPAPI at machine scope rather than per-user: it can be
/// read by another local account, which is acceptable for a password to a
/// client whose Web UI is bound to this computer only (#1053).
/// </summary>
public sealed class FileBundledComponentCredentialStore(string path, ISecretProtector protector) : IBundledComponentCredentialStore
{
    private const string PurposePrefix = "bundled-component:";
    private static readonly object Gate = new();

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "secrets", "component-credentials.json");

    public bool Has(string kind) => Read().ContainsKey(kind);

    public IReadOnlyList<string> Kinds() => Read().Keys.ToArray();

    public BundledComponentCredentials? TryGet(string kind)
    {
        if (!Read().TryGetValue(kind, out var entry) || string.IsNullOrEmpty(entry.Password))
        {
            return null;
        }

        try
        {
            var password = protector.Unprotect(PurposePrefix + kind, entry.Password);
            return string.IsNullOrEmpty(password) ? null : new BundledComponentCredentials(entry.Username, password);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string kind, BundledComponentCredentials credentials)
    {
        lock (Gate)
        {
            var entries = Read();
            entries[kind] = new StoredEntry(credentials.Username, protector.Protect(PurposePrefix + kind, credentials.Password));
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
    }

    public void Remove(string kind)
    {
        lock (Gate)
        {
            var entries = Read();
            if (!entries.Remove(kind))
            {
                return;
            }

            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
    }

    private Dictionary<string, StoredEntry> Read()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, StoredEntry>(StringComparer.OrdinalIgnoreCase);
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, StoredEntry>>(File.ReadAllText(path));
            return parsed is null
                ? new Dictionary<string, StoredEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, StoredEntry>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, StoredEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed record StoredEntry(string Username, string Password);
}

public static class BundledComponentCredentialStores
{
    /// <summary>The machine-scope DPAPI-backed store the tray writes and the API reads; null off Windows.</summary>
    public static IBundledComponentCredentialStore CreateForDataRoot(string dataRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NoBundledComponentCredentialStore();
        }

        return CreateWindows(dataRoot);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IBundledComponentCredentialStore CreateWindows(string dataRoot)
        => new FileBundledComponentCredentialStore(
            FileBundledComponentCredentialStore.PathFor(dataRoot),
            new WindowsDpapiSecretProtector(System.Security.Cryptography.DataProtectionScope.LocalMachine));
}

public static class BundledComponentCredentialsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// "Use X" on an app the installer added: Deluno connects it itself, with the sign-in it
    /// holds, and answers with whether it worked. There is no route that returns an app's
    /// sign-in to anyone (#1061).
    /// </summary>
    public static IEndpointRouteBuilder MapDelunoBundledComponentCredentialsEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/system/bundled-components/{kind}/connect", async (string kind, IInstalledComponentConnector connector, CancellationToken cancellationToken) =>
            {
                var result = await connector.ConnectNowAsync(kind, cancellationToken);
                return result is null
                    ? Results.NotFound()
                    : Results.Ok(new { name = result.Name, healthy = result.Healthy, message = result.Message, needsSignIn = result.NeedsSignIn, protocol = result.Protocol, host = result.Host, port = result.Port });
            })
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        return api;
    }
}
