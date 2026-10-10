namespace Deluno.Platform.Contracts;

/// <summary>
/// What a hand-off may say about a processor's stored health (#1213). Only a check or an answer can move a processor
/// from not answering back to answering; a hand-off that failed is not an answer and must never hide an outage.
/// </summary>
public static class ProcessorHealthRules
{
    /// <summary>
    /// The health to store after a hand-off was submitted, or null to leave the stored health alone.
    /// Accepted means the processor answered: healthy. No HTTP answer at all (it could not be reached) means
    /// unreachable. A refusal means the processor is there and answered, so it says nothing about its health:
    /// the refusal is on the hand-off and in Activity, and writing "degraded" would show a working processor as down
    /// on the Dashboard or hide an outage on the tray.
    /// </summary>
    public static string? HealthAfterSubmission(ProcessorSubmissionResult submission)
        => submission.IsAccepted ? "healthy"
            : submission.StatusCode is null ? "unreachable"
            : null;
}
