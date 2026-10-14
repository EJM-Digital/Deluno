using System.Text.Json;
using Deluno.Contracts;
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
public sealed record BundledComponentCredentials(string Username, string Password)
{
    /// <summary>A record prints its fields; this one must never print the password into a log.</summary>
    public override string ToString() => $"{nameof(BundledComponentCredentials)} {{ Username = {Username}, Password = (hidden) }}";
}

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

    /// <summary>Whether the saved sign-ins can be read right now (#1260). Busy or Unreadable means "unknown", never "none".</summary>
    StateFileKind Availability() => StateFileKind.Ok;

    /// <summary>Every kind that has a saved sign-in, so a leftover one can be swept.</summary>
    IReadOnlyList<string> Kinds() => [];
}

/// <summary>What a "connect this installed app" request did. Never carries any sign-in or password.</summary>
/// <param name="NeedsSignIn">The screen should open the sign-in form (host and port are given for a download client).</param>
/// <param name="NeedsAccount">Weir only: the person has to create their own Weir account to connect it; connect-all skips it and says so.</param>
public sealed record InstalledComponentConnectResult(
    string Kind,
    string Name,
    bool Healthy,
    string Message,
    bool NeedsSignIn = false,
    string? Protocol = null,
    string? Host = null,
    int? Port = null,
    bool NeedsAccount = false);

/// <param name="Installed">The app is on this computer.</param>
/// <param name="Connected">A Deluno connection to it on this computer and port exists.</param>
/// <param name="NeedsAccount">Weir only: Weir answers and has no account yet.</param>
/// <param name="AccountExists">Weir only: Weir answers and already has the person's account.</param>
public sealed record InstalledComponentState(bool Installed, bool Connected, bool NeedsAccount, bool AccountExists);

/// <summary>Connects the apps the installer added, on this computer, when the person asks (#1061, #968). Implemented by the worker.</summary>
public interface IInstalledComponentConnector
{
    /// <summary>
    /// Connects one app. <paramref name="username"/> and <paramref name="password"/> are the person's own Weir
    /// account (used in memory for this call only); the download clients ignore them.
    /// </summary>
    /// <returns>Null when <paramref name="kind"/> is not an app Deluno can connect this way.</returns>
    Task<InstalledComponentConnectResult?> ConnectAsync(string kind, string? username, string? password, string delunoBaseUrl, CancellationToken cancellationToken);

    /// <summary>Connects every installed, not yet connected download client; Weir is listed with needsAccount instead.</summary>
    Task<IReadOnlyList<InstalledComponentConnectResult>> ConnectAllAsync(CancellationToken cancellationToken);

    /// <summary>Per kind: installed, connected, and (Weir) whether it has an account yet.</summary>
    Task<IReadOnlyDictionary<string, InstalledComponentState>> StatesAsync(IReadOnlyList<BundledComponentStatus> detected, CancellationToken cancellationToken);
}

/// <summary>The optional body of a connect request: the person's own Weir account. Ignored for download clients.</summary>
public sealed record BundledComponentConnectRequest(string? Username, string? Password);

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
public sealed class FileBundledComponentCredentialStore(string path, ISecretProtector protector, IStateFileSystem? io = null) : IBundledComponentCredentialStore
{
    private const string PurposePrefix = "bundled-component:";
    private const string What = "Deluno's saved app sign-ins";

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "secrets", "component-credentials.json");

    public bool Has(string kind) => Read().ContainsKey(kind);

    public IReadOnlyList<string> Kinds() => Read().Keys.ToArray();

    /// <summary>Whether the file can be built on right now: a busy or damaged one cannot, and "no sign-ins" would be a guess.</summary>
    public StateFileKind Availability() => StateFile.ReadLocked<Dictionary<string, StoredEntry>>(path, null, io).Kind;

    public BundledComponentCredentials? TryGet(string kind)
    {
        if (!Read().TryGetValue(kind, out var entry) || string.IsNullOrEmpty(entry.Password))
        {
            return null;
        }

        var password = protector.TryUnprotect(PurposePrefix + kind, entry.Password).Value;
        return string.IsNullOrEmpty(password) ? null : new BundledComponentCredentials(entry.Username, password);
    }

    /// <summary>Saves the sign-in. Throws <see cref="StateFileException"/> (in everyday words) when the file is busy or damaged: nothing is written then.</summary>
    public void Save(string kind, BundledComponentCredentials credentials)
        => StateFile.UpdateOrThrow<Dictionary<string, StoredEntry>>(
            path,
            current =>
            {
                var entries = Copy(current);
                entries[kind] = new StoredEntry(credentials.Username, protector.Protect(PurposePrefix + kind, credentials.Password));
                return entries;
            },
            What,
            io: io);

    public void Remove(string kind)
        => StateFile.UpdateOrThrow<Dictionary<string, StoredEntry>>(
            path,
            current =>
            {
                var entries = Copy(current);
                // Nothing to forget: leave the file exactly as it is.
                return entries.Remove(kind) || current is not null ? entries : null;
            },
            What,
            io: io);

    private static Dictionary<string, StoredEntry> Copy(Dictionary<string, StoredEntry>? current)
        => current is null
            ? new Dictionary<string, StoredEntry>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, StoredEntry>(current, StringComparer.OrdinalIgnoreCase);

    /// <summary>The entries, or none when the file is missing, busy or damaged. Only reads use this: a write never starts from it.</summary>
    private Dictionary<string, StoredEntry> Read()
        => Copy(StateFile.ReadLocked<Dictionary<string, StoredEntry>>(path, null, io).Value);

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
    /// "Connect the apps you installed": the person asks, one app or all of them, and Deluno
    /// connects them server-side and answers only whether it worked. There is no route that
    /// returns an app's sign-in or password to anyone (#1061).
    /// </summary>
    public static IEndpointRouteBuilder MapDelunoBundledComponentCredentialsEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/system/bundled-components/{kind}/connect", async (string kind, HttpContext httpContext, [Microsoft.AspNetCore.Mvc.FromServices] IInstalledComponentConnector connector, CancellationToken cancellationToken) =>
            {
                BundledComponentConnectRequest? body = null;
                if (httpContext.Request.HasJsonContentType())
                {
                    try
                    {
                        body = await httpContext.Request.ReadFromJsonAsync<BundledComponentConnectRequest>(cancellationToken);
                    }
                    catch (JsonException exception)
                    {
                        var (message, member) = DelunoBadRequestAnswers.Describe(exception);
                        return Results.BadRequest(new { message, member });
                    }
                }

                var baseUrl = $"{httpContext.Request.Scheme}://127.0.0.1:{httpContext.Connection.LocalPort}{httpContext.Request.PathBase}";
                var result = await connector.ConnectAsync(kind, body?.Username, body?.Password, baseUrl, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(ConnectResponse.From(result));
            })
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        api.MapPost("/system/bundled-components/connect-all", async ([Microsoft.AspNetCore.Mvc.FromServices] IInstalledComponentConnector connector, CancellationToken cancellationToken)
                => Results.Ok(new { results = (await connector.ConnectAllAsync(cancellationToken)).Select(ConnectResponse.From).ToArray() }))
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        return api;
    }

    private sealed record ConnectResponse(string Kind, string Name, bool Healthy, string Message, bool NeedsSignIn, string? Protocol, string? Host, int? Port, bool NeedsAccount)
    {
        public static ConnectResponse From(InstalledComponentConnectResult result)
            => new(result.Kind, result.Name, result.Healthy, result.Message, result.NeedsSignIn, result.Protocol, result.Host, result.Port, result.NeedsAccount);
    }
}
