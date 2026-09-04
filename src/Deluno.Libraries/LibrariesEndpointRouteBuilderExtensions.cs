using System.Text.Json;
using Deluno.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Deluno.Security;
using Deluno.Security.Contracts;
using static Deluno.Contracts.DelunoValueNormalizers;

namespace Deluno.Libraries;

/// <summary>
/// /api/libraries, /api/destination-rules and /api/library-views. Split out of
/// PlatformEndpointRouteBuilderExtensions by ADR-001 Step 1; handler bodies are
/// unchanged apart from the repository type and explicit [FromServices].
/// The library-scoped search-now/skip-cycle/import-existing actions and the
/// rules-vs-global-settings destination "resolve" endpoint stay in
/// Deluno.Platform, which references Deluno.Libraries for LibraryItem.
/// </summary>
public static class LibrariesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoLibrariesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var libraries = endpoints.MapGroup("/api/libraries");
        var destinationRules = endpoints.MapGroup("/api/destination-rules");
        var libraryViews = endpoints.MapGroup("/api/library-views");

        destinationRules.MapGet(string.Empty, async ([FromServices] ILibrariesRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListDestinationRulesAsync(cancellationToken);
            return Results.Ok(items);
        });


        destinationRules.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateDestinationRuleRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateDestinationRule(request.Name, request.MatchValue, request.RootPath);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateDestinationRuleAsync(request, cancellationToken);
            // Destination rules are global configuration, not children of one
            // library. The rule id lets clients invalidate the library surface
            // without inventing a separate event family for this sub-resource.
            await realtimeEventPublisher.PublishEntityChangedAsync("Library", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        destinationRules.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateDestinationRuleRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateDestinationRule(request.Name, request.MatchValue, request.RootPath);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateDestinationRuleAsync(id, request, cancellationToken);
            if (item is null) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Library", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        destinationRules.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteDestinationRuleAsync(id, cancellationToken);
            if (!removed) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Library", id, cancellationToken);
            return Results.NoContent();
        });

        libraryViews.MapGet(string.Empty, async (
            HttpContext httpContext,
            string? variant,
            [FromServices] ILibrariesRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var user = httpContext.Items["deluno.user"] as UserItem;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var items = await repository.ListLibraryViewsAsync(user.Id, variant ?? "movies", cancellationToken);
            return Results.Ok(items);
        });

        libraryViews.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateLibraryViewRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var user = httpContext.Items["deluno.user"] as UserItem;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var errors = ValidateLibraryView(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateLibraryViewAsync(user.Id, request, cancellationToken);
            // Saved views are per-user presentation state rather than a library
            // row, so their own id is the only stable invalidation identity.
            await realtimeEventPublisher.PublishEntityChangedAsync("Library", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        libraryViews.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateLibraryViewRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var user = httpContext.Items["deluno.user"] as UserItem;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var errors = ValidateLibraryView(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateLibraryViewAsync(user.Id, id, request, cancellationToken);
            if (item is null) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Library", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        libraryViews.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var user = httpContext.Items["deluno.user"] as UserItem;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var removed = await repository.DeleteLibraryViewAsync(user.Id, id, cancellationToken);
            if (!removed) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Library", id, cancellationToken);
            return Results.NoContent();
        });

        libraries.MapGet(string.Empty, async (
            [FromServices] ILibrariesRepository repository,
            [FromServices] IJobQueueRepository jobs,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListLibrariesAsync(cancellationToken);
            var automation = await jobs.ListLibraryAutomationStatesAsync(cancellationToken);
            var merged = items.Select(item => MergeLibraryState(item, automation)).ToArray();

            return Results.Ok(merged);
        });

        libraries.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateLibraryRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IJobQueueRepository jobs,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateLibrary(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateLibraryAsync(request, cancellationToken);
            await jobs.EnsureLibraryAutomationStateAsync(ToAutomationPlan(item), cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("AutomationState", item.Id, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Library", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        endpoints.MapPut("/api/libraries/{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateLibraryDetailsRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IJobQueueRepository jobs,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors["name"] = ["Give this library a name."];
            }

            if (string.IsNullOrWhiteSpace(request.RootPath))
            {
                errors["rootPath"] = ["Choose a folder for this library."];
            }

            RequireExistingFolder(errors, "rootPath", request.RootPath, "this library lives in");
            RequireExistingFolder(errors, "downloadsPath", request.DownloadsPath, "downloads arrive in");

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateLibraryDetailsAsync(id, request, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            await jobs.EnsureLibraryAutomationStateAsync(ToAutomationPlan(item), cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("AutomationState", item.Id, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Library", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        endpoints.MapPut("/api/libraries/{id}/automation", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateLibraryAutomationRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IJobQueueRepository jobs,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateLibraryAutomation(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateLibraryAutomationAsync(id, request, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            await jobs.EnsureLibraryAutomationStateAsync(ToAutomationPlan(item), cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("AutomationState", item.Id, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Library", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        // The languages Deluno can name, so the picker and the parser cannot
        // drift apart. Served rather than duplicated in TypeScript: a code the
        // browser offered that the server did not recognise would be dropped on
        // save, and the only sign would be a language quietly missing from the
        // list you just wrote.
        endpoints.MapGet("/api/subtitle-languages", (HttpContext httpContext) =>
            Results.Ok(SubtitleLanguages.All.Select(language => new { code = language.Code, name = language.Name })));

        // Which subtitle languages this library wants (#301, DESIGN-002).
        //
        // No job is enqueued here. Changing the languages changes what the bar
        // says is wanted; it does not, on its own, mean go and fetch them —
        // that happens on the library's own search cycle, which already has a
        // window, an interval and a per-run cap. A second thing that reaches
        // out the moment a setting is saved is how you end up with two
        // schedulers, which DESIGN-002 exists to avoid.
        endpoints.MapPut("/api/libraries/{id}/subtitles", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateLibrarySubtitlesRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = await repository.UpdateLibrarySubtitlesAsync(id, request, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            await realtimeEventPublisher.PublishEntityChangedAsync("Library", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        endpoints.MapPut("/api/libraries/{id}/quality-profile", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateLibraryQualityProfileRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = await repository.UpdateLibraryQualityProfileAsync(id, request, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            await realtimeEventPublisher.PublishEntityChangedAsync("Library", item.Id, cancellationToken);

            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: item.MediaType == "tv" ? "series.quality.recalculate" : "movies.quality.recalculate",
                    Source: item.MediaType,
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        libraryId = item.Id,
                        libraryName = item.Name,
                        mediaType = item.MediaType,
                        cutoffQuality = item.CutoffQuality,
                        upgradeUntilCutoff = item.UpgradeUntilCutoff,
                        upgradeUnknownItems = item.UpgradeUnknownItems
                    }),
                    RelatedEntityType: "library",
                    RelatedEntityId: item.Id),
                cancellationToken);

            return Results.Ok(item);
        });

        endpoints.MapPut("/api/libraries/{id}/media-plan", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateLibraryMediaPlanRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = await repository.UpdateLibraryMediaPlanAsync(id, request, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            await realtimeEventPublisher.PublishEntityChangedAsync("Library", item.Id, cancellationToken);

            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: item.MediaType == "tv" ? "series.quality.recalculate" : "movies.quality.recalculate",
                    Source: item.MediaType,
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        libraryId = item.Id,
                        libraryName = item.Name,
                        mediaType = item.MediaType,
                        policySetId = item.DefaultPolicySetId,
                        policySetName = item.DefaultPolicySetName,
                        cutoffQuality = item.CutoffQuality,
                        upgradeUntilCutoff = item.UpgradeUntilCutoff
                    }),
                    RelatedEntityType: "library",
                    RelatedEntityId: item.Id),
                cancellationToken);

            return Results.Ok(item);
        });

        endpoints.MapPut("/api/libraries/{id}/workflow", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateLibraryWorkflowRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateLibraryWorkflow(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateLibraryWorkflowAsync(id, request, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            await realtimeEventPublisher.PublishEntityChangedAsync("Library", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        libraries.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IJobQueueRepository jobs,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteLibraryAsync(id, cancellationToken);
            if (!removed)
            {
                return Results.NotFound();
            }

            await jobs.RemoveLibraryAutomationStateAsync(id, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("AutomationState", id, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Library", id, cancellationToken);
            return Results.NoContent();
        });

        libraries.MapGet("export", async (
            HttpContext httpContext,
            [FromServices] ILibrariesRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var items = await repository.ListLibrariesAsync(cancellationToken);
            var export = items.Select(lib => new
            {
                lib.Name,
                lib.MediaType,
                lib.Purpose,
                lib.RootPath,
                lib.DownloadsPath,
                lib.QualityProfileId,
                lib.ImportWorkflow,
                lib.ProcessorName,
                lib.ProcessorOutputPath,
                lib.ProcessorTimeoutMinutes,
                lib.ProcessorFailureMode,
                lib.AutoSearchEnabled,
                lib.MissingSearchEnabled,
                lib.UpgradeSearchEnabled,
                lib.SearchIntervalHours,
                lib.RetryDelayHours,
                lib.MaxItemsPerRun,
                lib.SearchWindowStartHour,
                lib.SearchWindowEndHour
            });
            return Results.Ok(new { exportedAt = DateTimeOffset.UtcNow, libraries = export });
        });

        endpoints.MapGet("/api/libraries/{id}/routing", async (
            string id,
            [FromServices] ILibrariesRepository repository,
            CancellationToken cancellationToken) =>
        {
            var routing = await repository.GetLibraryRoutingAsync(id, cancellationToken);
            return routing is null ? Results.NotFound() : Results.Ok(routing);
        });

        endpoints.MapPut("/api/libraries/{id}/routing", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateLibraryRoutingRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateLibraryRouting(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var routing = await repository.SaveLibraryRoutingAsync(id, request, cancellationToken);
            if (routing is null)
            {
                return Results.NotFound();
            }

            await realtimeEventPublisher.PublishEntityChangedAsync("Library", id, cancellationToken);
            return Results.Ok(routing);
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> ValidateLibraryRouting(UpdateLibraryRoutingRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in request.Sources ?? [])
        {
            if (string.IsNullOrWhiteSpace(source.IndexerId))
            {
                errors["sources"] = ["Choose a source before saving library routing."];
                break;
            }
        }

        foreach (var client in request.DownloadClients ?? [])
        {
            if (string.IsNullOrWhiteSpace(client.DownloadClientId))
            {
                errors["downloadClients"] = ["Choose a download client before saving library routing."];
                break;
            }

            if (client.Category?.Length > 200)
            {
                errors["downloadClients"] = ["Keep a download category under 200 characters."];
                break;
            }
        }

        return errors;
    }


    /// <summary>
    /// Refuses a folder that is not there.
    ///
    /// <para>A library used to save happily with a root folder that did not
    /// exist. The form said so plainly — "That folder does not exist yet" — and
    /// then Create saved it anyway, and nothing in Deluno.Libraries ever creates
    /// a directory. So the library pointed at nothing, permanently, and the
    /// owner found out at import time. Saying it twice and meaning it once is
    /// worse than not checking at all.</para>
    /// </summary>
    private static void RequireExistingFolder(
        Dictionary<string, string[]> errors,
        string field,
        string? path,
        string whatItIsFor)
    {
        if (string.IsNullOrWhiteSpace(path) || errors.ContainsKey(field))
        {
            return;
        }

        if (!Directory.Exists(path.Trim()))
        {
            errors[field] = File.Exists(path.Trim())
                ? [$"{path.Trim()} is a file, not a folder. Choose the folder {whatItIsFor}."]
                : [$"{path.Trim()} does not exist. Create it first, or choose the folder {whatItIsFor}."];
        }
    }

    private static Dictionary<string, string[]> ValidateLibrary(CreateLibraryRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this library a name."];
        }

        if (string.IsNullOrWhiteSpace(request.RootPath))
        {
            errors["rootPath"] = ["Choose a folder for this library."];
        }

        var mediaType = request.MediaType?.Trim().ToLowerInvariant();
        if (mediaType is not ("movies" or "tv" or "tv shows" or "tvshows"))
        {
            errors["mediaType"] = ["Choose Movies or TV Shows."];
        }

        RequireExistingFolder(errors, "rootPath", request.RootPath, "this library lives in");
        RequireExistingFolder(errors, "downloadsPath", request.DownloadsPath, "downloads arrive in");
        RequireExistingFolder(errors, "processorOutputPath", request.ProcessorOutputPath, "your processor writes to");

        return errors;
    }


    private static Dictionary<string, string[]> ValidateDestinationRule(string? name, string? matchValue, string? rootPath)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this destination rule a name."];
        }

        if (string.IsNullOrWhiteSpace(matchValue))
        {
            errors["matchValue"] = ["Choose what this rule should match."];
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            errors["rootPath"] = ["Choose where matching titles should land."];
        }

        RequireExistingFolder(errors, "rootPath", rootPath, "matching titles should land in");

        return errors;
    }


    private static Dictionary<string, string[]> ValidateLibraryView(string? name)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this filter view a name."];
        }

        return errors;
    }


    private static Dictionary<string, string[]> ValidateLibraryAutomation(UpdateLibraryAutomationRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (request.SearchIntervalHours is <= 0)
        {
            errors["searchIntervalHours"] = ["Choose how often Deluno should check this library."];
        }

        if (request.RetryDelayHours is <= 0)
        {
            errors["retryDelayHours"] = ["Choose how long Deluno should wait before trying again."];
        }

        if (request.MaxItemsPerRun is <= 0)
        {
            errors["maxItemsPerRun"] = ["Choose how many titles Deluno should work through at a time."];
        }

        return errors;
    }


    private static Dictionary<string, string[]> ValidateLibraryWorkflow(UpdateLibraryWorkflowRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var workflow = NormalizeImportWorkflow(request.ImportWorkflow);

        if (workflow == "refine-before-import" && string.IsNullOrWhiteSpace(request.ProcessorOutputPath))
        {
            errors["processorOutputPath"] = ["Choose where the processor will write cleaned files before Deluno imports them."];
        }

        if (request.ProcessorTimeoutMinutes is <= 0)
        {
            errors["processorTimeoutMinutes"] = ["Choose how long Deluno should wait for the processor."];
        }

        return errors;
    }


    private static string NormalizeImportWorkflow(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "refine-before-import" or "refine" or "processor" or "processing" => "refine-before-import",
            _ => "standard"
        };

    private static LibraryAutomationPlanItem ToAutomationPlan(LibraryItem item)
        => new(
            LibraryId: item.Id,
            LibraryName: item.Name,
            MediaType: item.MediaType,
            AutoSearchEnabled: item.AutoSearchEnabled,
            MissingSearchEnabled: item.MissingSearchEnabled,
            UpgradeSearchEnabled: item.UpgradeSearchEnabled,
            SearchIntervalHours: item.SearchIntervalHours,
            RetryDelayHours: item.RetryDelayHours,
            MaxItemsPerRun: item.MaxItemsPerRun,
            SearchWindowStartHour: item.SearchWindowStartHour,
            SearchWindowEndHour: item.SearchWindowEndHour);

    /// <summary>
    /// Used to filter libraries by media scope for the bulk search-now
    /// trigger -- not related to indexer/download-client media scope,
    /// which is Deluno.Connections' own copy of the same small switch.
    /// </summary>

    private static LibraryItem MergeLibraryState(
        LibraryItem item,
        IReadOnlyDictionary<string, LibraryAutomationStateItem> automation)
    {
        if (!automation.TryGetValue(item.Id, out var state))
        {
            return item;
        }

        return item with
        {
            AutomationStatus = state.Status,
            SearchRequested = state.SearchRequested,
            LastSearchedUtc = state.LastCompletedUtc,
            NextSearchUtc = state.NextSearchUtc
        };
    }


}
