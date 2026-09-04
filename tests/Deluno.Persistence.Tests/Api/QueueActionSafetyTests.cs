using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Removing, pausing and dismissing things that are already in flight.
///
/// <para><b>Why this file exists.</b> The coverage inventory was first read as
/// "the DELETE routes are the destructive ones", and the product owner pointed
/// out that this is not how Deluno removes most things: a stalled download, a
/// slow one, a failed import and an abandoned library scan are all cleared with
/// a POST. Those are the destructive actions an owner reaches for most often,
/// and none of them were tested.</para>
///
/// <para>It also found a real hole in the counting: because a route was matched
/// only up to its first parameter, every route beneath a parameterised path
/// inherited that path's coverage. <c>queue/actions</c> - the one that removes a
/// download - was reported as covered because something, somewhere, mentioned
/// <c>/api/download-clients</c>. Nothing tested it.</para>
///
/// <para>What is asserted here is the part that holds without a download client
/// on the other end: that Deluno refuses the destructive ones it should refuse,
/// and says why. Whether SABnzbd honours a delete is SABnzbd's business and the
/// lab's; whether Deluno asks it to when the owner has not opted in is
/// Deluno's.</para>
/// </summary>
public sealed class QueueActionSafetyTests
{
    /// <summary>
    /// The guard that matters most. An item in somebody's queue can be seeded,
    /// shared or cross-seeded, so removing it is opt-in - and until the owner
    /// opts in, Deluno must refuse rather than quietly forward the request.
    /// </summary>
    [Fact]
    public async Task Removing_a_queued_download_is_refused_until_the_owner_turns_it_on()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var clientId = await CreateDownloadClientAsync(app);

        var refused = await ActOnQueueAsync(app, clientId, action: "remove", queueItemId: "SABnzbd_nzo_abc");

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.False(refused.Succeeded);
        Assert.Contains("queue removal is disabled", refused.Message, StringComparison.OrdinalIgnoreCase);

        // And the refusal names the setting rather than leaving somebody
        // hunting for it.
        Assert.Contains("Download clients", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turning the setting on gets the request past the guard. It still fails,
    /// because no SABnzbd is listening in a test - but it fails for that reason
    /// rather than for the configuration one, which is what proves the toggle
    /// is what was standing in the way.
    /// </summary>
    [Fact]
    public async Task Turning_removal_on_gets_the_request_past_the_guard()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var clientId = await CreateDownloadClientAsync(app);

        var patch = await app.Client.PatchAsJsonAsync("/api/settings/", new { removeCompletedDownloads = true });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        var attempt = await ActOnQueueAsync(app, clientId, action: "remove", queueItemId: "SABnzbd_nzo_abc");

        Assert.False(attempt.Succeeded);
        Assert.DoesNotContain("queue removal is disabled", attempt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("obliterate")]
    [InlineData("")]
    [InlineData("delete-everything")]
    public async Task An_action_Deluno_does_not_recognise_is_refused_rather_than_guessed(string action)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var clientId = await CreateDownloadClientAsync(app);

        var refused = await ActOnQueueAsync(app, clientId, action, queueItemId: "SABnzbd_nzo_abc");

        Assert.False(refused.Succeeded);
        Assert.Equal("Unsupported action.", refused.Message);
    }

    [Fact]
    public async Task Acting_on_a_download_client_that_is_gone_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var refused = await ActOnQueueAsync(app, "no-such-client", action: "pause", queueItemId: "anything");

        Assert.False(refused.Succeeded);
        Assert.Equal("Download client was not found.", refused.Message);
    }

    /// <summary>
    /// Silencing a stalled or slow warning on an item nobody is tracking must
    /// not invent a record to silence.
    /// </summary>
    [Fact]
    public async Task Ignoring_a_health_warning_that_was_never_raised_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var clientId = await CreateDownloadClientAsync(app);

        var response = await app.Client.PostAsJsonAsync(
            $"/api/download-clients/{clientId}/queue/SABnzbd_nzo_abc/health/stalled/ignore",
            new { durationDays = 7 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Previewing_the_cleanup_of_an_item_that_is_not_queued_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var clientId = await CreateDownloadClientAsync(app);

        var response = await app.Client.GetAsync(
            $"/api/download-clients/{clientId}/queue/SABnzbd_nzo_abc/cleanup-preview");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Resolving and dismissing are the two ways a failed import leaves the
    /// list, and they are not the same thing: one says it was sorted out, the
    /// other says stop telling me. Both must clear it, and neither must clear
    /// a case that has already gone.
    /// </summary>
    [Theory]
    [InlineData("/api/movies/import-recovery/{0}/resolve")]
    [InlineData("/api/movies/import-recovery/{0}/dismiss")]
    public async Task Either_way_of_closing_a_failed_movie_import_clears_it_exactly_once(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var recoveryCase = await movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(
                Title: "Some Film (2019)",
                FailureKind: "unpack-failed",
                Summary: "The archive was password protected",
                RecommendedAction: "Grab a different release"),
            CancellationToken.None);

        var closed = await app.Client.PostAsJsonAsync(
            string.Format(route, recoveryCase.Id), new { });
        Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync());

        Assert.DoesNotContain(
            recoveryCase.Id,
            await ApiPayload.ListIdsAsync(app.Client, "/api/movies/import-recovery"));

        var again = await app.Client.PostAsJsonAsync(
            string.Format(route, recoveryCase.Id), new { });
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Theory]
    [InlineData("/api/series/import-recovery/{0}/resolve")]
    [InlineData("/api/series/import-recovery/{0}/dismiss")]
    public async Task Either_way_of_closing_a_failed_series_import_clears_it_exactly_once(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var recoveryCase = await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(
                Title: "Some Show S01E01",
                FailureKind: "no-matching-episode",
                Summary: "Nothing in the library matched the file",
                RecommendedAction: "Check the episode numbering"),
            CancellationToken.None);

        var closed = await app.Client.PostAsJsonAsync(
            string.Format(route, recoveryCase.Id), new { });
        Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync());

        Assert.DoesNotContain(
            recoveryCase.Id,
            await ApiPayload.ListIdsAsync(app.Client, "/api/series/import-recovery"));

        var again = await app.Client.PostAsJsonAsync(
            string.Format(route, recoveryCase.Id), new { });
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    /// <summary>
    /// Cancelling a scan that is not running must not report that it cancelled
    /// one, or the button lies about what it did.
    /// </summary>
    [Fact]
    public async Task Cancelling_an_import_scan_for_a_library_that_has_none_does_not_claim_it_stopped_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync(
            "/api/libraries/00000000-0000-0000-0000-000000000000/import-existing/cancel",
            new { });

        Assert.False(response.IsSuccessStatusCode);
    }

    /// <summary>
    /// Deluno trips a breaker on an indexer that keeps failing, and this is the
    /// button that puts it back. Pressing it on an indexer that is not there
    /// must say so rather than report a reset that never happened.
    /// </summary>
    [Fact]
    public async Task Resetting_a_tripped_indexer_works_and_only_for_one_that_exists()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var indexerId = await ApiPayload.CreateAsync(app.Client, "/api/indexers/", new
        {
            name = "Some indexer",
            protocol = "usenet",
            privacy = "private",
            baseUrl = "https://example.invalid",
            apiKey = "abcdef123456",
            priority = 1,
            categories = "2000,5000",
            tags = (string?)null,
            mediaScope = "both",
            isEnabled = true
        });

        var reset = await app.Client.PostAsJsonAsync($"/api/indexers/{indexerId}/reset-circuit", new { });
        Assert.True(reset.IsSuccessStatusCode, await reset.Content.ReadAsStringAsync());

        var missing = await app.Client.PostAsJsonAsync(
            "/api/indexers/00000000-0000-0000-0000-000000000000/reset-circuit", new { });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record QueueActionOutcome(HttpStatusCode Status, bool Succeeded, string Message);

    private static async Task<QueueActionOutcome> ActOnQueueAsync(
        ApplicationTestHost app,
        string clientId,
        string action,
        string queueItemId)
    {
        var response = await app.Client.PostAsJsonAsync(
            $"/api/download-clients/{clientId}/queue/actions",
            new { action, queueItemId });

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new QueueActionOutcome(
            response.StatusCode,
            document.RootElement.GetProperty("succeeded").GetBoolean(),
            document.RootElement.GetProperty("message").GetString() ?? string.Empty);
    }

    private static async Task<string> CreateDownloadClientAsync(ApplicationTestHost app)
    {
        var response = await app.Client.PostAsJsonAsync("/api/download-clients/", new
        {
            name = "SAB",
            protocol = "sabnzbd",
            host = "127.0.0.1",
            port = 8080,
            username = (string?)null,
            password = "an-api-key",
            endpointUrl = (string?)null,
            moviesCategory = "movies",
            tvCategory = "tv",
            categoryTemplate = (string?)null,
            priority = 1,
            isEnabled = true
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

}
