using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

public sealed class InMemoryDispatchAlertRepository : IDispatchAlertRepository
{
    private readonly List<DispatchAlert> _alerts = new();
    private readonly TimeProvider _timeProvider;

    public InMemoryDispatchAlertRepository(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public async Task<DispatchAlert> CreateAlertAsync(
        string dispatchId,
        string title,
        string summary,
        string alertKind,
        string severity,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        var alert = new DispatchAlert(
            Id: Guid.CreateVersion7().ToString("N"),
            DispatchId: dispatchId,
            Title: title,
            Summary: summary,
            AlertKind: alertKind,
            Severity: severity,
            Metadata: metadata,
            DetectedUtc: _timeProvider.GetUtcNow(),
            Acknowledged: false,
            AcknowledgedUtc: null);

        _alerts.Add(alert);
        await Task.CompletedTask;
        return alert;
    }

    public async Task<IReadOnlyList<DispatchAlert>> GetOpenAlertsAsync(
        string? severityFilter = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var query = _alerts.Where(a => !a.Acknowledged);
        
        if (!string.IsNullOrEmpty(severityFilter))
            query = query.Where(a => a.Severity == severityFilter);

        await Task.CompletedTask;
        return query.OrderByDescending(a => a.DetectedUtc).Take(limit).ToList();
    }

    public async Task<bool> AcknowledgeAlertAsync(string alertId, CancellationToken cancellationToken)
    {
        var alert = _alerts.FirstOrDefault(a => a.Id == alertId);
        if (alert == null)
            return await Task.FromResult(false);

        var index = _alerts.IndexOf(alert);
        _alerts[index] = alert with { Acknowledged = true, AcknowledgedUtc = _timeProvider.GetUtcNow() };
        return await Task.FromResult(true);
    }

    public Task<int> AcknowledgeAlertsForDispatchAsync(string dispatchId, CancellationToken cancellationToken)
    {
        var open = _alerts.Where(a => !a.Acknowledged && a.DispatchId == dispatchId).ToList();
        foreach (var alert in open)
        {
            _alerts[_alerts.IndexOf(alert)] = alert with { Acknowledged = true, AcknowledgedUtc = _timeProvider.GetUtcNow() };
        }

        return Task.FromResult(open.Count);
    }

    /// <summary>This store knows no downloads, so it matches the release by the alert's title, which is the release name.</summary>
    public Task<int> AcknowledgeAlertsForReleaseAsync(string downloadClientId, string releaseName, CancellationToken cancellationToken)
    {
        var open = _alerts.Where(a => !a.Acknowledged && a.Title == releaseName).ToList();
        foreach (var alert in open)
        {
            _alerts[_alerts.IndexOf(alert)] = alert with { Acknowledged = true, AcknowledgedUtc = _timeProvider.GetUtcNow() };
        }

        return Task.FromResult(open.Count);
    }

    public async Task<int> GetOpenAlertCountBySeverityAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return _alerts.Count(a => !a.Acknowledged);
    }

    public Task<bool> HasUnresolvedAlertAsync(string dispatchId, string alertKind, CancellationToken cancellationToken)
        => Task.FromResult(_alerts.Any(a => a.DispatchId == dispatchId && a.AlertKind == alertKind));

    /// <summary>
    /// This store holds alerts and nothing about the downloads they describe,
    /// so it cannot tell which have settled. Nothing is registered against it;
    /// the SQLite repository is the one that resolves.
    /// </summary>
    public Task<int> ResolveSettledAlertsAsync(CancellationToken cancellationToken) => Task.FromResult(0);
}
