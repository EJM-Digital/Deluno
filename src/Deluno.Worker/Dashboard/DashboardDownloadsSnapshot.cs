using Deluno.Integrations.DownloadClients;

namespace Deluno.Worker.Dashboard;

/// <summary>
/// The last download-client reading, kept where the Dashboard can read it
/// without calling a client. Whatever already polls the clients (the progress
/// publisher) hands its reading here; the Dashboard never asks a client itself.
/// Null until the first reading, which the Dashboard reports as "not measured"
/// rather than "idle".
/// </summary>
public interface IDashboardDownloadsSnapshot
{
    DownloadTelemetryOverview? Latest { get; }

    void Publish(DownloadTelemetryOverview overview);
}

public sealed class DashboardDownloadsSnapshot : IDashboardDownloadsSnapshot
{
    private volatile DownloadTelemetryOverview? _latest;

    public DownloadTelemetryOverview? Latest => _latest;

    public void Publish(DownloadTelemetryOverview overview) => _latest = overview;
}
