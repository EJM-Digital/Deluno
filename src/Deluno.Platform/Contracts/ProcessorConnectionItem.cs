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
    DateTimeOffset UpdatedUtc)
{
    public bool SecretConfigured => !string.IsNullOrWhiteSpace(Secret);
}

public sealed record CreateProcessorConnectionRequest(
    string? Name,
    string? Provider,
    string? SubmissionUrl,
    string? AuthHeaderName,
    string? Secret,
    bool IsEnabled);

public sealed record UpdateProcessorConnectionRequest(
    string? Name,
    string? Provider,
    string? SubmissionUrl,
    string? AuthHeaderName,
    string? Secret,
    bool IsEnabled);

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
}
