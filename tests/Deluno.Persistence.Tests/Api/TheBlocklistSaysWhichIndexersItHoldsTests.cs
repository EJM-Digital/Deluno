using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #585: the blocklist says when a refusal holds a release from every indexer.
/// The screen reads it from here rather than keeping a second copy of the rule.
/// </summary>
public sealed class TheBlocklistSaysWhichIndexersItHoldsTests
{
    [Theory]
    [InlineData(ImportFailurePolicy.ProcessorRejected, true)]
    [InlineData(ImportFailurePolicy.DownloadStalled, false)]
    public async Task Each_refusal_says_whether_it_holds_every_indexer(string reasonCode, bool everyIndexer)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await app.Services.GetRequiredService<IBlockedReleaseRepository>().BlockAsync(
            new BlockedRelease(
                "block-1",
                BlockedReleaseKeys.For("Arrival.2016.2160p", "Nebula"),
                "Arrival.2016.2160p",
                "Nebula",
                "movies",
                "movie-1",
                "Arrival",
                reasonCode,
                "Refused.",
                null,
                null,
                null,
                DateTimeOffset.Parse("2026-09-23T10:00:00Z")),
            CancellationToken.None);

        var response = await app.Client.GetAsync("/api/blocked-releases");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        using var document = JsonDocument.Parse(body);
        var list = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.GetProperty("items");
        Assert.Equal(everyIndexer, Assert.Single(list.EnumerateArray()).GetProperty("everyIndexer").GetBoolean());
    }
}
