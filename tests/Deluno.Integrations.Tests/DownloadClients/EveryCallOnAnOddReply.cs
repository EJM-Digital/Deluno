using Deluno.Tests.Recorded;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1268: asks a download client every question it has, in every form a caller can ask it, so a test can answer them all with one
/// odd reply (nothing, an empty object, an error with no message) and prove none of them throws. The kinds of reply are the
/// shapes a client sends when something is wrong; the recordings supply what a working client says.
/// </summary>
internal static class EveryCallOnAnOddReply
{
    /// <summary>The JSON-RPC method a request body names.</summary>
    public static string MethodOf(string body)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("method", out var method) ? method.GetString() ?? string.Empty : string.Empty;
    }

    public static readonly string[] MagnetHash = [RecordedReplies.Qbittorrent("category-moved").Hash()];

    public static IEnumerable<DownloadClientGrabRequest> GrabRequests(bool usenet)
    {
        if (usenet)
        {
            yield return new("Example.Movie.2026.1080p", "https://indexer.example/getnzb/1.nzb", "movies", "movies", "Example Indexer");
            yield return new("Example.Show.S01E01", "https://indexer.example/getnzb/2.nzb", "tv", null, "Example Indexer") { Priority = 1 };
            yield break;
        }

        yield return new("Example.Movie.2026.1080p", "magnet:?xt=urn:btih:" + MagnetHash[0] + "&dn=Example", "movies", "movies", "Example Indexer") { SaveFolder = "/downloads/movies" };
        yield return new("Example.Movie.2026.1080p", "magnet:?dn=Example", "movies", "movies", "Example Indexer");
        yield return new("Example.Movie.2026.1080p", "https://indexer.example/download/1", "movies", " ", "Example Indexer");
        yield return new("Example.Show.S01E01", "https://indexer.example/download/2", "tv", null, "Example Indexer") { TorrentFile = [1, 2, 3], SaveFolder = "/downloads/tv" };
    }

    public static IEnumerable<string> AllActions()
        => typeof(DownloadClientActions).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).Append("juggle");

    /// <summary>
    /// A client that cannot be reached says so by throwing HttpRequestException, and one that answers with another app's page by throwing
    /// JsonException (its callers catch both: ClientsAnsweredWithAnotherAppsPageTests); nothing else may escape.
    /// </summary>
    private static async Task Safely(Func<Task> ask)
    {
        try
        {
            await ask();
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            // The documented ways a client says it cannot be asked.
        }
    }

    /// <summary>Every question, each once.</summary>
    public static async Task AskAsync(DownloadClientBase client, DownloadClientItem item, bool usenet, string hash)
    {
        // Side by side: a grab of a torrent waits a few seconds for the client to list it.
        await Task.WhenAll(GrabRequests(usenet).Select(request => Safely(() => client.GrabAsync(item, request, CancellationToken.None))));
        await Safely(() => client.GetSnapshotAsync(item, DateTimeOffset.UtcNow, CancellationToken.None));
        await Safely(() => client.GetFilesAsync(item, hash, CancellationToken.None));
        foreach (var action in AllActions())
        {
            await Safely(() => client.ExecuteActionAsync(item, action, hash, CancellationToken.None));
        }

        await Safely(() => client.SetCategoryAsync(item, hash, "seeding", "movies", CancellationToken.None));
        await Safely(() => client.SetCategoryAsync(item, hash, "seeding", null, CancellationToken.None));
        await Safely(() => client.CheckCategoryAsync(item, "movies", CancellationToken.None));
        await Safely(() => client.CheckCategoryAsync(item with { Location = "/downloads" }, "movies", CancellationToken.None));
        await Safely(() => client.CheckKeepSeedingCategoryAsync(item, "seeding", CancellationToken.None));
        await Safely(() => client.GetHistoryAsync(item, DateTimeOffset.UtcNow, CancellationToken.None));
        await Safely(() => client.ClearWarningsAsync(item, CancellationToken.None));
        await Safely(() => client.ResumeClientAsync(item, CancellationToken.None));
        await Safely(() => client.HasUsenetProviderAsync(item, CancellationToken.None));
        await Safely(() => client.SetUsenetProviderAsync(item, new Deluno.Connections.Usenet.UsenetProviderRequest("news.example", 563, true, "user", "pw", 8), CancellationToken.None));
    }
}
