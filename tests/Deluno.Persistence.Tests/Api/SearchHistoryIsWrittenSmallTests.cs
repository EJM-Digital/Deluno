using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>#1083: a search records the outcome and the first releases, not every release it compared.</summary>
public sealed class SearchHistoryIsWrittenSmallTests
{
    [Fact]
    public async Task A_recorded_search_keeps_its_outcome_and_the_first_releases_and_a_count()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Wanted Film", releaseYear = 2011, monitored = true });
        var movieId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var repository = app.Services.GetRequiredService<IMovieCatalogRepository>();

        var candidates = new JsonArray();
        for (var i = 0; i < 80; i++)
        {
            candidates.Add(new JsonObject
            {
                ["ReleaseName"] = $"Wanted.Film.{i}",
                ["Quality"] = "WEB 1080p",
                ["DecisionStatus"] = "eligible",
                ["DownloadUrl"] = "https://indexer.example/dl?apikey=secret" + new string('x', 400)
            });
        }

        var plan = new JsonObject { ["Candidates"] = candidates, ["Summary"] = "Found 80" }.ToJsonString();
        await repository.RecordSearchAttemptAsync(
            movieId, "lib", "automatic", "held", DateTimeOffset.UtcNow, null, "Found 80", "Wanted.Film.0", "Fixture", plan, CancellationToken.None);

        var row = Assert.Single(await repository.ListSearchHistoryAsync(CancellationToken.None));
        Assert.Equal("held", row.Outcome);
        Assert.Equal("Wanted.Film.0", row.ReleaseName);
        var details = JsonNode.Parse(row.DetailsJson!)!;
        Assert.Equal(80, details["CandidateCount"]!.GetValue<int>());
        Assert.Equal(6, details["Candidates"]!.AsArray().Count);
        Assert.DoesNotContain("apikey", row.DetailsJson!);
        Assert.True(row.DetailsJson!.Length < plan.Length / 5);
    }
}
