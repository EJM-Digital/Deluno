using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Guides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// POST /api/v1/guides/trash/sync/apply: how the route answers what the sync service says. The sync itself (the exact reviewed
/// snapshot is applied, a candidate that was not the preview is refused) is proven against the real service in
/// GuidePackageSyncServiceTests; here the service is made to refuse in each way it can and the route's answer is checked.
/// </summary>
public sealed class GuideSyncApplyRouteTests
{
    private sealed class RefusingSync(Exception refusal) : IGuidePackageSyncService
    {
        public Task<GuidePackageUpdatePreview> PreviewAsync(GuidePackageSyncRequest request, CancellationToken cancellationToken) => throw refusal;
        public Task<StoredGuidePackage> ApplyAsync(GuidePackageSyncRequest request, CancellationToken cancellationToken) => throw refusal;
    }

    private static Task<ApplicationTestHost> StartAsync(Exception refusal)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IGuidePackageSyncService>();
            services.AddSingleton<IGuidePackageSyncService>(new RefusingSync(refusal));
        });

    [Fact]
    public async Task A_request_the_service_cannot_read_is_answered_400_with_the_reason_under_sync()
    {
        await using var app = await StartAsync(new ArgumentException("Preview this update before applying it."));

        var response = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/sync/apply", new { expectedCurrentIntegritySha256 = "abc", expectedUpstreamRevision = "def" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("Preview this update before applying it.", errors.GetProperty("sync")[0].GetString());
    }

    [Fact]
    public async Task A_candidate_that_is_no_longer_the_one_previewed_is_answered_409_with_the_service_message()
    {
        await using var app = await StartAsync(new InvalidOperationException("The guide changed after the preview. Preview it again."));

        var response = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/sync/apply", new { expectedCurrentIntegritySha256 = "abc", expectedUpstreamRevision = "def" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("The guide changed after the preview. Preview it again.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }
}
