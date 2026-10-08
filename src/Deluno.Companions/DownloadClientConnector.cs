using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Connections.Usenet;

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
/// <param name="NoProvider">The client answered and its sign-in works, but it is a Usenet client with no news server yet (#1142). It is connected, not healthy.</param>
public sealed record DownloadClientConnectResult(string Name, bool Created, bool Healthy, string Message, DownloadClientItem? Client = null, bool NoProvider = false);

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

    /// <summary>The port a client of this kind listens on when none is saved (the vendor default, which is what a saved null port means).</summary>
    public static int DefaultPortFor(string? protocol) => protocol?.ToLowerInvariant() switch
    {
        "transmission" => 9091,
        "deluge" => 8112,
        "nzbget" => 6789,
        _ => 8080
    };

    /// <param name="saveOnlyWhenHealthy">A person clicked "Use": a connection that would not pass its test is never saved, so nothing broken is left behind; the failure is returned instead.</param>
    public async Task<DownloadClientConnectResult> ConnectAsync(DownloadClientWiring wiring, int attempts, TimeSpan delay, CancellationToken cancellationToken, bool saveOnlyWhenHealthy = false)
    {
        var existing = (await connections.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(client => string.Equals(client.Protocol, wiring.Protocol, StringComparison.OrdinalIgnoreCase) && IsLoopback(client.Host) && (client.Port ?? DefaultPortFor(client.Protocol)) == wiring.Port);
        var now = DateTimeOffset.UtcNow;
        var draft = existing ?? new DownloadClientItem(
            string.Empty, wiring.Name, wiring.Protocol, wiring.Host, wiring.Port, wiring.Username, wiring.Secret, null,
            false, null, wiring.MoviesCategory, wiring.TvCategory, null, 10, true, "unknown", null, null, null, null, now, now);
        var client = saveOnlyWhenHealthy ? draft : existing ?? await CreateAsync(wiring, cancellationToken);

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

        var healthy = test.HealthStatus == "healthy";
        // A Usenet client with no news server yet answers, signs in and is not healthy. That is not a failed
        // sign-in: it is saved so it shows "No Usenet provider set up" until the person adds theirs (#1142).
        var noProvider = !healthy && test.FailureCategory == "configuration" && string.Equals(test.Message, UsenetProviderText.NoProvider, StringComparison.Ordinal);
        if (existing is null && saveOnlyWhenHealthy)
        {
            if (!healthy && !noProvider)
            {
                // Nothing is saved: the person is told why and can type the sign-in themselves.
                return new DownloadClientConnectResult(wiring.Name, false, false, test.Message, null);
            }

            client = await CreateAsync(wiring, cancellationToken);
        }

        await connections.UpdateDownloadClientHealthAsync(client.Id, test.HealthStatus, test.Message, test.FailureCategory, null, cancellationToken);
        return new DownloadClientConnectResult(client.Name, existing is null, healthy, test.Message, client, noProvider);
    }

    private Task<DownloadClientItem> CreateAsync(DownloadClientWiring wiring, CancellationToken cancellationToken)
        => connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest(wiring.Name, wiring.Protocol, wiring.Host, wiring.Port, wiring.Username, wiring.Secret, null, wiring.MoviesCategory, wiring.TvCategory, null, 10, true),
            cancellationToken);
}
