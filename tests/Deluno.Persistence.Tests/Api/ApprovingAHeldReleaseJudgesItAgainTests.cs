using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Approve" on a release Deluno held for a person is judged again first, against today's rules, through the real route: an
/// unknown release is a 404, and a film that has its file since is a 409 that says why in plain words and sends nothing.
/// </summary>
public sealed class ApprovingAHeldReleaseJudgesItAgainTests
{
    private const string HeldPlan =
        """{"bestCandidate":{"releaseName":"Big.Buck.Bunny.2008.2160p.WEB-DL-GRP","indexerName":"Nyaa","indexerId":"ix1","quality":"WEB 2160p","sizeBytes":8000000000,"seeders":12,"downloadUrl":"https://indexer.example/get/1","decisionStatus":"held","summary":"Held for review by the typed release plan."},"candidates":[{"releaseName":"Big.Buck.Bunny.2008.2160p.WEB-DL-GRP","decisionStatus":"held","downloadUrl":"https://indexer.example/get/1"}]}""";

    [Fact]
    public async Task Approving_a_release_that_is_not_held_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsync("/api/attention/held-releases/no-such-held-release/approve", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Approving_a_release_for_a_film_that_has_its_file_since_is_a_409_with_the_plain_reason_and_sends_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var added = await movies.AddAsync(new CreateMovieRequest("Big Buck Bunny", 2008, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(added.Id, "lib", "missing", "Wanted", hasFile: false, null, "WEB 1080p", false, CancellationToken.None);
        await movies.RecordSearchAttemptAsync(added.Id, "lib", "automatic", "held", DateTimeOffset.UtcNow.AddHours(-1), null, "Held.", "Big.Buck.Bunny.2008.2160p.WEB-DL-GRP", "Nyaa", HeldPlan, CancellationToken.None);
        var held = Assert.Single(await app.Services.GetRequiredService<IHeldReleaseReader>().ListAsync(50, CancellationToken.None));
        await movies.EnsureWantedStateAsync(added.Id, "lib", "covered", "Has its file", hasFile: true, "WEB 2160p", "WEB 1080p", true, CancellationToken.None);

        using var response = await app.Client.PostAsync($"/api/attention/held-releases/{held.HistoryId}/approve", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("held-release-refused", body.GetProperty("code").GetString());
        Assert.Contains("no longer needs a release", body.GetProperty("message").GetString());
        Assert.Empty((await app.Services.GetRequiredService<Deluno.Jobs.Data.IDownloadDispatchesRepository>()
            .QueryDispatchesAsync(new Deluno.Jobs.Data.DispatchQueryFilter(), new Deluno.Jobs.Data.DispatchPaginationOptions(), CancellationToken.None)).Items);
    }
}
