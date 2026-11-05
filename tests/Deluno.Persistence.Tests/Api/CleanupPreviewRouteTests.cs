using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>GET /api/download-clients/{clientId}/queue/{queueItemId}/cleanup-preview: what removing a queue item would do, and who may ask.</summary>
public sealed class CleanupPreviewRouteTests
{
    private static readonly DownloadCleanupPreview Preview = new(
        "client-1", "item-1", "Film.2020.1080p.WEB-DL-GRP", "Stalled download", "It has not moved for 3 checks.",
        "Remove from the client", "The download and its files", RemovalAllowed: true, ReplacementSearchWillRun: true,
        RequiresReview: false, StrikeThreshold: 3, BlocksRelease: true, PurgesPayload: true);

    /// <summary>Answers only the preview; anything else this route does not call throws.</summary>
    private class PreviewOnly : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IDownloadClientTelemetryService.PreviewCleanupAsync))
            {
                throw new NotSupportedException(targetMethod.Name);
            }

            var known = (string)args![0]! == Preview.ClientId && (string)args[1]! == Preview.QueueItemId;
            return Task.FromResult(known ? Preview : null);
        }
    }

    private static Task<ApplicationTestHost> StartAsync()
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientTelemetryService>();
            services.AddSingleton(DispatchProxy.Create<IDownloadClientTelemetryService, PreviewOnly>());
        });

    [Fact]
    public async Task A_queued_item_answers_what_removing_it_would_do()
    {
        await using var app = await StartAsync();

        var response = await app.Client.GetAsync("/api/download-clients/client-1/queue/item-1/cleanup-preview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Film.2020.1080p.WEB-DL-GRP", body.GetProperty("releaseName").GetString());
        Assert.Equal("The download and its files", body.GetProperty("affectedFiles").GetString());
        Assert.True(body.GetProperty("removalAllowed").GetBoolean());
        Assert.True(body.GetProperty("replacementSearchWillRun").GetBoolean());
        Assert.True(body.GetProperty("blocksRelease").GetBoolean());
        Assert.True(body.GetProperty("purgesPayload").GetBoolean());
    }

    [Fact]
    public async Task An_item_that_is_not_queued_is_a_not_found_and_a_stranger_is_refused()
    {
        await using var app = await StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/download-clients/client-1/queue/other/cleanup-preview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/download-clients/client-1/queue/item-1/cleanup-preview")).StatusCode);
    }
}
