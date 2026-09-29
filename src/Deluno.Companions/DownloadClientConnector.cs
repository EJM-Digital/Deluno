using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Companions;

/// <summary>The same test the connection drawer's Test button runs, behind an interface so a test can answer for it.</summary>
public interface IDownloadClientTester
{
    Task<(string HealthStatus, string Message, string? FailureCategory)> TestAsync(DownloadClientItem client, CancellationToken cancellationToken);
}

public sealed class DrawerDownloadClientTester : IDownloadClientTester
{
    public async Task<(string HealthStatus, string Message, string? FailureCategory)> TestAsync(DownloadClientItem client, CancellationToken cancellationToken)
    {
        var (healthStatus, message, failureCategory) = await ConnectionsEndpointRouteBuilderExtensions.TestDownloadClientAsync(client, cancellationToken);
        return (healthStatus, message, failureCategory);
    }
}

/// <summary>What Deluno needs to add a download client that is already installed on this computer.</summary>
public sealed record DownloadClientWiring(
    string Name,
    string Protocol,
    string Host,
    int Port,
    string? Username,
    string? Secret,
    string MoviesCategory,
    string TvCategory);

/// <param name="Created">False when a client of that kind on this computer was already connected and was reused.</param>
public sealed record DownloadClientConnectResult(string Name, bool Created, bool Healthy, string Message, DownloadClientItem? Client = null);

/// <summary>
/// The one place a download client that is installed on this computer becomes a
/// Deluno connection: find an existing loopback client of the same kind or add one,
/// wait for it to answer the same test a person runs, and record the outcome as the
/// connection's health. Used by the "get the tools you need" step and by connecting
/// what the installer's picker installed (#1054), so there is no second copy.
/// A failed test never removes the connection.
/// </summary>
public sealed class DownloadClientConnector(IConnectionsRepository connections, IDownloadClientTester tester)
{
    public static bool IsLoopback(string? host) => host is "127.0.0.1" or "localhost" or "::1";

    public async Task<DownloadClientConnectResult> ConnectAsync(DownloadClientWiring wiring, int attempts, TimeSpan delay, CancellationToken cancellationToken)
    {
        var existing = (await connections.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(client => string.Equals(client.Protocol, wiring.Protocol, StringComparison.OrdinalIgnoreCase) && IsLoopback(client.Host));
        var client = existing ?? await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest(wiring.Name, wiring.Protocol, wiring.Host, wiring.Port, wiring.Username, wiring.Secret, null, wiring.MoviesCategory, wiring.TvCategory, null, 10, true),
            cancellationToken);

        // Waiting for it to answer, then the same test the drawer runs.
        (string HealthStatus, string Message, string? FailureCategory) test = ("unreachable", "Not tested.", null);
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            try
            {
                test = await tester.TestAsync(client, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                test = ("unreachable", $"{wiring.Name} did not answer.", null);
            }

            if (test.HealthStatus == "healthy")
            {
                break;
            }

            if (attempt + 1 < attempts)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        await connections.UpdateDownloadClientHealthAsync(client.Id, test.HealthStatus, test.Message, test.FailureCategory, null, cancellationToken);
        return new DownloadClientConnectResult(client.Name, existing is null, test.HealthStatus == "healthy", test.Message, client);
    }
}
