using Deluno.Contracts;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Realtime;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Platform;

public static class PlatformEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapGroup("/api/settings");
        var auth = endpoints.MapGroup("/api/auth");

        settings.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var snapshot = await repository.GetAsync(cancellationToken);
            return Results.Ok(snapshot);
        });

        settings.MapPut(string.Empty, async (
            HttpContext httpContext,
            UpdatePlatformSettingsRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateSettings(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var snapshot = await repository.SaveAsync(request, cancellationToken);
            return Results.Ok(snapshot);
        });

        var libraries = endpoints.MapGroup("/api/libraries");
        var qualityProfiles = endpoints.MapGroup("/api/quality-profiles");
        var tags = endpoints.MapGroup("/api/tags");
        var intakeSources = endpoints.MapGroup("/api/intake-sources");
        var customFormats = endpoints.MapGroup("/api/custom-formats");
        var destinationRules = endpoints.MapGroup("/api/destination-rules");
        var policySets = endpoints.MapGroup("/api/policy-sets");
        var libraryViews = endpoints.MapGroup("/api/library-views");
        auth.MapPost("/login", async (
            LoginRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["username"] = ["Username is required."],
                    ["password"] = ["Password is required."]
                });
            }

            var login = await repository.ValidateUserCredentialsAsync(request.Username, request.Password, cancellationToken);
            if (login is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new LoginResponse(
                AccessToken: UserAuthorization.IssueAccessToken(login),
                User: login));
        });

        auth.MapGet("/bootstrap-status", async (
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var requiresSetup = await repository.RequiresBootstrapAsync(cancellationToken);
            return Results.Ok(new BootstrapStatusResponse(RequiresSetup: requiresSetup));
        });

        auth.MapPost("/bootstrap", async (
            BootstrapUserRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (!await repository.RequiresBootstrapAsync(cancellationToken))
            {
                return Results.Conflict(new
                {
                    message = "Deluno has already been configured."
                });
            }

            var errors = ValidateBootstrap(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var created = await repository.BootstrapUserAsync(request, cancellationToken);
            return Results.Ok(new LoginResponse(
                AccessToken: UserAuthorization.IssueAccessToken(created),
                User: created));
        });

        auth.MapPut("/password", async (
            HttpContext httpContext,
            ChangePasswordRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (!UserAuthorization.TryReadUser(httpContext, out var user) || user is null)
            {
                return Results.Unauthorized();
            }

            var errors = ValidatePasswordChange(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var changed = await repository.ChangeUserPasswordAsync(
                user.Id,
                request.CurrentPassword!,
                request.NewPassword!,
                cancellationToken);

            if (!changed)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["Current password is not correct."]
                });
            }

            return Results.NoContent();
        });

        qualityProfiles.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListQualityProfilesAsync(cancellationToken);
            return Results.Ok(items);
        });

        qualityProfiles.MapPost(string.Empty, async (
            HttpContext httpContext,
            CreateQualityProfileRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateQualityProfile(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateQualityProfileAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        qualityProfiles.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            UpdateQualityProfileRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateQualityProfile(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateQualityProfileAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        qualityProfiles.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteQualityProfileAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        qualityProfiles.MapPut("order", async (
            HttpContext httpContext,
            ReorderQualityProfilesRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.Ids is null || request.Ids.Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ids"] = ["Provide at least one quality profile id."]
                });
            }

            await repository.ReorderQualityProfilesAsync(request.Ids, cancellationToken);
            return Results.NoContent();
        });

        tags.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListTagsAsync(cancellationToken);
            return Results.Ok(items);
        });

        tags.MapPost(string.Empty, async (
            HttpContext httpContext,
            CreateTagRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateTag(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateTagAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        tags.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            UpdateTagRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateTag(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateTagAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        tags.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteTagAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        intakeSources.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListIntakeSourcesAsync(cancellationToken);
            return Results.Ok(items);
        });

        intakeSources.MapPost(string.Empty, async (
            HttpContext httpContext,
            CreateIntakeSourceRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateIntakeSource(request.Name, request.Provider, request.FeedUrl);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateIntakeSourceAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        intakeSources.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            UpdateIntakeSourceRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateIntakeSource(request.Name, request.Provider, request.FeedUrl);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateIntakeSourceAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        intakeSources.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteIntakeSourceAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        customFormats.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListCustomFormatsAsync(cancellationToken);
            return Results.Ok(items);
        });

        customFormats.MapPost(string.Empty, async (
            HttpContext httpContext,
            CreateCustomFormatRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateCustomFormat(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateCustomFormatAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        customFormats.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            UpdateCustomFormatRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateCustomFormat(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateCustomFormatAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        customFormats.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteCustomFormatAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        destinationRules.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListDestinationRulesAsync(cancellationToken);
            return Results.Ok(items);
        });

        destinationRules.MapPost("resolve", async (
            DestinationResolutionRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var settings = await repository.GetAsync(cancellationToken);
            var rules = await repository.ListDestinationRulesAsync(cancellationToken);
            var result = ResolveDestination(request, settings, rules);
            return Results.Ok(result);
        });

        destinationRules.MapPost(string.Empty, async (
            HttpContext httpContext,
            CreateDestinationRuleRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
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
            return Results.Ok(item);
        });

        destinationRules.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            UpdateDestinationRuleRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
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
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        destinationRules.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteDestinationRuleAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        policySets.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListPolicySetsAsync(cancellationToken);
            return Results.Ok(items);
        });

        policySets.MapPost(string.Empty, async (
            HttpContext httpContext,
            CreatePolicySetRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidatePolicySet(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreatePolicySetAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        policySets.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            UpdatePolicySetRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidatePolicySet(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdatePolicySetAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        policySets.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeletePolicySetAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        libraryViews.MapGet(string.Empty, async (
            HttpContext httpContext,
            string? variant,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
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
            CreateLibraryViewRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
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
            return Results.Ok(item);
        });

        libraryViews.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            UpdateLibraryViewRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
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
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        libraryViews.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
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
            return removed ? Results.NoContent() : Results.NotFound();
        });

        libraries.MapGet(string.Empty, async (
            IPlatformSettingsRepository repository,
            IJobQueueRepository jobs,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListLibrariesAsync(cancellationToken);
            var automation = await jobs.ListLibraryAutomationStatesAsync(cancellationToken);
            return Results.Ok(items.Select(item => MergeLibraryState(item, automation)));
        });

        libraries.MapPost(string.Empty, async (
            HttpContext httpContext,
            CreateLibraryRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
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
            return Results.Ok(item);
        });

        endpoints.MapPut("/api/libraries/{id}/automation", async (
            string id,
            HttpContext httpContext,
            UpdateLibraryAutomationRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
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
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        endpoints.MapPut("/api/libraries/{id}/quality-profile", async (
            string id,
            HttpContext httpContext,
            UpdateLibraryQualityProfileRequest request,
            IPlatformSettingsRepository repository,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = await repository.UpdateLibraryQualityProfileAsync(id, request, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

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

        endpoints.MapPost("/api/libraries/{id}/search-now", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            IJobQueueRepository jobs,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var library = (await repository.ListLibrariesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

            if (library is null)
            {
                return Results.NotFound();
            }

            var requested = await jobs.RequestLibrarySearchAsync(ToPlanItem(library), cancellationToken);
            return requested ? Results.Accepted() : Results.NotFound();
        });

        endpoints.MapPost("/api/libraries/{id}/import-existing", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            IExistingLibraryImportService importService,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await importService.ImportLibraryAsync(id, cancellationToken);
            if (result is null)
            {
                return Results.NotFound();
            }

            await activityFeedRepository.RecordActivityAsync(
                "library.import.existing",
                $"Deluno scanned {result.LibraryName} and brought in {result.ImportedCount} existing item{(result.ImportedCount == 1 ? "" : "s")}.",
                null,
                null,
                "library",
                result.LibraryId,
                cancellationToken);

            return Results.Ok(result);
        });

        libraries.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteLibraryAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        var connections = endpoints.MapGroup("/api/connections");

        var indexers = endpoints.MapGroup("/api/indexers");

        indexers.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListIndexersAsync(cancellationToken);
            return Results.Ok(items);
        });

        indexers.MapPost(string.Empty, async (
            HttpContext httpContext,
            CreateIndexerRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateIndexer(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateIndexerAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        indexers.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteIndexerAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        indexers.MapPost("{id}/test", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = (await repository.ListIndexersAsync(cancellationToken))
                .FirstOrDefault(indexer => string.Equals(indexer.Id, id, StringComparison.OrdinalIgnoreCase));

            if (item is null)
            {
                return Results.NotFound();
            }

            var (healthStatus, message) = await TestIndexerAsync(item, cancellationToken);
            var result = await repository.UpdateIndexerHealthAsync(id, healthStatus, message, cancellationToken);
            if (result is not null)
            {
                await realtimeEventPublisher.PublishHealthChangedAsync(
                    item.Name,
                    healthStatus == "ready" ? "healthy" : "degraded",
                    message,
                    cancellationToken);
            }

            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        var downloadClients = endpoints.MapGroup("/api/download-clients");

        downloadClients.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListDownloadClientsAsync(cancellationToken);
            return Results.Ok(items);
        });

        downloadClients.MapPost(string.Empty, async (
            HttpContext httpContext,
            CreateDownloadClientRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateDownloadClient(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateDownloadClientAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        downloadClients.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteDownloadClientAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        downloadClients.MapPost("{id}/test", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = (await repository.ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(client => string.Equals(client.Id, id, StringComparison.OrdinalIgnoreCase));

            if (item is null)
            {
                return Results.NotFound();
            }

            var (healthStatus, message) = await TestDownloadClientAsync(item, cancellationToken);
            var result = await repository.UpdateDownloadClientHealthAsync(id, healthStatus, message, cancellationToken);
            if (result is not null)
            {
                await realtimeEventPublisher.PublishHealthChangedAsync(
                    item.Name,
                    healthStatus == "ready" ? "healthy" : "degraded",
                    message,
                    cancellationToken);
            }

            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        endpoints.MapGet("/api/libraries/{id}/routing", async (
            string id,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var routing = await repository.GetLibraryRoutingAsync(id, cancellationToken);
            return routing is null ? Results.NotFound() : Results.Ok(routing);
        });

        endpoints.MapPut("/api/libraries/{id}/routing", async (
            string id,
            HttpContext httpContext,
            UpdateLibraryRoutingRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
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
            return routing is null ? Results.NotFound() : Results.Ok(routing);
        });

        connections.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListConnectionsAsync(cancellationToken);
            return Results.Ok(items);
        });

        connections.MapPost(string.Empty, async (
            HttpContext httpContext,
            CreateConnectionRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateConnection(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateConnectionAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        connections.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, repository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteConnectionAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> ValidateSettings(UpdatePlatformSettingsRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.AppInstanceName))
        {
            errors["appInstanceName"] = ["A library name is required."];
        }

        if (request.HostPort <= 0)
        {
            errors["hostPort"] = ["Choose a valid port number."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateDownloadClient(CreateDownloadClientRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this download client a name."];
        }

        return errors;
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
        }

        return errors;
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

        return errors;
    }

    private static Dictionary<string, string[]> ValidateQualityProfile(CreateQualityProfileRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this quality profile a name."];
        }

        var mediaType = request.MediaType?.Trim().ToLowerInvariant();
        if (mediaType is not ("movies" or "tv" or "tv shows" or "tvshows"))
        {
            errors["mediaType"] = ["Choose whether this profile is for Movies or TV Shows."];
        }

        if (string.IsNullOrWhiteSpace(request.CutoffQuality))
        {
            errors["cutoffQuality"] = ["Choose the quality Deluno should aim for."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateQualityProfile(UpdateQualityProfileRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this quality profile a name."];
        }

        if (string.IsNullOrWhiteSpace(request.CutoffQuality))
        {
            errors["cutoffQuality"] = ["Choose the quality Deluno should aim for."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateTag(string? name)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this tag a name."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateIntakeSource(string? name, string? provider, string? feedUrl)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this intake source a name."];
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            errors["provider"] = ["Choose a provider."];
        }

        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            errors["feedUrl"] = ["Add the source URL or identifier Deluno should poll."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateCustomFormat(string? name)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this custom format a name."];
        }

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

        return errors;
    }

    private static Dictionary<string, string[]> ValidatePolicySet(string? name)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this policy set a name."];
        }

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

    private static Dictionary<string, string[]> ValidateUser(string? username, string? password)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(username))
        {
            errors["username"] = ["Give this user a username."];
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            errors["password"] = ["Give this user a password."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateBootstrap(BootstrapUserRequest request)
    {
        var errors = ValidateUser(request.Username, request.Password);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors["displayName"] = ["Choose the name Deluno should show in the app."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidatePasswordChange(ChangePasswordRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            errors["currentPassword"] = ["Enter your current password."];
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            errors["newPassword"] = ["Enter a new password."];
        }
        else if (request.NewPassword.Length < 8)
        {
            errors["newPassword"] = ["Use at least 8 characters for the new password."];
        }

        if (!string.IsNullOrWhiteSpace(request.CurrentPassword) &&
            !string.IsNullOrWhiteSpace(request.NewPassword) &&
            string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
        {
            errors["newPassword"] = ["Choose a password that is different from your current password."];
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

    private static Dictionary<string, string[]> ValidateConnection(CreateConnectionRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this connection a name."];
        }

        if (string.IsNullOrWhiteSpace(request.ConnectionKind))
        {
            errors["connectionKind"] = ["Choose what kind of connection this is."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateIndexer(CreateIndexerRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this indexer a name."];
        }

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            errors["baseUrl"] = ["Add the address Deluno should use for this indexer."];
        }

        return errors;
    }

    private static async Task<(string healthStatus, string message)> TestIndexerAsync(
        IndexerItem item,
        CancellationToken cancellationToken)
    {
        var testUrl = BuildIndexerTestUrl(item);
        if (!Uri.TryCreate(testUrl, UriKind.Absolute, out var uri))
        {
            return ("attention", "The address is not valid yet.");
        }

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, cancellationToken);

            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 400)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (LooksLikeIndexerResponse(item.Protocol, body))
                {
                    return ("ready", $"Reached {uri.Host} and received a valid {FormatIndexerProtocol(item.Protocol)} response.");
                }

                return ("attention", $"Reached {uri.Host}, but the response did not look like {FormatIndexerProtocol(item.Protocol)}.");
            }

            return ("attention", $"Reached {uri.Host}, but it returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return ("attention", ex.Message);
        }
    }

    private static string BuildIndexerTestUrl(IndexerItem item)
    {
        if (!Uri.TryCreate(item.BaseUrl, UriKind.Absolute, out var uri))
        {
            return item.BaseUrl;
        }

        if (string.Equals(item.Protocol, "rss", StringComparison.OrdinalIgnoreCase))
        {
            return uri.ToString();
        }

        var separator = string.IsNullOrWhiteSpace(uri.Query) ? "?" : "&";
        var apiKey = string.IsNullOrWhiteSpace(item.ApiKey) ? string.Empty : $"&apikey={Uri.EscapeDataString(item.ApiKey)}";
        return $"{uri}{separator}t=caps{apiKey}";
    }

    private static bool LooksLikeIndexerResponse(string protocol, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        if (string.Equals(protocol, "rss", StringComparison.OrdinalIgnoreCase))
        {
            return body.Contains("<rss", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("<feed", StringComparison.OrdinalIgnoreCase);
        }

        return body.Contains("<caps", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("<rss", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("newznab", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("torznab", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatIndexerProtocol(string protocol)
    {
        return protocol.ToLowerInvariant() switch
        {
            "newznab" => "Newznab",
            "torznab" => "Torznab",
            "rss" => "RSS",
            _ => "indexer"
        };
    }

    private static async Task<(string healthStatus, string message)> TestDownloadClientAsync(
        DownloadClientItem item,
        CancellationToken cancellationToken)
    {
        var uri = ResolveDownloadClientEndpoint(item);
        if (uri is null)
        {
            return ("attention", "Add the client address before testing.");
        }

        try
        {
            return item.Protocol.ToLowerInvariant() switch
            {
                "qbittorrent" => await TestQbittorrentAsync(item, uri, cancellationToken),
                "sabnzbd" => await TestSabnzbdAsync(item, uri, cancellationToken),
                "transmission" => await TestTransmissionAsync(item, uri, cancellationToken),
                "deluge" => await TestDelugeAsync(item, uri, cancellationToken),
                "nzbget" => await TestNzbGetAsync(item, uri, cancellationToken),
                "utorrent" => await TestUTorrentAsync(item, uri, cancellationToken),
                _ => await TestGenericDownloadClientAsync(item, uri, cancellationToken)
            };
        }
        catch (Exception ex)
        {
            return ("attention", ex.Message);
        }
    }

    private static async Task<(string healthStatus, string message)> TestQbittorrentAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(8) };
        if (!string.IsNullOrWhiteSpace(item.Username) || !string.IsNullOrWhiteSpace(item.Secret))
        {
            using var login = await client.PostAsync(
                "api/v2/auth/login",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = item.Username ?? string.Empty,
                    ["password"] = item.Secret ?? string.Empty
                }),
                cancellationToken);
            if (!login.IsSuccessStatusCode)
            {
                return ("attention", $"qBittorrent rejected the login with {(int)login.StatusCode}.");
            }
        }

        using var response = await client.GetAsync("api/v2/app/version", cancellationToken);
        return response.IsSuccessStatusCode
            ? ("ready", $"Connected to qBittorrent at {uri.Host}:{uri.Port}.")
            : ("attention", $"qBittorrent returned {(int)response.StatusCode}.");
    }

    private static async Task<(string healthStatus, string message)> TestSabnzbdAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Secret))
        {
            return ("attention", "SABnzbd API key is missing.");
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var url = new Uri(uri, $"api?mode=version&output=json&apikey={Uri.EscapeDataString(item.Secret)}");
        using var response = await client.GetAsync(url, cancellationToken);
        return response.IsSuccessStatusCode
            ? ("ready", $"Connected to SABnzbd at {uri.Host}:{uri.Port}.")
            : ("attention", $"SABnzbd returned {(int)response.StatusCode}.");
    }

    private static async Task<(string healthStatus, string message)> TestTransmissionAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        AddBasicAuth(client, item);
        var endpoint = new Uri(uri, "transmission/rpc");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new TransmissionRequest("session-get", new Dictionary<string, object>()))
        };
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict && response.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
        {
            request.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", values.FirstOrDefault());
            using var retry = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new TransmissionRequest("session-get", new Dictionary<string, object>())),
                Headers = { { "X-Transmission-Session-Id", values.FirstOrDefault() ?? string.Empty } }
            }, cancellationToken);
            return retry.IsSuccessStatusCode
                ? ("ready", $"Connected to Transmission at {uri.Host}:{uri.Port}.")
                : ("attention", $"Transmission returned {(int)retry.StatusCode}.");
        }

        return response.IsSuccessStatusCode
            ? ("ready", $"Connected to Transmission at {uri.Host}:{uri.Port}.")
            : ("attention", $"Transmission returned {(int)response.StatusCode}.");
    }

    private static async Task<(string healthStatus, string message)> TestDelugeAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var login = new DelugeRequest("auth.login", [item.Secret ?? string.Empty]);
        using var response = await client.PostAsJsonAsync(new Uri(uri, "json"), login, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ("attention", $"Deluge returned {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body.Contains("true", StringComparison.OrdinalIgnoreCase)
            ? ("ready", $"Connected to Deluge at {uri.Host}:{uri.Port}.")
            : ("attention", "Deluge login failed. Check the Web UI password.");
    }

    private static async Task<(string healthStatus, string message)> TestNzbGetAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        AddBasicAuth(client, item);
        using var response = await client.PostAsJsonAsync(
            new Uri(uri, "jsonrpc"),
            new NzbGetRequest("version", []),
            cancellationToken);
        return response.IsSuccessStatusCode
            ? ("ready", $"Connected to NZBGet at {uri.Host}:{uri.Port}.")
            : ("attention", $"NZBGet returned {(int)response.StatusCode}.");
    }

    private static async Task<(string healthStatus, string message)> TestUTorrentAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), Credentials = BuildCredential(item) };
        using var client = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(8) };
        var html = await client.GetStringAsync("gui/token.html", cancellationToken);
        return html.Contains("<div", StringComparison.OrdinalIgnoreCase)
            ? ("ready", $"Connected to uTorrent at {uri.Host}:{uri.Port}.")
            : ("attention", "uTorrent token endpoint did not return the expected response.");
    }

    private static async Task<(string healthStatus, string message)> TestGenericDownloadClientAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await client.SendAsync(request, cancellationToken);

        if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 500)
        {
            return ("ready", $"Reached {item.Name} at {uri.Host}:{uri.Port}.");
        }

        return ("attention", $"Reached {uri.Host}, but it returned {(int)response.StatusCode}.");
    }

    private static Uri? ResolveDownloadClientEndpoint(DownloadClientItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.EndpointUrl) &&
            Uri.TryCreate(EnsureTrailingSlash(item.EndpointUrl), UriKind.Absolute, out var endpoint))
        {
            return endpoint;
        }

        if (string.IsNullOrWhiteSpace(item.Host) || item.Port is null)
        {
            return null;
        }

        return Uri.TryCreate($"http://{item.Host}:{item.Port}/", UriKind.Absolute, out var generated)
            ? generated
            : null;
    }

    private static string EnsureTrailingSlash(string value)
        => value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";

    private static void AddBasicAuth(HttpClient client, DownloadClientItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Username) && string.IsNullOrWhiteSpace(item.Secret))
        {
            return;
        }

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{item.Username ?? string.Empty}:{item.Secret ?? string.Empty}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private static NetworkCredential? BuildCredential(DownloadClientItem item)
        => string.IsNullOrWhiteSpace(item.Username) && string.IsNullOrWhiteSpace(item.Secret)
            ? null
            : new NetworkCredential(item.Username ?? string.Empty, item.Secret ?? string.Empty);

    private sealed record TransmissionRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("arguments")] IReadOnlyDictionary<string, object> Arguments);

    private sealed record DelugeRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object[] Params,
        [property: JsonPropertyName("id")] int Id = 1);

    private sealed record NzbGetRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object[] Params);

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

    private static LibraryAutomationPlanItem ToPlanItem(LibraryItem library)
    {
        return new LibraryAutomationPlanItem(
            LibraryId: library.Id,
            LibraryName: library.Name,
            MediaType: library.MediaType,
            AutoSearchEnabled: library.AutoSearchEnabled,
            MissingSearchEnabled: library.MissingSearchEnabled,
            UpgradeSearchEnabled: library.UpgradeSearchEnabled,
            SearchIntervalHours: library.SearchIntervalHours,
            RetryDelayHours: library.RetryDelayHours,
            MaxItemsPerRun: library.MaxItemsPerRun);
    }

    private static DestinationResolutionResult ResolveDestination(
        DestinationResolutionRequest request,
        PlatformSettingsSnapshot settings,
        IReadOnlyList<DestinationRuleItem> rules)
    {
        var mediaType = NormalizeMediaType(request.MediaType);
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Untitled" : request.Title.Trim();
        var rootFallback = mediaType == "tv"
            ? settings.SeriesRootPath ?? settings.MovieRootPath ?? string.Empty
            : settings.MovieRootPath ?? settings.SeriesRootPath ?? string.Empty;

        var match = rules
            .Where(rule => rule.IsEnabled && string.Equals(NormalizeMediaType(rule.MediaType), mediaType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(rule => rule.Priority)
            .FirstOrDefault(rule => MatchesDestinationRule(rule, request));

        var rootPath = match?.RootPath ?? rootFallback;
        var template = match?.FolderTemplate ??
                       (mediaType == "tv" ? settings.SeriesFolderFormat : settings.MovieFolderFormat);
        var folderName = ApplyFolderTemplate(template, title, request.Year);
        var fullPath = string.IsNullOrWhiteSpace(rootPath)
            ? folderName
            : Path.Combine(rootPath, folderName);

        return new DestinationResolutionResult(
            MediaType: mediaType,
            Title: title,
            Year: request.Year,
            RootPath: rootPath,
            FolderName: folderName,
            FullPath: fullPath,
            MatchedRuleId: match?.Id,
            MatchedRuleName: match?.Name,
            Reason: match is null
                ? "No destination rule matched, so Deluno used the default root folder."
                : $"Matched {match.MatchKind} rule '{match.Name}' with priority {match.Priority}.");
    }

    private static string NormalizeMediaType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() is "tv" or "series" or "shows"
            ? "tv"
            : "movies";

    private static bool MatchesDestinationRule(DestinationRuleItem rule, DestinationResolutionRequest request)
    {
        var expected = rule.MatchValue.Trim();
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        return rule.MatchKind.Trim().ToLowerInvariant() switch
        {
            "genre" => ContainsAny(request.Genres, expected),
            "tag" => ContainsAny(request.Tags, expected),
            "studio" => ContainsText(request.Studio, expected),
            "language" or "originallanguage" => ContainsText(request.OriginalLanguage, expected),
            "title" => ContainsText(request.Title, expected),
            _ => ContainsAny(request.Genres, expected) || ContainsAny(request.Tags, expected)
        };
    }

    private static bool ContainsAny(IReadOnlyList<string>? values, string expected)
        => values?.Any(value => ContainsText(value, expected)) == true;

    private static bool ContainsText(string? value, string expected)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static string ApplyFolderTemplate(string? template, string title, int? year)
    {
        var resolved = string.IsNullOrWhiteSpace(template)
            ? "{Title} ({Year})"
            : template;
        var safeTitle = SanitizePathSegment(title);
        var safeYear = year?.ToString(CultureInfo.InvariantCulture) ?? "Unknown Year";

        return SanitizePathSegment(resolved
            .Replace("{Movie Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{MovieTitle}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{Series Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{SeriesTitle}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{Release Year}", safeYear, StringComparison.OrdinalIgnoreCase)
            .Replace("{ReleaseYear}", safeYear, StringComparison.OrdinalIgnoreCase)
            .Replace("{Series Year}", safeYear, StringComparison.OrdinalIgnoreCase)
            .Replace("{SeriesYear}", safeYear, StringComparison.OrdinalIgnoreCase)
            .Replace("{Year}", safeYear, StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned.Trim();
    }
}

