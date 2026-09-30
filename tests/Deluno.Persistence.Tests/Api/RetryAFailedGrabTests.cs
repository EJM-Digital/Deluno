using System.Net;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Retry on a failed grab, and what a failed grab says about retrying (#621).
///
/// <para>Retry queued a library search with no target and one item per run, so
/// it searched whichever wanted title came first in the library. It also
/// stamped the dispatch's retry window with the moment it was pressed, which
/// the failure then read back as "Retry scheduled" at a time already gone. The
/// automatic retry pass that window was for never ran: nothing opened a window
/// for it, and no grab failure code matched one of its policies.</para>
/// </summary>
public sealed class RetryAFailedGrabTests
{
    private const string Release = "Wanted.Film.2011.1080p.WEB-DL-DELUNO";

    [Theory]
    [InlineData("movies", "movies")]
    [InlineData("movie", "movies")]
    [InlineData("tv", "tv")]
    public async Task Retry_searches_for_the_title_whose_grab_failed(string dispatchMediaType, string searchedMediaType)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);
        var dispatchId = await FailedGrabAsync(app, library.Id, dispatchMediaType, "title-wanted");

        var response = await app.Client.PostAsync($"/api/v1/download-dispatches/{dispatchId}/retry", null);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var search = Assert.Single(
            await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None),
            job => job.Source == "download_dispatch.retry");
        Assert.Equal(LibrarySearchJobTypes.For(searchedMediaType), search.JobType);
        using var payload = JsonDocument.Parse(search.PayloadJson!);
        var root = payload.RootElement;
        Assert.Equal("title-wanted", root.GetProperty("targetEntityId").GetString());
        Assert.Equal(searchedMediaType, root.GetProperty("mediaType").GetString());
        Assert.Equal(library.Name, root.GetProperty("libraryName").GetString());
        Assert.Equal(library.RetryDelayHours, root.GetProperty("retryDelayHours").GetInt32());
        Assert.DoesNotContain("nextRetryEligibleUtc", body);
    }

    [Fact]
    public async Task Retry_leaves_no_retry_window_behind()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);
        var dispatchId = await FailedGrabAsync(app, library.Id, "movies", "title-wanted", grabFailureCode: "access_denied");

        var response = await app.Client.PostAsync($"/api/v1/download-dispatches/{dispatchId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var dispatch = await app.Services.GetRequiredService<IDownloadDispatchesRepository>()
            .GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.NotNull(dispatch);
        Assert.Null(dispatch.NextRetryEligibleUtc);
        Assert.NotEqual(IntegrationRetryState.RetryScheduled, dispatch.Failure?.RetryState);
    }

    /// <summary>
    /// A row an older build's Retry stamped. The value is when somebody pressed
    /// the button, not when anything will happen, so it is not a retry time.
    /// </summary>
    [Fact]
    public async Task An_old_retry_window_does_not_read_as_a_scheduled_retry()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);
        var dispatchId = await FailedGrabAsync(app, library.Id, "movies", "title-wanted", grabFailureCode: "access_denied");
        await StampRetryWindowAsync(app, dispatchId, DateTimeOffset.UtcNow.AddHours(-3));

        using var detail = JsonDocument.Parse(await app.Client.GetStringAsync($"/api/v1/download-dispatches/{dispatchId}"));

        var failure = detail.RootElement.GetProperty("dispatch").GetProperty("failure");
        Assert.Equal("grab", failure.GetProperty("operation").GetString());
        Assert.Equal(nameof(IntegrationRetryState.NotRetryable), failure.GetProperty("retryState").GetString());
        Assert.Equal(JsonValueKind.Null, failure.GetProperty("retryAfterUtc").ValueKind);
    }

    [Fact]
    public async Task Retry_on_a_release_chosen_on_search_searches_for_nothing_and_says_why()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);
        var dispatchId = await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            library.Id, "movies", "release", "0a1b2c3d4e5f", Release, "Fixture", "client-1", "qBittorrent", "failed", null,
            cancellationToken: CancellationToken.None);

        var response = await app.Client.PostAsync($"/api/v1/download-dispatches/{dispatchId}/retry", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CHOSEN_ON_SEARCH", body);
        Assert.Contains("choose one on Search again", body);
        Assert.DoesNotContain(
            await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None),
            job => job.Source == "download_dispatch.retry");
    }

    [Fact]
    public async Task Retry_says_so_when_the_library_is_gone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var dispatchId = await FailedGrabAsync(app, "library-gone", "movies", "title-wanted");

        var response = await app.Client.PostAsync($"/api/v1/download-dispatches/{dispatchId}/retry", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("LIBRARY_NOT_FOUND", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(
            await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None),
            job => job.Source == "download_dispatch.retry");
    }

    private static async Task<LibraryItem> CreateLibraryAsync(ApplicationTestHost app)
        => await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                "Films", "movies", "library", Path.Combine(app.DataRoot, "movies"), Path.Combine(app.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 36, 25),
            CancellationToken.None);

    private static Task<string> FailedGrabAsync(
        ApplicationTestHost app,
        string libraryId,
        string mediaType,
        string entityId,
        string? grabFailureCode = null)
        => app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            libraryId, mediaType, mediaType == "tv" ? "episode" : "movie", entityId, Release, "Fixture", "client-1", "qBittorrent", "failed", null,
            grabFailureCode: grabFailureCode,
            cancellationToken: CancellationToken.None);

    private static async Task StampRetryWindowAsync(ApplicationTestHost app, string dispatchId, DateTimeOffset at)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE download_dispatches SET next_retry_eligible_utc = $at WHERE id = $id;";
        foreach (var (name, value) in new (string, object)[] { ("$at", at.ToString("O")), ("$id", dispatchId) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
