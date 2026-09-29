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
    public bool SecretConfigured => !string.IsNullOrWhiteSpace(Secret);
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
}

public sealed record ProcessorConnectionTestResult(
    string ConnectionId,
    bool IsReachable,
    string Status,
    string Message,
    int? StatusCode,
    int? LatencyMs);

public sealed record ProcessorSubmissionResult(
    bool IsAccepted,
    string Status,
    string Message,
    int? StatusCode);

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
