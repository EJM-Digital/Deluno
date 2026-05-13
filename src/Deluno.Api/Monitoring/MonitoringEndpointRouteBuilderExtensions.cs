using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.Monitoring;

public static class MonitoringEndpointRouteBuilderExtensions
{
    public static RouteGroupBuilder MapDelunoMonitoringEndpoints(this RouteGroupBuilder api)
    {
        var monitoring = api.MapGroup("/monitoring");

        monitoring.MapGet("/dashboard", async (
            IMonitoringService service,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await service.GetDashboardAsync(cancellationToken);
            return Results.Ok(snapshot);
        });

        monitoring.MapGet("/alerts", async (
            IMonitoringService service,
            CancellationToken cancellationToken) =>
        {
            var alerts = await service.GetAlertsAsync(cancellationToken);
            return Results.Ok(new
            {
                openCount = alerts.Count,
                alerts
            });
        });

        monitoring.MapGet("/diagnostics", async (
            string? query,
            string? category,
            string? severity,
            string? sinceUtc,
            int? take,
            IMonitoringService service,
            CancellationToken cancellationToken) =>
        {
            DateTimeOffset? parsedSince = null;
            if (!string.IsNullOrWhiteSpace(sinceUtc) &&
                DateTimeOffset.TryParse(sinceUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                parsedSince = parsed;
            }

            var items = await service.SearchDiagnosticsAsync(
                new MonitoringDiagnosticsQuery(
                    Query: query,
                    Category: category,
                    Severity: severity,
                    SinceUtc: parsedSince,
                    Take: Math.Clamp(take ?? 100, 1, 500)),
                cancellationToken);

            return Results.Ok(new
            {
                count = items.Count,
                diagnostics = items
            });
        });

        monitoring.MapGet("/export/prometheus", async (
            IMonitoringService service,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await service.BuildExportSnapshotAsync(cancellationToken);
            var lines = new List<string>
            {
                "# HELP deluno_monitoring_readiness_ready 1 when readiness checks pass.",
                "# TYPE deluno_monitoring_readiness_ready gauge"
            };
            lines.AddRange(snapshot.NumericMetrics
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key} {pair.Value.ToString(CultureInfo.InvariantCulture)}"));

            return Results.Text(string.Join('\n', lines) + '\n', "text/plain; version=0.0.4");
        });

        monitoring.MapGet("/export/influx", async (
            IMonitoringService service,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await service.BuildExportSnapshotAsync(cancellationToken);
            var timestamp = snapshot.Dashboard.GeneratedUtc.ToUnixTimeMilliseconds() * 1_000_000L;
            var fields = string.Join(
                ",",
                snapshot.NumericMetrics
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value.ToString(CultureInfo.InvariantCulture)}"));

            return Results.Text(
                $"deluno_monitoring,instance=local {fields} {timestamp}\n",
                "text/plain; charset=utf-8");
        });

        return api;
    }
}
