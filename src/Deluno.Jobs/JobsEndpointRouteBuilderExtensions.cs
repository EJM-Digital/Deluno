using Deluno.Jobs.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Jobs;

public static class JobsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoJobsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/jobs", async (
            int? take,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListAsync(Math.Clamp(take ?? 25, 1, 100), cancellationToken);
            return Results.Ok(items);
        });

        endpoints.MapGet("/api/activity", async (
            int? take,
            IActivityFeedRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListActivityAsync(Math.Clamp(take ?? 50, 1, 200), cancellationToken);
            return Results.Ok(items);
        });

        return endpoints;
    }
}
