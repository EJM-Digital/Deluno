using System.Text.Json.Serialization;

namespace Deluno.Platform.Contracts;

/// <summary>
/// A reusable outbound processor connection. Deluno only submits a correlated
/// hand-off; the processor must report completion through Deluno's existing
/// guarded callback before any import is queued.
/// </summary>
public sealed record ProcessorConnectionItem(
    string Id,
    string Name,
    string Provider,
    string SubmissionUrl,
    string AuthHeaderName,
    [property: JsonIgnore] string? Secret,
    bool IsEnabled,
    string HealthStatus,
    string? LastHealthMessage,
    DateTimeOffset? LastHealthTestUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    /// <summary>
    /// Optional address Deluno asks (GET) to prove the processor is up: a 2xx answer is healthy,
    /// 401/403 a rejected credential, anything else a failure. Empty means the test can only
    /// confirm that something answers at <see cref="SubmissionUrl"/> (#1055).
    /// </summary>
    string? HealthCheckUrl = null)
{
    public bool SecretConfigured => !string.IsNullOrWhiteSpace(Secret) || SecretUnreadable;

    /// <summary>The saved secret was stored on another computer, account or key and cannot be read here (#1094). It is listed as missing; entering it again clears this.</summary>
    public bool SecretUnreadable { get; init; }

    /// <summary>Plain-words reason when <see cref="SecretUnreadable"/> is set, for the screen and Health.</summary>
    public string? SecretProblem { get; init; }

}

public sealed record CreateProcessorConnectionRequest(
    string? Name,
    string? Provider,
    string? SubmissionUrl,
    string? AuthHeaderName,
    string? Secret,
    bool IsEnabled,
    string? HealthCheckUrl = null)
{
    /// <summary>#1076: the machine name the processor published (never sent by a client), used instead of the address host in the connection's name.</summary>
    [JsonIgnore]
    public string? MachineName { get; init; }

    /// <summary>#1144: the processor's own name, as it reported it or as the setup that added it knows it (never sent by a client); the connection is named "&lt;this&gt; on &lt;machine&gt;".</summary>
    [JsonIgnore]
    public string? ProductName { get; init; }
}

public sealed record UpdateProcessorConnectionRequest(
    string? Name,
    string? Provider,
    string? SubmissionUrl,
    string? AuthHeaderName,
    string? Secret,
    bool IsEnabled,
    string? HealthCheckUrl = null)
{
    /// <summary>#1076: the machine name the processor published (never sent by a client), used instead of the address host in the connection's name.</summary>
    [JsonIgnore]
    public string? MachineName { get; init; }

    /// <summary>#1144: the processor's own name, as it reported it or as the setup that added it knows it (never sent by a client); the connection is named "&lt;this&gt; on &lt;machine&gt;".</summary>
    [JsonIgnore]
    public string? ProductName { get; init; }
}

public sealed record ProcessorConnectionTestResult(
    string ConnectionId,
    bool IsReachable,
    string Status,
    string Message,
    int? StatusCode,
    int? LatencyMs);

/// <summary>
/// #1117: whose releases a processor's version is compared with. A processor says its version in its capabilities
/// answer, but it does not say which project it is, so only the one Deluno's own installer sets up (on the intake
/// route, with its <c>/ready</c> health address) is matched to a release feed; any other shows its version alone.
/// </summary>
public static class ProcessorProjects
{
    private const string IntakePrefix = "/api/v1/intake/webhook/";

    public static string? ProjectFor(ProcessorConnectionItem connection)
        => Uri.TryCreate(connection.SubmissionUrl, UriKind.Absolute, out var submission)
            && submission.AbsolutePath.StartsWith(IntakePrefix, StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(connection.HealthCheckUrl, UriKind.Absolute, out var health)
            && health.AbsolutePath.TrimEnd('/').Equals("/ready", StringComparison.OrdinalIgnoreCase)
                ? Deluno.Connections.Versions.ConnectionProjects.Weir
                : null;
}

public sealed record ProcessorSubmissionResult(
    bool IsAccepted,
    string Status,
    string Message,
    int? StatusCode,
    TimeSpan? RetryAfter = null);

public interface IProcessorConnectionService
{
    /// <summary>
    /// #1076: the machine name a processor publishes in its capabilities
    /// (GET /api/v1/intake/capabilities, answered to the webhook secret as
    /// "machine_name"), or null when it publishes none, cannot be reached in a
    /// few seconds, or is not that kind of processor. Any processor may do this;
    /// nothing here knows which product it is.
    /// </summary>
    Task<string?> GetMachineNameAsync(
        string submissionUrl,
        string? authHeaderName,
        string? secret,
        CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// #1117: the version a processor publishes in the same capabilities answer ("4.0.0", or "0.0.1-dev" for a
    /// development build), or null when it publishes none or cannot be reached in a few seconds. Asked after a
    /// test that reached the processor; never part of the test itself.
    /// </summary>
    Task<string?> ReadVersionAsync(ProcessorConnectionItem connection, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// #1144: the processor's own name, from a top-level <c>name</c> or <c>product</c> in the same capabilities answer,
    /// or null when it publishes none. Generic: nothing here knows which product it is.
    /// </summary>
    Task<string?> GetProductNameAsync(
        string submissionUrl,
        string? authHeaderName,
        string? secret,
        CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// #1144: the processor's <c>library-folders</c> list (GET /api/v1/intake/library-folders), empty when it does not
    /// advertise the capability or cannot be asked.
    /// </summary>
    Task<IReadOnlyList<ProcessorLibraryFolder>> GetLibraryFoldersAsync(
        ProcessorConnectionItem connection,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ProcessorLibraryFolder>>([]);

    Task<ProcessorConnectionTestResult> TestAsync(
        ProcessorConnectionItem connection,
        CancellationToken cancellationToken);

    Task<ProcessorSubmissionResult> SubmitAsync(
        ProcessorConnectionItem connection,
        ProcessorHandoffItem handoff,
        CancellationToken cancellationToken);

    /// <summary>Whether this processor can be asked about a hand-off, and cancel one (#511).</summary>
    Task<ProcessorStatusSupport> GetStatusSupportAsync(
        ProcessorConnectionItem connection,
        CancellationToken cancellationToken);

    Task<ProcessorHandoffStatusResult> GetHandoffStatusAsync(
        ProcessorConnectionItem connection,
        string handoffId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Asks the processor to drop work it has not started. A processor already
    /// working on the file answers <see cref="ProcessorAnswer.Busy"/>.
    /// </summary>
    Task<ProcessorHandoffCancelResult> CancelHandoffAsync(
        ProcessorConnectionItem connection,
        string handoffId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Tells the processor what became of the file it handed back, so it can
    /// let go of its own copy (#578).
    ///
    /// <para>A processor cannot know: Deluno's answer to a completed report
    /// only says an import was queued, and a queued import can still fail. So
    /// its copy is kept until this says otherwise, or until its own retention
    /// reclaims it — which is what has to work when the media manager on the
    /// other end says nothing at all.</para>
    /// </summary>
    /// <param name="occurredUtc">
    /// When it happened, not when it is being said: a report sent again must be
    /// the same report (#580).
    /// </param>
    Task<ProcessorOutcomeResult> ReportOutcomeAsync(
        ProcessorConnectionItem connection,
        string handoffId,
        ProcessorHandoffOutcome outcome,
        string? importedPath,
        string? reason,
        DateTimeOffset occurredUtc,
        CancellationToken cancellationToken);
}
