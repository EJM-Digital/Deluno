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
public sealed record CompanionToolStatus(
    string Id,
    string Name,
    string Description,
    string State,
    string? Version,
    string? ConnectionName,
    bool HasLogin,
    CompanionJobState? Job);

/// <param name="Status"><c>queued</c>, <c>running</c>, <c>done</c>, <c>skipped</c> or <c>failed</c>.</param>
public sealed record CompanionJobState(string Status, string Step, string Message, DateTimeOffset UpdatedUtc);

public sealed record CompanionToolsResponse(CompanionAvailability Availability, IReadOnlyList<CompanionToolStatus> Tools, string UninstallNote);

public sealed record CompanionInstallRequest(IReadOnlyList<string>? ToolIds);

/// <summary>A login Deluno generated for a tool, shown when the owner asks.</summary>
public sealed record CompanionLogin(string ToolId, string Url, string? Username, string Secret, string SecretLabel);
