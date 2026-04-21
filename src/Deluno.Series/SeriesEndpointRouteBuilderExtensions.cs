using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Series;

public static class SeriesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoSeriesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var series = endpoints.MapGroup("/api/series");

        series.MapGet("/", async (ISeriesCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListAsync(cancellationToken);
            return Results.Ok(items);
        });

        series.MapGet("/import-recovery", async (ISeriesCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var summary = await repository.GetImportRecoverySummaryAsync(cancellationToken);
            return Results.Ok(summary);
        });

        series.MapGet("/wanted", async (ISeriesCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var summary = await repository.GetWantedSummaryAsync(cancellationToken);
            return Results.Ok(summary);
        });

        series.MapPost("/import-recovery", async (
            CreateSeriesImportRecoveryCaseRequest request,
            ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateImportRecovery(request.Title, request.Summary);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.AddImportRecoveryCaseAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        series.MapDelete("/import-recovery/{id}", async (
            string id,
            ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var removed = await repository.DeleteImportRecoveryCaseAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        series.MapGet("/{id}", async (string id, ISeriesCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var item = await repository.GetByIdAsync(id, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        series.MapPost("/", async (
            CreateSeriesRequest request,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.AddAsync(request, cancellationToken);
            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            foreach (var library in libraries.Where(entry => entry.MediaType == "tv"))
            {
                await repository.EnsureWantedStateAsync(
                    item.Id,
                    library.Id,
                    "missing",
                    FormatWantedReason(library),
                    false,
                    false,
                    cancellationToken);
            }

            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "series.catalog.refresh",
                    Source: "series",
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        item.Id,
                        item.Title,
                        item.ImdbId
                    }),
                    RelatedEntityType: "series",
                    RelatedEntityId: item.Id),
                cancellationToken);
            return Results.Created($"/api/series/{item.Id}", item);
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> Validate(CreateSeriesRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            errors["title"] = ["A series title is required."];
        }

        if (request.StartYear is < 1888 or > 2100)
        {
            errors["startYear"] = ["Start year must be between 1888 and 2100."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateImportRecovery(string? title, string? summary)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(title))
        {
            errors["title"] = ["Give this import issue a TV show title."];
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            errors["summary"] = ["Add a short summary so Deluno can explain what went wrong."];
        }

        return errors;
    }

    private static string FormatWantedReason(Platform.Contracts.LibraryItem library)
    {
        if (!string.IsNullOrWhiteSpace(library.QualityProfileName) && !string.IsNullOrWhiteSpace(library.CutoffQuality))
        {
            return $"Deluno is still looking for this TV show for {library.QualityProfileName} and will keep upgrading until {library.CutoffQuality}.";
        }

        return "Deluno is still looking for this TV show.";
    }
}
