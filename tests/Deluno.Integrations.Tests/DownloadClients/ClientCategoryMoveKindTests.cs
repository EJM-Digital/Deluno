using Deluno.Contracts;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>#1240: a client that cannot answer is an unreachable client, not one that refused.</summary>
public sealed class ClientCategoryMoveKindTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Task<DownloadClientActionResult> Run(params ClientCategoryReading[] readings)
    {
        var client = new DownloadClientItem("c1", "qBit", "qbittorrent", "localhost", 8080, null, null, null, false, null, "movies", "tv", null, 1, true, "healthy", null, null, null, Now, Now, Now);
        var next = 0;
        return ClientCategoryMove.RunAsync(client, "hash", "seeding", "qBit",
            () => Task.FromResult(readings[Math.Min(next++, readings.Length - 1)]),
            _ => Task.FromResult(new DownloadClientActionResult("c1", "hash", DownloadClientActions.SetCategory, true, "told")));
    }

    private static readonly IntegrationFailureKind Rejected = IntegrationFailureFactory.FromLegacy("download-client", "c1", "qBit", "x", "rejected", "m").Kind;

    [Fact]
    public async Task A_client_that_could_not_be_asked_is_unreachable_not_rejected()
    {
        var result = await Run(new ClientCategoryReading(ClientHas.Unknown, null));

        Assert.NotEqual(Rejected, result.Failure!.Kind);
    }

    [Fact]
    public async Task A_client_that_did_not_confirm_the_move_is_unreachable_not_rejected()
    {
        var result = await Run(new ClientCategoryReading(ClientHas.Yes, "movies"), new ClientCategoryReading(ClientHas.Unknown, null));

        Assert.NotEqual(Rejected, result.Failure!.Kind);
    }

    [Fact]
    public async Task A_client_that_has_no_such_download_is_rejected()
    {
        var result = await Run(new ClientCategoryReading(ClientHas.No, null));

        Assert.Equal(Rejected, result.Failure!.Kind);
    }
}
