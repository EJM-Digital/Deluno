using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>#1217: "Check now" on System asks every health check to run again, so rows that no longer hold clear.</summary>
public sealed class CheckNowRunsEveryHealthCheckTests
{
    [Fact]
    public async Task Check_now_asks_for_every_category_including_the_processor_checks()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/system/health/run", new { });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var asked = await app.Services.GetRequiredService<IHealthCheckRepository>().TakePendingImmediateRunsAsync(CancellationToken.None);
        Assert.Contains(HealthCheckCategories.Processor, asked);
        Assert.Contains(HealthCheckCategories.DownloadClient, asked);
        Assert.Contains(HealthCheckCategories.Library, asked);
        Assert.DoesNotContain(HealthCheckCategories.Notice, asked);
    }
}
