using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Replace on a hand send for a film that is already downloading (coverage register, engines): when the client will not remove the
/// first download the send is refused as a 400 on duplicateAction with the client's own words and nothing new is sent, and a first
/// download that cannot be identified is never guessed at. Refs #1296.
/// </summary>
public sealed class AReplaceThatCannotRemoveTheFirstDownloadIsRefusedTests
{
    private const string Release = "Big.Buck.Bunny.2008.1080p.BluRay.x264-GRP";
    private const string SameFilmAgain = "Big.Buck.Bunny.2008.2160p.WEB-DL.x265-OTHER";

    [Fact]
    public async Task Replace_is_refused_on_duplicateAction_when_the_client_will_not_remove_the_first_download_and_nothing_new_is_sent()
    {
        var grabs = new CountingGrabService();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientGrabService>();
            services.AddSingleton<IDownloadClientGrabService>(grabs);
            services.RemoveAll<IDownloadClientTelemetryService>();
            services.AddSingleton(DispatchProxy.Create<IDownloadClientTelemetryService, RefusingRemovalStub>());
        });
        var filmId = await SeedAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await GrabAsync(app, filmId, Release)).StatusCode);

        using var replaced = await GrabAsync(app, filmId, SameFilmAgain, duplicateAction: "replace");

        Assert.Equal(HttpStatusCode.BadRequest, replaced.StatusCode);
        var errors = (await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("duplicateAction");
        Assert.Contains("could not be removed from", Assert.Single(errors.EnumerateArray()).GetString(), StringComparison.Ordinal);
        Assert.Contains("The client said no.", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, grabs.Count);
    }

    [Fact]
    public async Task Replace_cannot_remove_a_first_download_it_cannot_identify_and_says_to_remove_it_there_or_send_as_well()
    {
        var removed = new List<(string ClientId, string Item, bool FromClient, bool Blocklist)>();
        var telemetry = AHandSendForAFilmTests.TelemetryStub.Create(removed);

        var noItem = await ActiveDownloadChecks.ReplaceAsync(telemetry, new ActiveDownloadConflict("client-1", "qBittorrent", null, "First.Release", "the film"), CancellationToken.None);
        var noClient = await ActiveDownloadChecks.ReplaceAsync(telemetry, new ActiveDownloadConflict("", "qBittorrent", "hash", "First.Release", "the film"), CancellationToken.None);

        Assert.Equal("Deluno cannot tell which download in qBittorrent to remove. Remove it there, or send this one as well.", noItem);
        Assert.Equal(noItem, noClient);
        Assert.Empty(removed);
    }

    private static Task<HttpResponseMessage> GrabAsync(ApplicationTestHost app, string filmId, string releaseName, string? duplicateAction = null)
        => app.Client.PostAsJsonAsync($"/api/movies/{filmId}/grab", new
        {
            releaseName,
            indexerName = "Fixture",
            downloadUrl = "https://fixture.invalid/x.torrent",
            candidateQuality = releaseName.Contains("2160p") ? "WEB 2160p" : "Bluray 1080p",
            duplicateAction
        });

    private static async Task<string> SeedAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var client = await app.Services.GetRequiredService<IConnectionsRepository>().CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Fake", "qbittorrent", "localhost", 1234, null, null, null, "movies", "tv", null, 1, true),
            CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Big Buck Bunny", releaseYear = 2008, monitored = true, libraryId, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        return (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private sealed class CountingGrabService : IDownloadClientGrabService
    {
        public int Count { get; private set; }

        public Task<DownloadClientGrabResult> GrabAsync(string clientId, DownloadClientGrabRequest request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new DownloadClientGrabResult(clientId, request.ReleaseName, true, "sent", "Sent to the fake client.")
            {
                ExternalId = Count == 1 ? "fake-hash" : $"fake-hash-{Count}"
            });
        }
    }

    /// <summary>A client that answers every removal with "no".</summary>
    public class RefusingRemovalStub : AHandSendForAFilmTests.TelemetryStub
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Name == "RemoveWithOptionsAsync"
                ? Task.FromResult(new QueueRemovalResult((string)args![0]!, (string)args[1]!, "failed", false, "The client said no.", false, false, null, null))
                : base.Invoke(targetMethod, args);
    }
}
