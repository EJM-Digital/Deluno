namespace Deluno.Jobs.Contracts;

public interface IDispatchAlertRepository
{
    Task<DispatchAlert> CreateAlertAsync(
        string dispatchId,
        string title,
        string summary,
        string alertKind,
        string severity,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DispatchAlert>> GetOpenAlertsAsync(
        string? severityFilter = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<bool> AcknowledgeAlertAsync(string alertId, CancellationToken cancellationToken);

    Task<int> GetOpenAlertCountBySeverityAsync(CancellationToken cancellationToken);
}
