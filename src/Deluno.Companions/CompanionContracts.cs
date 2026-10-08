namespace Deluno.Companions;

/// <summary>The tools Deluno can fetch, install and connect for its owner (#506).</summary>
public static class CompanionToolIds
{
    public const string Sabnzbd = "sabnzbd";
    public const string Qbittorrent = "qbittorrent";
    public const string Weir = "weir";

    public static readonly IReadOnlyList<string> All = [Sabnzbd, Qbittorrent, Weir];
}

/// <summary>Whether this Deluno can install anything at all, and if not, why.</summary>
public sealed record CompanionAvailability(bool Available, string? Reason);

/// <param name="State"><c>not-installed</c>, <c>installed</c> (on this computer, not connected) or <c>connected</c>.</param>
/// <param name="Job">What the last install run did for this tool, step by step.</param>
/// <param name="Url">Where the tool answers on this computer, for an "Open" link; null when it is not installed or not known.</param>
public sealed record CompanionToolStatus(
    string Id,
    string Name,
    string Description,
    string State,
    string? Version,
    string? ConnectionName,
    CompanionJobState? Job,
    string? Url = null);

/// <param name="Status"><c>queued</c>, <c>running</c>, <c>done</c>, <c>skipped</c> or <c>failed</c>.</param>
public sealed record CompanionJobState(string Status, string Step, string Message, DateTimeOffset UpdatedUtc);

public sealed record CompanionToolsResponse(CompanionAvailability Availability, IReadOnlyList<CompanionToolStatus> Tools, string UninstallNote);

public sealed record CompanionInstallRequest(IReadOnlyList<string>? ToolIds);

/// <summary>Where Weir stands, for the "connect the apps you installed" step. Never carries anything secret.</summary>
/// <param name="Installed">Weir is on this computer.</param>
/// <param name="NeedsAccount">Weir answered and has no account yet: the person creates theirs when connecting.</param>
/// <param name="AccountExists">Weir answered and already has the person's account.</param>
public sealed record WeirAccountState(bool Installed, bool NeedsAccount, bool AccountExists);

/// <param name="NeedsSignIn">The person still has to type their Weir username and password.</param>
/// <param name="ProcessorConnectionId">The processor connection made for Weir, so libraries that refine before import can be linked to it. Null when nothing was made.</param>
/// <param name="ProcessorConnectionName">That connection's name.</param>
public sealed record WeirConnectOutcome(bool Healthy, string Message, bool NeedsSignIn, string? ProcessorConnectionId = null, string? ProcessorConnectionName = null);

/// <summary>Links Weir and Deluno with the account the person gives, held in memory for that call only.</summary>
public interface IWeirConnector
{
    Task<WeirAccountState> AccountStateAsync(CancellationToken cancellationToken);

    Task<WeirConnectOutcome> ConnectAsync(string? username, string? password, string delunoBaseUrl, CancellationToken cancellationToken);

    Task<string?> ConnectionNameAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Where an installer-generated login (qBittorrent's required Web UI login, SABnzbd's key) is
/// kept for this computer's own connect step. Nothing serves it to a caller.
/// </summary>
public interface IInstalledClientLogins
{
    void Keep(string kind, string? username, string secret);
}

public sealed class NoInstalledClientLogins : IInstalledClientLogins
{
    public void Keep(string kind, string? username, string secret) { }
}
