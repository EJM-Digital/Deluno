using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// #1167: Nosferatu's and The Twilight Zone's cases were closed by the download poller with no line anywhere, so a
/// case that had been on the screen for days simply vanished. Every path that closes a case on its own now writes one
/// plain Activity line.
/// </summary>
public sealed class ASettledRecoveryCaseWritesAnActivityLineTests
{
    private static async Task<string[]> LinesAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, null, null, CancellationToken.None))
            .Where(line => line.Category == "recovery.case-settled")
            .Select(line => line.Message)
            .ToArray();

    [Fact]
    public async Task A_film_case_the_download_poller_settles_writes_one_line_saying_why()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest("Nosferatu.1922.1080p.BluRay.x264-HD4U", "importFailed", "Could not import.", "Review.", """{"DispatchId":"d-nos"}"""),
            CancellationToken.None);
        var other = await movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest("Metropolis", "importFailed", "Could not import.", "Review.", """{"DispatchId":"d-other"}"""),
            CancellationToken.None);

        await app.Services.GetRequiredService<IDispatchRecoveryHandler>().HandleDispatchSettledAsync("d-nos", "The download was archived.", CancellationToken.None);

        var line = Assert.Single(await LinesAsync(app));
        Assert.Contains("Nosferatu.1922.1080p.BluRay.x264-HD4U", line);
        Assert.Contains("The download was archived.", line);
        Assert.DoesNotContain("Metropolis", line);
        Assert.Contains(other.Id, (await movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases.Select(item => item.Id));

        // Nothing left to close, nothing more to say.
        await app.Services.GetRequiredService<IDispatchRecoveryHandler>().HandleDispatchSettledAsync("d-nos", "The download was archived.", CancellationToken.None);
        Assert.Single(await LinesAsync(app));
    }

    [Fact]
    public async Task An_episode_case_settled_because_the_title_has_its_file_and_one_closed_by_a_deletion_each_write_a_line()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest("The.Twilight.Zone.1959.S01E02", "importFailed", "Could not import.", "Review.", """{"DispatchId":"d-tz"}"""),
            CancellationToken.None);
        await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest("Severance.S02E09", "downloadFailed", "Failed.", "Review.", """{"DispatchId":"d-sev"}"""),
            CancellationToken.None);
        var handler = app.Services.GetRequiredService<IDispatchRecoveryHandler>();

        await handler.HandleDispatchSettledAsync("d-tz", "The title already has its file.", CancellationToken.None);
        var closed = await handler.HandleScopeSettledAsync(
            ImportRecoveryCaseScope.ForDeleted(["d-sev"], [], []),
            "The download client was removed, so this is no longer something to answer.",
            CancellationToken.None);

        Assert.Equal(1, closed);
        var lines = await LinesAsync(app);
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, line => line.Contains("The.Twilight.Zone.1959.S01E02") && line.Contains("The title already has its file."));
        Assert.Contains(lines, line => line.Contains("Severance.S02E09") && line.Contains("no longer something to answer"));
    }
}
