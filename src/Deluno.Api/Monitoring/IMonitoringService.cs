namespace Deluno.Api.Monitoring;

public interface IMonitoringService
{
    Task<MonitoringDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MonitoringAlertItem>> GetAlertsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MonitoringDiagnosticItem>> SearchDiagnosticsAsync(MonitoringDiagnosticsQuery query, CancellationToken cancellationToken);
    Task<MonitoringExportSnapshot> BuildExportSnapshotAsync(CancellationToken cancellationToken);
}
