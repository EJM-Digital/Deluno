using Microsoft.Extensions.Logging;

namespace Deluno.Jobs.Data;

public sealed class CircuitBreakerDownloadDispatchPollingService(
    IDownloadDispatchPollingService innerService,
    ILogger<CircuitBreakerDownloadDispatchPollingService> logger)
    : IDownloadDispatchPollingService
{
    private static readonly int FailureThreshold = 3;
    private static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromMinutes(15);

    private int _consecutiveFailures;
    private DateTimeOffset? _circuitOpenedUtc;

    public async Task<DownloadDispatchPollingReport> PollAsync(CancellationToken cancellationToken)
    {
        if (_circuitOpenedUtc is not null && DateTimeOffset.UtcNow - _circuitOpenedUtc < CircuitOpenDuration)
        {
            logger.LogWarning(
                "Circuit breaker is open. Polling disabled until {ResetTime}.",
                _circuitOpenedUtc.Value.Add(CircuitOpenDuration));
            return new DownloadDispatchPollingReport(
                UnresolvedDispatchesChecked: 0,
                GrabTimeoutsDetected: 0,
                DetectionTimeoutsDetected: 0,
                ImportTimeoutsDetected: 0,
                ImportFailuresDetected: 0,
                RecoveryCasesRecorded: 0);
        }

        if (_circuitOpenedUtc is not null)
        {
            _circuitOpenedUtc = null;
            _consecutiveFailures = 0;
            logger.LogInformation("Circuit breaker reset after cooldown period.");
        }

        try
        {
            var report = await innerService.PollAsync(cancellationToken);
            _consecutiveFailures = 0;
            return report;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            logger.LogError(
                ex,
                "Polling failed ({FailureCount}/{Threshold}). Next failure will open circuit.",
                _consecutiveFailures,
                FailureThreshold);

            if (_consecutiveFailures >= FailureThreshold)
            {
                _circuitOpenedUtc = DateTimeOffset.UtcNow;
                logger.LogError(
                    "Circuit breaker opened after {FailureCount} consecutive failures. Polling will resume at {ResumeTime}.",
                    _consecutiveFailures,
                    _circuitOpenedUtc.Value.Add(CircuitOpenDuration));
            }

            throw;
        }
    }
}
