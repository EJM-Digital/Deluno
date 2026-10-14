using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Sending a release by hand for a film, over the real grab route and the real databases and with only the download client's reply
/// stood in for: a good release is sent and recorded, a risky one is refused until it is forced, and a film that already has a
/// download on its way asks first, then sends as well or replaces.
/// </summary>
public sealed class AHandSendForAFilmTests
{
    private const string Release = "Big.Buck.Bunny.2008.1080p.BluRay.x264-GRP";
    private const string SameFilmAgain = "Big.Buck.Bunny.2008.2160p.WEB-DL.x265-OTHER";

    [Fact]
    public async Task A_good_release_is_sent_once_recorded_as_a_send_and_nothing_is_written_to_the_library()
    {
        var grabs = new CountingGrabService();
        await using var app = await StartAsync(grabs, removed: null);
        var filmId = await SeedAsync(app);

        using var response = await GrabAsync(app, filmId, Release);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Release, body.GetProperty("releaseName").GetString());
        Assert.False(body.GetProperty("forceOverride").GetBoolean());
        Assert.Equal(1, grabs.Count);
        var dispatches = await app.Client.GetStringAsync("/api/download-dispatches");
        Assert.Contains(Release, dispatches);
        Assert.Contains(filmId, dispatches);
        Assert.Empty(Directory.GetFiles(app.DataRoot, "*.mkv", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_release_that_is_not_safe_to_send_by_itself_is_refused_with_the_force_hint_and_sent_only_when_forced()
    {
        var grabs = new CountingGrabService();
        await using var app = await StartAsync(grabs, removed: null);
        var filmId = await SeedAsync(app);
        const string risky = "Big.Buck.Bunny.2008.HDCAM.x264-GRP";

        using var refused = await GrabAsync(app, filmId, risky);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Use force override if you still want this exact release.", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, grabs.Count);

        using var forced = await GrabAsync(app, filmId, risky, force: true, overrideReason: "It is the only copy there is.");

        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
        var body = await forced.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("forceOverride").GetBoolean());
        Assert.Equal("It is the only copy there is.", body.GetProperty("overrideReason").GetString());
        Assert.Equal(1, grabs.Count);
    }

    [Fact]
    public async Task A_second_hand_send_for_a_film_that_is_already_downloading_asks_first_then_sends_as_well()
    {
        var grabs = new CountingGrabService();
        await using var app = await StartAsync(grabs, removed: null);
        var filmId = await SeedAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await GrabAsync(app, filmId, Release)).StatusCode);

        using var asked = await GrabAsync(app, filmId, SameFilmAgain);

        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        var body = await asked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already-downloading", body.GetProperty("code").GetString());
        Assert.Contains("is already downloading in", body.GetProperty("message").GetString());
        Assert.Equal(1, grabs.Count);

        Assert.Equal(HttpStatusCode.OK, (await GrabAsync(app, filmId, SameFilmAgain, duplicateAction: "send-as-well")).StatusCode);
        Assert.Equal(2, grabs.Count);
    }

    [Fact]
    public async Task Replace_removes_the_first_download_from_its_client_without_blocklisting_it_before_sending_the_new_one()
    {
        var grabs = new CountingGrabService();
        var removed = new List<(string ClientId, string Item, bool FromClient, bool Blocklist)>();
        await using var app = await StartAsync(grabs, removed);
        var filmId = await SeedAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await GrabAsync(app, filmId, Release)).StatusCode);

        using var replaced = await GrabAsync(app, filmId, SameFilmAgain, duplicateAction: "replace");

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var gone = Assert.Single(removed);
        Assert.Equal("fake-hash", gone.Item);
        Assert.True(gone.FromClient);
        Assert.False(gone.Blocklist, "replacing is not a verdict on the first release");
        Assert.Equal(2, grabs.Count);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<HttpResponseMessage> GrabAsync(ApplicationTestHost app, string filmId, string releaseName, string? duplicateAction = null, bool force = false, string? overrideReason = null)
        => app.Client.PostAsJsonAsync($"/api/movies/{filmId}/grab", new
        {
            releaseName,
            indexerName = "Fixture",
            downloadUrl = "https://fixture.invalid/x.torrent",
            candidateQuality = releaseName.Contains("2160p") ? "WEB 2160p" : "Bluray 1080p",
            force = force ? true : (bool?)null,
            overrideReason,
            duplicateAction
        });

    private static Task<ApplicationTestHost> StartAsync(
        CountingGrabService grabs,
        List<(string ClientId, string Item, bool FromClient, bool Blocklist)>? removed)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientGrabService>();
            services.AddSingleton<IDownloadClientGrabService>(grabs);
            if (removed is not null)
            {
                services.RemoveAll<IDownloadClientTelemetryService>();
                services.AddSingleton(TelemetryStub.Create(removed));
            }
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

    /// <summary>Answers only the removal a Replace asks for; every other call is a harmless empty answer.</summary>
    public class TelemetryStub : DispatchProxy
    {
        private List<(string ClientId, string Item, bool FromClient, bool Blocklist)> removed = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? result = null;
            if (targetMethod!.Name == "RemoveWithOptionsAsync")
            {
                removed.Add(((string)args![0]!, (string)args[1]!, (bool)args[2]!, (bool)args[3]!));
                result = new QueueRemovalResult((string)args[0]!, (string)args[1]!, "removed", true, "Removed.", false, false, null, null);
            }

            var returns = targetMethod.ReturnType;
            if (returns == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = returns.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [result]);
            }

            return returns.IsValueType ? Activator.CreateInstance(returns) : result;
        }

        public static IDownloadClientTelemetryService Create(List<(string ClientId, string Item, bool FromClient, bool Blocklist)> removed)
        {
            var proxy = Create<IDownloadClientTelemetryService, TelemetryStub>();
            ((TelemetryStub)(object)proxy).removed = removed;
            return proxy;
        }
    }
}
