using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Movies;

public static class MoviesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoMoviesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var movies = endpoints.MapGroup("/api/movies");

        movies.MapGet("/", async (IMovieCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListAsync(cancellationToken);
            return Results.Ok(items);
        });

        movies.MapGet("/import-recovery", async (IMovieCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var summary = await repository.GetImportRecoverySummaryAsync(cancellationToken);
            return Results.Ok(summary);
        });

        movies.MapGet("/{id}", async (string id, IMovieCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var movie = await repository.GetByIdAsync(id, cancellationToken);
            return movie is null ? Results.NotFound() : Results.Ok(movie);
        });

        movies.MapPost("/", async (
            CreateMovieRequest request,
            IMovieCatalogRepository repository,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var movie = await repository.AddAsync(request, cancellationToken);
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "movies.catalog.refresh",
                    Source: "movies",
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        movie.Id,
                        movie.Title,
                        movie.ImdbId
                    }),
                    RelatedEntityType: "movie",
                    RelatedEntityId: movie.Id),
                cancellationToken);
            return Results.Created($"/api/movies/{movie.Id}", movie);
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> Validate(CreateMovieRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            errors["title"] = ["A movie title is required."];
        }

        if (request.ReleaseYear is < 1888 or > 2100)
        {
            errors["releaseYear"] = ["Release year must be between 1888 and 2100."];
        }

        return errors;
    }
}
