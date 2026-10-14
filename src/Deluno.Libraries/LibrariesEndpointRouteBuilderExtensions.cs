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
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateDestinationRule(request.Name, request.MatchKind, request.MatchValue, request.RootPath);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "libraryId", Deluno.Infrastructure.Storage.ReferenceKind.Library,
                request.LibraryId, NormalizeMediaType(request.MediaType), cancellationToken);
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
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateDestinationRule(request.Name, request.MatchKind, request.MatchValue, request.RootPath);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "libraryId", Deluno.Infrastructure.Storage.ReferenceKind.Library,
                request.LibraryId, NormalizeMediaType(request.MediaType), cancellationToken);
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
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
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
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "libraryId", Deluno.Infrastructure.Storage.ReferenceKind.Library,
                request.LibraryId, null, cancellationToken);
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
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
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
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "libraryId", Deluno.Infrastructure.Storage.ReferenceKind.Library,
                request.LibraryId, null, cancellationToken);
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
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
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
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "qualityProfileId", Deluno.Infrastructure.Storage.ReferenceKind.QualityProfile,
                request.QualityProfileId, NormalizeMediaType(request.MediaType), cancellationToken);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "processorConnectionId", Deluno.Infrastructure.Storage.ReferenceKind.ProcessorConnection,
                request.ProcessorConnectionId, null, cancellationToken);
            if (errors.Count == 0)
            {
                var refusal = LibraryFolderLayout.RefuseSave(
                    (await repository.ListLibrariesAsync(cancellationToken)).Select(LibraryFolders.From),
                    new LibraryFolders(Guid.NewGuid().ToString("N"), request.Name?.Trim() ?? "New", request.RootPath, request.DownloadsPath, request.ProcessorOutputPath));
                if (refusal is not null)
                {
                    errors["folders"] = [refusal];
                }
            }

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
            [FromBody] JsonElement body,
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

            if (body.ValueKind != JsonValueKind.Object)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["Send the library's details as an object."] });
            }

            UpdateLibraryDetailsRequest request;
            try
            {
                request = body.Deserialize<UpdateLibraryDetailsRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? new UpdateLibraryDetailsRequest(null, null, null);
            }
            catch (JsonException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["The library's details could not be read."] });
            }

            // The downloads folder: left out means unchanged, sent (even empty or null) means
            // exactly that, so a caller that only renames a library never wipes it.
            var downloadsSent = body.EnumerateObject().Any(property => string.Equals(property.Name, "downloadsPath", StringComparison.OrdinalIgnoreCase));
            if (!downloadsSent)
            {
                var current = (await repository.ListLibrariesAsync(cancellationToken)).FirstOrDefault(library => library.Id == id);
                if (current is null)
                {
                    return Results.NotFound();
                }

                request = request with { DownloadsPath = current.DownloadsPath };
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

            if (errors.Count == 0)
            {
                var all = await repository.ListLibrariesAsync(cancellationToken);
                var current = all.FirstOrDefault(library => library.Id == id);
                if (current is not null)
                {
                    var refusal = LibraryFolderLayout.RefuseSave(
                        all.Select(LibraryFolders.From),
                        new LibraryFolders(id, string.IsNullOrWhiteSpace(request.Name) ? current.Name : request.Name.Trim(), request.RootPath, request.DownloadsPath, current.ProcessorOutputPath));
                    if (refusal is not null)
                    {
                        errors["folders"] = [refusal];
                    }
                }
            }

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

        endpoints.MapPut("/api/libraries/{id}/media-plan", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateLibraryMediaPlanRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var existingLibrary = (await repository.ListLibrariesAsync(cancellationToken))
                .FirstOrDefault(library => string.Equals(library.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existingLibrary is not null)
            {
                var planErrors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                    connectionFactory, planErrors, "policySetId", Deluno.Infrastructure.Storage.ReferenceKind.PolicySet,
                    request.PolicySetId, existingLibrary.MediaType, cancellationToken);
                if (planErrors.Count > 0)
                {
                    return Results.ValidationProblem(planErrors);
                }
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

        // The one place a library is given another quality profile after setup (#1169).
        // Every title that follows the library is judged again at once, so a raised
        // cutoff turns the files below it into upgrades without waiting for a rescan.
        endpoints.MapPut("/api/libraries/{id}/quality-profile", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateLibraryQualityProfileRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken,
            [FromServices] Deluno.Quality.IProfileChangeReevaluator? reevaluator = null) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (string.IsNullOrWhiteSpace(request.QualityProfileId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["qualityProfileId"] = ["Choose a quality profile for this library."]
                });
            }

            var profileLibrary = (await repository.ListLibrariesAsync(cancellationToken))
                .FirstOrDefault(library => string.Equals(library.Id, id, StringComparison.OrdinalIgnoreCase));
            if (profileLibrary is not null)
            {
                var profileErrors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                    connectionFactory, profileErrors, "qualityProfileId", Deluno.Infrastructure.Storage.ReferenceKind.QualityProfile,
                    request.QualityProfileId, profileLibrary.MediaType, cancellationToken);
                if (profileErrors.Count > 0)
                {
                    return Results.ValidationProblem(profileErrors);
                }
            }

            var item = await repository.UpdateLibraryQualityProfileAsync(id, request.QualityProfileId, cancellationToken);
            if (item is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["qualityProfileId"] = ["Choose one of the quality profiles made for this kind of library."]
                });
            }

            if (reevaluator is not null)
            {
                await reevaluator.ReevaluateForLibraryAsync(item.Id, cancellationToken);
            }

            await realtimeEventPublisher.PublishEntityChangedAsync("Library", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        endpoints.MapPut("/api/libraries/{id}/workflow", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateLibraryWorkflowRequest request,
            [FromServices] ILibrariesRepository repository,
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateLibraryWorkflow(request);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "processorConnectionId", Deluno.Infrastructure.Storage.ReferenceKind.ProcessorConnection,
                request.ProcessorConnectionId, null, cancellationToken);
            if (errors.Count == 0)
            {
                var all = await repository.ListLibrariesAsync(cancellationToken);
                var current = all.FirstOrDefault(library => library.Id == id);
                if (current is not null)
                {
                    var refusal = LibraryFolderLayout.RefuseSave(
                        all.Select(LibraryFolders.From),
                        new LibraryFolders(id, current.Name, current.RootPath, current.DownloadsPath, request.ProcessorOutputPath));
                    if (refusal is not null)
                    {
                        errors["folders"] = [refusal];
                    }
                }
            }

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

        // Deleting a library takes what it was doing with it (#569).
        //
        // Its downloads were still polled although nothing could import them,
        // its hand-offs still waited for a processor, and both went on raising
        // recovery cases about a library nobody had any more.
        libraries.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] ILibrariesRepository repository,
            [FromServices] IJobQueueRepository jobs,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            [FromServices] IDeletedThingCleanup cleanup,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // Read before it goes, so what is said names the library.
            var library = (await repository.ListLibrariesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            var removed = await repository.DeleteLibraryAsync(id, cancellationToken);
            if (!removed)
            {
                return Results.NotFound();
            }

            await cleanup.LibraryDeletedAsync(id, library?.Name ?? string.Empty, cancellationToken);
            await jobs.RemoveLibraryAutomationStateAsync(id, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("AutomationState", id, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Library", id, cancellationToken);
            return Results.NoContent();
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
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateLibraryRouting(request);
            foreach (var source in request.Sources ?? [])
            {
                if (errors.ContainsKey("sources"))
                {
                    break;
                }

                await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                    connectionFactory, errors, "sources", Deluno.Infrastructure.Storage.ReferenceKind.Indexer,
                    source.IndexerId, null, cancellationToken);
            }

            foreach (var client in request.DownloadClients ?? [])
            {
                if (errors.ContainsKey("downloadClients"))
                {
                    break;
                }

                await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                    connectionFactory, errors, "downloadClients", Deluno.Infrastructure.Storage.ReferenceKind.DownloadClient,
                    client.DownloadClientId, null, cancellationToken);
            }
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


    private static Dictionary<string, string[]> ValidateDestinationRule(string? name, string? matchKind, string? matchValue, string? rootPath)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this destination rule a name."];
        }

        if (!string.IsNullOrWhiteSpace(matchKind) && DestinationRuleKinds.Normalize(matchKind) is null)
        {
            errors["matchKind"] = [$"'{matchKind.Trim()}' is not a kind of rule. Choose one of: {string.Join(", ", DestinationRuleKinds.All)}."];
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

        if (request.SearchIntervalMinutes is <= 0)
        {
            errors["searchIntervalMinutes"] = ["Choose how often Deluno should check this library."];
        }

        if (request.RetryDelayHours is <= 0)
        {
            errors["retryDelayHours"] = ["Choose how long Deluno should wait before trying again."];
        }

        if (request.MaxItemsPerRun is <= 0)
        {
            errors["maxItemsPerRun"] = ["Choose how many titles Deluno should work through at a time."];
        }

        if (request.MaxMissingItemsPerRun is <= 0)
        {
            errors["maxMissingItemsPerRun"] = ["Choose how many missing titles Deluno should work through at a time."];
        }

        if (request.MaxUpgradeItemsPerRun is <= 0)
        {
            errors["maxUpgradeItemsPerRun"] = ["Choose how many upgrade candidates Deluno should work through at a time."];
        }

        return errors;
    }


    private static Dictionary<string, string[]> ValidateLibraryWorkflow(UpdateLibraryWorkflowRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var workflow = NormalizeImportWorkflow(request.ImportWorkflow);

        if (workflow == ImportWorkflows.RefineBeforeImport)
        {
            if (string.IsNullOrWhiteSpace(request.ProcessorOutputPath))
            {
                errors["processorOutputPath"] = ["Choose where the processor will write cleaned files before Deluno imports them."];
            }

            // A malformed path (e.g. "E:WeirReadyMovies" -- drive-relative, no
            // separator) used to save happily here, and then the library
            // refused every file the processor handed back, because none of
            // them was ever "inside" that folder. #631.
            RequireExistingFolder(errors, "processorOutputPath", request.ProcessorOutputPath, "your processor writes to");
        }

        if (request.ProcessorTimeoutMinutes is { } minutes &&
            (minutes < ProcessorStallLimits.MinimumMinutes || minutes > ProcessorStallLimits.MaximumMinutes))
        {
            errors["processorTimeoutMinutes"] = ["Choose between 24 hours and 7 days of no progress before Deluno stops waiting for the processor."];
        }

        return errors;
    }


    private static string NormalizeImportWorkflow(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            ImportWorkflows.RefineBeforeImport or "refine" or "processor" or "processing" => ImportWorkflows.RefineBeforeImport,
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
            SearchIntervalMinutes: item.SearchIntervalMinutes,
            RetryDelayHours: item.RetryDelayHours,
            MaxItemsPerRun: item.MaxItemsPerRun,
            SearchWindowStartHour: item.SearchWindowStartHour,
            SearchWindowEndHour: item.SearchWindowEndHour,
            MaxMissingItemsPerRun: item.MaxMissingItemsPerRun,
            MaxUpgradeItemsPerRun: item.MaxUpgradeItemsPerRun,
            SearchWindowDays: item.SearchWindowDays);

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
