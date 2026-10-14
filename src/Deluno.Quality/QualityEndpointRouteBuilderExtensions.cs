using System.Text.Json;
using Deluno.Contracts;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.Guides;
using Deluno.Quality.Presets;
using Deluno.Quality.ReleasePreferences;
using Deluno.Quality.Scenarios;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Quality;

/// <summary>
/// /api/quality-profiles, /api/quality-model, /api/quality-profile-presets,
/// /api/custom-formats and /api/policy-sets. Split out of
/// PlatformEndpointRouteBuilderExtensions by ADR-001 Step 1; handler bodies
/// are unchanged apart from the repository type and explicit [FromServices].
/// Policy-set create/update still apply to assigned libraries via
/// <see cref="IPolicySetLibraryApplier"/> because Libraries has not moved
/// out of Platform yet.
/// </summary>
public static class QualityEndpointRouteBuilderExtensions
{
    /// <summary>
    /// #1326: GET leaves out the 1.5 MB pinned source inventory, so a package taken from GET and sent back has none. When it
    /// names the same upstream revision as the inventory shipped in this build, that inventory is its own pin: put it back
    /// rather than refuse a package Deluno itself issued. A package for any other revision is checked as it arrives.
    /// </summary>
    private static GuidePackageUpdateRequest WithPinnedSourceInventory(GuidePackageUpdateRequest request)
        => request.Package is { SourceInventory: null } package
           && string.Equals(package.Source.UpstreamRevision, GuideSourceInventoryCatalog.Current.UpstreamRevision, StringComparison.OrdinalIgnoreCase)
            ? request with { Package = package with { SourceInventory = GuideSourceInventoryCatalog.Current } }
            : request;

    /// <summary>
    /// Serves the immutable guide package used by the setup and quality
    /// screens. It is read-scoped because clients must be able to preview the
    /// package without gaining permission to change quality profiles.
    /// </summary>
    public static IEndpointRouteBuilder MapDelunoGuidePackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var guideEndpoints = endpoints
            .MapGroup("/api/v1/guides")
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);
        var guideWrites = endpoints
            .MapGroup("/api/v1/guides")
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        guideEndpoints.MapGet("/trash/package", async (
            IGuidePackageStore store,
            CancellationToken cancellationToken) =>
        {
            var package = (await store.GetCurrentAsync(cancellationToken)).Package;
            // The source inventory is deliberately a separate, on-demand
            // endpoint. Setup screens need the concise curated package, not a
            // 1.5 MB provenance payload on every visit.
            return Results.Json(package with { SourceInventory = null }, ReleasePreferenceJson.Options);
        });
        guideEndpoints.MapGet("/trash/inventory", async (
            IGuidePackageStore store,
            CancellationToken cancellationToken) => Results.Json(
                GuideCapabilityInventoryBuilder.Build((await store.GetCurrentAsync(cancellationToken)).Package),
                ReleasePreferenceJson.Options));
        guideEndpoints.MapGet("/trash/update-check", async (
            IGuideUpdateCheckService updateCheckService,
            CancellationToken cancellationToken) => Results.Json(
                await updateCheckService.GetAsync(cancellationToken),
                ReleasePreferenceJson.Options));
        guideEndpoints.MapGet("/trash/versions", async (
            IGuidePackageStore store,
            CancellationToken cancellationToken) => Results.Json(
                await store.ListAsync(cancellationToken),
                ReleasePreferenceJson.Options));
        guideEndpoints.MapGet("/trash/versions/{version:int}", async (
            int version,
            string? packageId,
            IGuidePackageStore store,
            CancellationToken cancellationToken) =>
        {
            var item = await store.GetAsync(packageId ?? GuidePackageCatalog.Current.Id, version, cancellationToken);
            return item is null ? Results.NotFound() : Results.Json(item, ReleasePreferenceJson.Options);
        });
        guideEndpoints.MapGet("/trash/profiles/{profileId}/compile", async (
            string profileId,
            string? mediaType,
            IGuidePackageStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var package = await store.GetCurrentAsync(cancellationToken);
                var compilation = GuidePlanCompiler.Compile(profileId, mediaType, package.Package);
                return Results.Json(compilation, ReleasePreferenceJson.Options);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        guideWrites.MapPost("/trash/preview", async (
            HttpContext httpContext,
            [FromBody] GuidePackageUpdateRequest request,
            IGuidePackageStore store,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            request = WithPinnedSourceInventory(request);

            var preview = await store.PreviewAsync(request, cancellationToken);
            return Results.Json(preview, ReleasePreferenceJson.Options);
        });

        guideWrites.MapPost("/trash/sync/preview", async (
            HttpContext httpContext,
            [FromBody] GuidePackageSyncRequest request,
            IGuidePackageSyncService syncService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            return Results.Json(
                await syncService.PreviewAsync(request, cancellationToken),
                ReleasePreferenceJson.Options);
        });

        guideWrites.MapPut("/trash/update-check/settings", async (
            HttpContext httpContext,
            [FromBody] UpdateGuideUpdateCheckSettingsRequest request,
            IGuideUpdateCheckService updateCheckService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            return Results.Json(
                await updateCheckService.SetEnabledAsync(request.IsEnabled, cancellationToken),
                ReleasePreferenceJson.Options);
        });

        guideWrites.MapPost("/trash/update-check/run", async (
            HttpContext httpContext,
            IGuideUpdateCheckService updateCheckService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            return Results.Json(
                await updateCheckService.CheckNowAsync(cancellationToken),
                ReleasePreferenceJson.Options);
        });

        guideWrites.MapPost("/trash/apply", async (
            HttpContext httpContext,
            [FromBody] GuidePackageUpdateRequest request,
            IGuidePackageStore store,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            request = WithPinnedSourceInventory(request);

            try
            {
                var applied = await store.ApplyAsync(request, cancellationToken);
                return Results.Json(applied, ReleasePreferenceJson.Options, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["package"] = [exception.Message]
                });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
        });

        // Every guide version is immutable and retained, which makes each
        // update a rollback point. This is the way back to one (#350).
        guideWrites.MapPost("/trash/versions/{version:int}/activate", async (
            int version,
            string? packageId,
            HttpContext httpContext,
            IGuidePackageStore store,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            try
            {
                var activated = await store.ActivateAsync(
                    packageId ?? GuidePackageCatalog.Current.Id,
                    version,
                    cancellationToken);
                return Results.Json(activated, ReleasePreferenceJson.Options);
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { message = exception.Message });
            }
            catch (InvalidDataException exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
        });

        guideWrites.MapPost("/trash/sync/apply", async (
            HttpContext httpContext,
            [FromBody] GuidePackageSyncRequest request,
            IGuidePackageSyncService syncService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            try
            {
                var applied = await syncService.ApplyAsync(request, cancellationToken);
                return Results.Json(applied, ReleasePreferenceJson.Options, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["sync"] = [exception.Message]
                });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
        });

        return endpoints;
    }

    /// <summary>
    /// Read-only contract surface for the typed release-preference model. It
    /// is mapped outside the write-only quality group so a dashboard or
    /// integration with the read scope can inspect the effective plan without
    /// receiving permission to edit profiles.
    /// </summary>
    public static IEndpointRouteBuilder MapDelunoReleasePreferenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var releasePreferences = endpoints
            .MapGroup("/api/v1/release-preferences")
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        var releasePreferenceWrites = endpoints
            .MapGroup("/api/v1/release-preferences")
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        releasePreferences.MapGet("/plans", async (
            string? mediaType,
            [FromServices] IReleasePreferencePlanRepository repository,
            CancellationToken cancellationToken) =>
        {
            var plans = await repository.ListAsync(mediaType, cancellationToken);
            return Results.Json(plans, ReleasePreferenceJson.Options);
        });

        releasePreferences.MapGet("/plans/{planId}", async (
            string planId,
            string? version,
            [FromServices] IReleasePreferencePlanRepository repository,
            CancellationToken cancellationToken) =>
        {
            var stored = await repository.GetAsync(planId, version, cancellationToken);
            return stored is null
                ? Results.NotFound()
                : Results.Json(stored, ReleasePreferenceJson.Options);
        });

        releasePreferences.MapPost("/preview", async (
            HttpContext httpContext,
            [FromBody] ReleasePreferencePreviewRequest request,
            [FromServices] IReleasePreferencePlanRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(request.PlanId))
            {
                errors["planId"] = ["A persisted release-preference plan is required."];
            }

            if (string.IsNullOrWhiteSpace(request.ReleaseName))
            {
                errors["releaseName"] = ["Release name is required."];
            }
            else if (request.ReleaseName.Trim().Length > 500)
            {
                errors["releaseName"] = ["Release name must be 500 characters or fewer."];
            }

            if (request.CurrentReleaseName?.Trim().Length > 500)
            {
                errors["currentReleaseName"] = ["Current release name must be 500 characters or fewer."];
            }

            if (request.Seeders is < 0)
            {
                errors["seeders"] = ["Seeders cannot be negative."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var stored = await repository.GetAsync(request.PlanId!.Trim(), request.PlanVersion, cancellationToken);
            if (stored is null)
            {
                return Results.NotFound();
            }

            var candidateFacts = ReleasePreferenceFactFactory.WithTransientSignals(
                stored.Plan,
                ReleasePreferenceFactFactory.FromReleaseName(
                        stored.Plan,
                        request.ReleaseName,
                        request.CandidateQuality,
                        "typed-preview")
                    .Concat(request.CandidateFacts ?? []),
                request.Seeders);
            var candidateEvaluation = ReleasePreferenceEvaluator.Evaluate(stored.Plan, candidateFacts);

            var hasCurrent = !string.IsNullOrWhiteSpace(request.CurrentReleaseName)
                || !string.IsNullOrWhiteSpace(request.CurrentQuality)
                || request.CurrentFacts is { Count: > 0 };
            string? currentReleaseName = null;
            IReadOnlyList<PreferenceFact>? currentFacts = null;
            PreferenceEvaluation? currentEvaluation = null;
            PreferenceComparison? comparison = null;
            if (hasCurrent)
            {
                currentReleaseName = request.CurrentReleaseName?.Trim();
                currentFacts = ReleasePreferenceFactFactory.FromReleaseName(
                        stored.Plan,
                        request.CurrentReleaseName,
                        request.CurrentQuality,
                        "typed-preview-current")
                    .Concat(request.CurrentFacts ?? [])
                    .ToArray();
                currentEvaluation = ReleasePreferenceEvaluator.Evaluate(stored.Plan, currentFacts);
                comparison = ReleasePreferenceEvaluator.Compare(stored.Plan, currentFacts, candidateFacts);
            }

            return Results.Json(
                new ReleasePreferencePreview(
                    request.ReleaseName!.Trim(),
                    stored.Plan.Id,
                    stored.Plan.Version,
                    stored.PlanHash,
                    candidateFacts,
                    candidateEvaluation,
                    currentReleaseName,
                    currentFacts,
                    currentEvaluation,
                    comparison),
                ReleasePreferenceJson.Options);
        });

        // Plans are immutable. Editing a plan means posting the next version;
        // the repository rejects a reused id/version whose definition differs.
        // That gives callers an atomic, auditable activation primitive without
        // creating a second mutable release-ranking store.
        releasePreferenceWrites.MapPost("/plans", async (
            HttpContext httpContext,
            [FromBody] ReleasePreferencePlan plan,
            [FromServices] IReleasePreferencePlanRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ReleasePreferencePlanValidator.Validate(plan);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["plan"] = errors.ToArray()
                });
            }

            try
            {
                var stored = await repository.SaveAsync(plan, cancellationToken);
                return Results.Json(stored, ReleasePreferenceJson.Options, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["plan"] = [exception.Message]
                });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
        });

        // #386: a step has to be able to show the judgement for answers nobody
        // has saved yet. Nothing is written - the plan is compiled in memory,
        // used once and dropped.
        releasePreferences.MapPost("/judge-draft", async (
            HttpContext httpContext,
            [FromBody] DraftProfileJudgementRequest request,
            [FromServices] IQualityRepository qualityRepository,
            [FromServices] IGuidePackageStore guidePackageStore,
            [FromServices] TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(request.ReleaseName))
            {
                errors["releaseName"] = ["Type a release name to see how this profile would judge it."];
            }
            else if (request.ReleaseName.Trim().Length > 500)
            {
                errors["releaseName"] = ["Release name must be 500 characters or fewer."];
            }

            if (request.CurrentReleaseName?.Trim().Length > 500)
            {
                errors["currentReleaseName"] = ["Current release name must be 500 characters or fewer."];
            }

            if (request.Seeders is < 0)
            {
                errors["seeders"] = ["Seeders cannot be negative."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var customFormats = await qualityRepository.ListCustomFormatsAsync(cancellationToken);
            var guidePackage = await guidePackageStore.GetCurrentAsync(cancellationToken);
            return Results.Json(
                DraftProfileJudge.Judge(request, customFormats, guidePackage.Package, timeProvider),
                ReleasePreferenceJson.Options);
        });

        releasePreferences.MapGet("/plans/quality-profile/{profileId}", async (
            string profileId,
            [FromServices] IQualityRepository qualityRepository,
            [FromServices] IReleasePreferencePlanRepository planRepository,
            [FromServices] IGuidePackageStore guidePackageStore,
            CancellationToken cancellationToken) =>
        {
            var profile = (await qualityRepository.ListQualityProfilesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                return Results.NotFound();
            }

            var customFormats = await qualityRepository.ListCustomFormatsAsync(cancellationToken);
            var guidePackage = await guidePackageStore.GetCurrentAsync(cancellationToken);
            var compilation = ReleasePreferencePlanFactory.CompileProfile(
                profile,
                customFormats,
                guidePackage.Package);
            var stored = await planRepository.SaveAsync(compilation.Plan, cancellationToken);
            return Results.Json(new ReleasePreferencePlanCompilation(
                PreferenceTraitRegistry.Current.Version,
                profile.Id,
                profile.Name,
                stored.Plan,
                stored.PlanHash,
                compilation.AdvancedRules,
                compilation.Warnings,
                compilation.RequiresReview,
                stored.CreatedUtc), ReleasePreferenceJson.Options);
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapDelunoQualityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var scenarioReads = endpoints
            .MapGroup("/api/media-plan-scenarios")
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        scenarioReads.MapGet(string.Empty, (string? mediaType) =>
        {
            var normalizedMediaType = string.IsNullOrWhiteSpace(mediaType)
                ? null
                : NormalizeScenarioMediaType(mediaType);
            var scenarios = MediaPlanScenarioCatalog.All
                .Where(scenario => normalizedMediaType is null || scenario.MediaTypes.Contains(normalizedMediaType, StringComparer.OrdinalIgnoreCase))
                .Select(scenario => new
                {
                    scenario.Id,
                    scenario.Name,
                    scenario.Description,
                    scenario.MediaTypes,
                    scenario.Requirements,
                    scenario.Version,
                    variants = scenario.Variants
                })
                .ToArray();
            return Results.Json(scenarios, ReleasePreferenceJson.Options);
        });

        scenarioReads.MapGet("/{id}/compile", (string id, string? mediaType, string? name) =>
        {
            try
            {
                return Results.Json(
                    MediaPlanScenarioCompiler.Compile(id, mediaType, name),
                    ReleasePreferenceJson.Options);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [ex.ParamName ?? "mediaType"] = [ex.Message]
                });
            }
        });

        var qualityProfiles = endpoints.MapGroup("/api/quality-profiles");
        var qualityModel = endpoints.MapGroup("/api/quality-model");
        var customFormats = endpoints.MapGroup("/api/custom-formats");
        var policySets = endpoints.MapGroup("/api/policy-sets");
        qualityProfiles.MapGet(string.Empty, async ([FromServices] IQualityRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListQualityProfilesAsync(cancellationToken);
            return Results.Ok(items);
        });

        qualityProfiles.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateQualityProfileRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IQualityModelService qualityModelService,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var model = await qualityModelService.GetAsync(cancellationToken);
            var errors = ValidateQualityProfile(request, model.Tiers.Select(tier => tier.Name));
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateQualityProfileAsync(request, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("QualityProfile", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        qualityProfiles.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateQualityProfileRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IQualityModelService qualityModelService,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken,
            [FromServices] IProfileChangeReevaluator? reevaluator = null) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var model = await qualityModelService.GetAsync(cancellationToken);
            var errors = ValidateQualityProfile(request, model.Tiers.Select(tier => tier.Name));
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateQualityProfileAsync(id, request, cancellationToken);
            if (item is not null)
            {
                // Judge everything the profile covers again (#1169): a raised cutoff
                // must turn the files below it into upgrades now, not when the next
                // import happens to touch them.
                if (reevaluator is not null)
                {
                    await reevaluator.ReevaluateForProfileAsync(item.Id, cancellationToken);
                }

                await realtimeEventPublisher.PublishEntityChangedAsync("QualityProfile", item.Id, cancellationToken);
            }
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        qualityProfiles.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] IQualityRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteQualityProfileAsync(id, cancellationToken);
            if (removed)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("QualityProfile", id, cancellationToken);
            }
            return removed ? Results.NoContent() : Results.NotFound();
        });

        qualityModel.MapGet(string.Empty, async (
            [FromServices] IQualityModelService service,
            CancellationToken cancellationToken) =>
        {
            var model = await service.GetAsync(cancellationToken);
            return Results.Ok(model);
        });

        qualityModel.MapPut(string.Empty, async (
            HttpContext httpContext,
            [FromBody] UpdateQualityModelRequest request,
            [FromServices] IQualityModelService service,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            try
            {
                var model = await service.SaveAsync(request, cancellationToken);
                return Results.Ok(model);
            }
            catch (InvalidOperationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["qualityModel"] = [ex.Message]
                });
            }
        });

        customFormats.MapGet(string.Empty, async ([FromServices] IQualityRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListCustomFormatsAsync(cancellationToken);
            return Results.Ok(items);
        });

        customFormats.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateCustomFormatRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IGuidePackageStore guidePackageStore,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // A rule made from a guide format sends its guide id and no
            // conditions, and gets the guide's own. The screens that make
            // these rules used to write the conditions themselves, three
            // different ways.
            if (!string.IsNullOrWhiteSpace(request.TrashId) && string.IsNullOrWhiteSpace(request.Conditions))
            {
                var package = (await guidePackageStore.GetCurrentAsync(cancellationToken)).Package;
                var guideFormat = package.CustomFormats.FirstOrDefault(format =>
                    string.Equals(format.TrashId, request.TrashId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (guideFormat is null || !GuideFormatConditions.AppliesTo(guideFormat, request.MediaType))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["trashId"] = [$"The guide has no format '{request.TrashId}' for {MediaPolicyCatalog.NormalizeMediaType(request.MediaType)}."]
                    });
                }

                request = request with
                {
                    Name = string.IsNullOrWhiteSpace(request.Name) ? guideFormat.Name : request.Name,
                    Conditions = GuideFormatConditions.For(guideFormat)
                };
            }

            var errors = ValidateCustomFormat(request.Name);
            AddDuplicateCustomFormatNameError(
                errors,
                await repository.ListCustomFormatsAsync(cancellationToken),
                request.Name,
                request.MediaType,
                exceptId: null);
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
            [FromBody] UpdateCustomFormatRequest request,
            [FromServices] IQualityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateCustomFormat(request.Name);
            AddDuplicateCustomFormatNameError(
                errors,
                await repository.ListCustomFormatsAsync(cancellationToken),
                request.Name,
                request.MediaType,
                exceptId: id);
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
            [FromServices] IQualityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteCustomFormatAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // #777: JSON import/export, separate from TRaSH guide sync. Export
        // always re-derives Deluno's own re-importable condition shape
        // (CustomFormatDefinitionMatcher.PreviewImportedConditions), even for
        // a format saved from an imported Radarr/Sonarr array, so exporting
        // and re-importing a format is always a clean round trip.
        customFormats.MapGet("export", async ([FromServices] IQualityRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListCustomFormatsAsync(cancellationToken);
            return Results.Json(items.Select(ToCustomFormatExportItem).ToArray(), ReleasePreferenceJson.Options);
        });

        customFormats.MapGet("{id}/export", async (
            string id,
            [FromServices] IQualityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListCustomFormatsAsync(cancellationToken);
            var item = items.FirstOrDefault(candidate => candidate.Id == id);
            return item is null
                ? Results.NotFound()
                : Results.Json(ToCustomFormatExportItem(item), ReleasePreferenceJson.Options);
        });

        customFormats.MapPost("import", async (
            HttpContext httpContext,
            [FromBody] JsonElement body,
            [FromServices] IQualityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // One format or an array of them (#777's single/bulk import). Anything else is not the
            // documented shape; a number or a piece of text is refused here rather than answered
            // with one error per "item" (#1280).
            if (body.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                return Results.BadRequest(new
                {
                    message = "Send one custom format as a JSON object, or a list of them as a JSON array."
                });
            }

            var items = body.ValueKind == JsonValueKind.Array
                ? body.EnumerateArray().ToArray()
                : [body];
            if (items.Length == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["conditions"] = ["Nothing to import."]
                });
            }

            var existing = (await repository.ListCustomFormatsAsync(cancellationToken)).ToList();
            var results = new List<CustomFormatImportResult>();
            foreach (var item in items)
            {
                var result = await ImportOneCustomFormatAsync(item, repository, existing, cancellationToken);
                results.Add(result);
                if (result.Imported)
                {
                    // A second item in the same batch reusing the name of the
                    // one just created must also be caught as a duplicate.
                    existing.Add(new CustomFormatItem(
                        result.Id!, result.Name!, ReadImportString(item, "mediaType") ?? "movies", 0, null, "[]", true,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                }
            }

            return Results.Json(results, ReleasePreferenceJson.Options);
        });

        policySets.MapGet(string.Empty, async ([FromServices] IQualityRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListPolicySetsAsync(cancellationToken);
            return Results.Ok(items);
        });

        policySets.MapGet("{id}/versions", async (
            string id,
            [FromServices] IQualityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var versions = await repository.ListMediaPlanVersionsAsync(id, cancellationToken);
            return Results.Ok(versions);
        });

        policySets.MapGet("{id}/versions/{version:int}", async (
            string id,
            int version,
            [FromServices] IQualityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var item = await repository.GetMediaPlanVersionAsync(id, version, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        policySets.MapPost("{id}/preview", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdatePolicySetRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var current = (await repository.ListPolicySetsAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return Results.NotFound();
            }

            var currentSnapshot = MediaPlanSnapshot.From(current);
            var proposedReleasePreferencePlan = current.ReleasePreferencePlan;
            var requestedQualityProfileId = string.IsNullOrWhiteSpace(request.QualityProfileId)
                ? null
                : request.QualityProfileId.Trim();
            if (!string.Equals(requestedQualityProfileId, current.QualityProfileId, StringComparison.OrdinalIgnoreCase))
            {
                if (requestedQualityProfileId is null)
                {
                    proposedReleasePreferencePlan = null;
                }
                else
                {
                    var problem = await Deluno.Infrastructure.Storage.ReferenceChecks.FindProblemAsync(
                        connectionFactory,
                        Deluno.Infrastructure.Storage.ReferenceKind.QualityProfile,
                        requestedQualityProfileId,
                        MediaPolicyCatalog.NormalizeMediaType(request.MediaType ?? current.MediaType),
                        cancellationToken);
                    if (problem is not null)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["qualityProfileId"] = [problem]
                        });
                    }

                    var proposedProfile = (await repository.ListQualityProfilesAsync(cancellationToken))
                        .First(profile => string.Equals(profile.Id, requestedQualityProfileId, StringComparison.OrdinalIgnoreCase));
                    proposedReleasePreferencePlan = proposedProfile.ReleasePreferencePlan;
                }
            }

            var proposedSnapshot = BuildProposedMediaPlanSnapshot(
                current,
                request,
                proposedReleasePreferencePlan);
            var changes = MediaPlanVersionCodec.Diff(currentSnapshot, proposedSnapshot);
            var latest = await repository.GetLatestMediaPlanVersionAsync(id, cancellationToken);
            return Results.Ok(new MediaPlanPreview(
                current.Id,
                latest?.Version,
                currentSnapshot,
                proposedSnapshot,
                changes,
                changes.Count > 0,
                latest?.PlanHash ?? MediaPlanVersionCodec.ComputeHash(currentSnapshot)));
        });

        policySets.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreatePolicySetRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            [FromServices] IPolicySetLibraryApplier libraryApplier,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken,
            [FromServices] IProfileChangeReevaluator? reevaluator = null) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidatePolicySet(request.Name);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "qualityProfileId",
                Deluno.Infrastructure.Storage.ReferenceKind.QualityProfile,
                request.QualityProfileId, MediaPolicyCatalog.NormalizeMediaType(request.MediaType), cancellationToken);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "destinationRuleId",
                Deluno.Infrastructure.Storage.ReferenceKind.DestinationRule,
                request.DestinationRuleId, MediaPolicyCatalog.NormalizeMediaType(request.MediaType), cancellationToken);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreatePolicySetAsync(request, cancellationToken);
            await libraryApplier.ApplyToAssignedLibrariesAsync(item.Id, cancellationToken);
            if (reevaluator is not null)
            {
                await reevaluator.ReevaluateForPolicySetAsync(item.Id, cancellationToken);
            }
            await realtimeEventPublisher.PublishEntityChangedAsync("PolicySet", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        policySets.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdatePolicySetRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            [FromServices] IPolicySetLibraryApplier libraryApplier,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken,
            [FromServices] IProfileChangeReevaluator? reevaluator = null) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidatePolicySet(request.Name);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "qualityProfileId",
                Deluno.Infrastructure.Storage.ReferenceKind.QualityProfile,
                request.QualityProfileId, MediaPolicyCatalog.NormalizeMediaType(request.MediaType), cancellationToken);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "destinationRuleId",
                Deluno.Infrastructure.Storage.ReferenceKind.DestinationRule,
                request.DestinationRuleId, MediaPolicyCatalog.NormalizeMediaType(request.MediaType), cancellationToken);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdatePolicySetAsync(id, request, cancellationToken);
            if (item is not null)
            {
                await libraryApplier.ApplyToAssignedLibrariesAsync(item.Id, cancellationToken);
                if (reevaluator is not null)
                {
                    await reevaluator.ReevaluateForPolicySetAsync(item.Id, cancellationToken);
                }
                await realtimeEventPublisher.PublishEntityChangedAsync("PolicySet", item.Id, cancellationToken);
            }
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        policySets.MapPost("{id}/rollback", async (
            string id,
            HttpContext httpContext,
            [FromBody] RollbackMediaPlanRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IPolicySetLibraryApplier libraryApplier,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken,
            [FromServices] IProfileChangeReevaluator? reevaluator = null) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.Version <= 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["version"] = ["Choose a positive media-plan version to restore."]
                });
            }

            var version = await repository.GetMediaPlanVersionAsync(id, request.Version, cancellationToken);
            if (version is null)
            {
                return Results.NotFound();
            }

            var snapshot = version.Snapshot;
            var qualityProfile = snapshot.QualityProfileId is null
                ? null
                : (await repository.ListQualityProfilesAsync(cancellationToken))
                    .FirstOrDefault(profile => string.Equals(
                        profile.Id,
                        snapshot.QualityProfileId,
                        StringComparison.OrdinalIgnoreCase));
            var conflict = MediaPlanRollbackGuard.Check(id, version.Version, snapshot, qualityProfile);
            if (conflict is not null)
            {
                return Results.Conflict(conflict);
            }

            var item = await repository.UpdatePolicySetAsync(
                id,
                new UpdatePolicySetRequest(
                    snapshot.Name,
                    snapshot.MediaType,
                    snapshot.QualityProfileId,
                    snapshot.DestinationRuleId,
                    snapshot.CustomFormatIds,
                    snapshot.SearchIntervalOverrideHours,
                    snapshot.RetryDelayOverrideHours,
                    snapshot.UpgradeUntilCutoff,
                    snapshot.IsEnabled,
                    snapshot.Notes,
                    snapshot.AutomationIntent),
                cancellationToken,
                "rollback");
            if (item is null)
            {
                return Results.NotFound();
            }

            await libraryApplier.ApplyToAssignedLibrariesAsync(item.Id, cancellationToken);
            if (reevaluator is not null)
            {
                await reevaluator.ReevaluateForPolicySetAsync(item.Id, cancellationToken);
            }
            await realtimeEventPublisher.PublishEntityChangedAsync("PolicySet", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        policySets.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] IQualityRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeletePolicySetAsync(id, cancellationToken);
            if (removed)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("PolicySet", id, cancellationToken);
            }
            return removed ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
    }

    private static string NormalizeScenarioMediaType(string value)
        => string.Equals(value.Trim(), "tv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "tv shows", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "tvshows", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movies";

    private static MediaPlanSnapshot BuildProposedMediaPlanSnapshot(
        PolicySetItem current,
        UpdatePolicySetRequest request,
        ReleasePreferencePlanReference? releasePreferencePlan)
    {
        var proposed = new PolicySetItem(
            current.Id,
            string.IsNullOrWhiteSpace(request.Name) ? current.Name : request.Name,
            request.MediaType ?? current.MediaType,
            request.QualityProfileId,
            null,
            request.DestinationRuleId,
            null,
            request.CustomFormatIds ?? string.Empty,
            request.SearchIntervalOverrideHours,
            request.RetryDelayOverrideHours,
            request.UpgradeUntilCutoff,
            request.IsEnabled,
            request.Notes,
            current.CreatedUtc,
            current.UpdatedUtc,
            request.AutomationIntent ?? current.AutomationIntent,
            releasePreferencePlan);
        return MediaPlanSnapshot.From(proposed);
    }

    private static Dictionary<string, string[]> ValidateQualityProfile(
        CreateQualityProfileRequest request,
        IEnumerable<string> tierNames)
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

        AddQualityTierErrors(errors, request.AllowedQualities, request.CutoffQuality, tierNames);

        return errors;
    }

    private static Dictionary<string, string[]> ValidateQualityProfile(
        UpdateQualityProfileRequest request,
        IEnumerable<string> tierNames)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this quality profile a name."];
        }

        AddQualityTierErrors(errors, request.AllowedQualities, request.CutoffQuality, tierNames);

        return errors;
    }

    private static void AddQualityTierErrors(
        Dictionary<string, string[]> errors,
        string? allowedQualities,
        string? cutoffQuality,
        IEnumerable<string> tierNames)
    {
        var knownTiers = tierNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknownAllowed = (allowedQualities ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(name => !knownTiers.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknownAllowed.Length > 0)
        {
            errors["allowedQualities"] =
            [
                $"Unknown quality tier(s): {string.Join(", ", unknownAllowed)}. Choose tiers from the quality model."
            ];
        }

        if (string.IsNullOrWhiteSpace(cutoffQuality))
        {
            errors["cutoffQuality"] = ["Choose the quality Deluno should aim for."];
        }
        else if (!knownTiers.Contains(cutoffQuality.Trim()))
        {
            errors["cutoffQuality"] =
            [
                $"Unknown cutoff quality '{cutoffQuality.Trim()}'. Choose a tier from the quality model."
            ];
        }
    }

    private static CustomFormatExportItem ToCustomFormatExportItem(CustomFormatItem item)
    {
        var preview = CustomFormatDefinitionMatcher.PreviewImportedConditions(item.Conditions, item.MediaType);
        return new CustomFormatExportItem(item.Name, item.MediaType, item.Score, item.UpgradeAllowed, preview.Supported);
    }

    /// <summary>
    /// Imports one format from an import payload item (#777). A condition
    /// this matcher cannot interpret is dropped from what gets saved, but
    /// always reported in <see cref="CustomFormatImportResult.DroppedConditions"/>
    /// (#774) rather than silently lost or failing the whole batch over it.
    /// </summary>
    private static async Task<CustomFormatImportResult> ImportOneCustomFormatAsync(
        JsonElement item,
        IQualityRepository repository,
        IReadOnlyList<CustomFormatItem> existing,
        CancellationToken cancellationToken)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return new CustomFormatImportResult(null, false, null, [], "Each imported item must be a JSON object.");
        }

        var name = ReadImportString(item, "name");
        var mediaType = MediaPolicyCatalog.NormalizeMediaType(ReadImportString(item, "mediaType"));
        var score = ReadImportInt(item, "score") ?? 0;
        var upgradeAllowed = ReadImportBool(item, "upgradeAllowed") ?? true;

        if (string.IsNullOrWhiteSpace(name))
        {
            return new CustomFormatImportResult(name, false, null, [], "Give this custom format a name.");
        }

        var trimmedName = name.Trim();
        if (existing.Any(candidate =>
                string.Equals(candidate.Name.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(MediaPolicyCatalog.NormalizeMediaType(candidate.MediaType), mediaType, StringComparison.OrdinalIgnoreCase)))
        {
            return new CustomFormatImportResult(
                trimmedName,
                false,
                null,
                [],
                $"A {mediaType} custom rule named '{trimmedName}' already exists. Rename it before importing, or edit that rule instead.");
        }

        if (!item.TryGetProperty("conditions", out var conditionsElement)
            || (conditionsElement.ValueKind != JsonValueKind.Array && conditionsElement.ValueKind != JsonValueKind.String))
        {
            return new CustomFormatImportResult(trimmedName, false, null, [], "No conditions were supplied.");
        }

        var rawConditions = conditionsElement.ValueKind == JsonValueKind.String
            ? conditionsElement.GetString()
            : conditionsElement.GetRawText();
        var preview = CustomFormatDefinitionMatcher.PreviewImportedConditions(rawConditions, mediaType);
        if (preview.Supported.Count == 0)
        {
            return new CustomFormatImportResult(
                trimmedName,
                false,
                null,
                preview.Dropped,
                "None of this format's conditions could be understood.");
        }

        var finalConditions = JsonSerializer.Serialize(preview.Supported, ReleasePreferenceJson.Options);
        var created = await repository.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(trimmedName, mediaType, score, TrashId: null, Conditions: finalConditions, upgradeAllowed),
            cancellationToken);

        return new CustomFormatImportResult(created.Name, true, created.Id, preview.Dropped, null);
    }

    private static string? ReadImportString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadImportInt(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? ReadImportBool(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => (bool?)null
            }
            : null;

    private static Dictionary<string, string[]> ValidateCustomFormat(string? name)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this custom format a name."];
        }

        return errors;
    }

    private static void AddDuplicateCustomFormatNameError(
        IDictionary<string, string[]> errors,
        IReadOnlyList<CustomFormatItem> existing,
        string? name,
        string? mediaType,
        string? exceptId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var normalizedMediaType = MediaPolicyCatalog.NormalizeMediaType(mediaType);
        if (existing.Any(item =>
                !string.Equals(item.Id, exceptId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    MediaPolicyCatalog.NormalizeMediaType(item.MediaType),
                    normalizedMediaType,
                    StringComparison.OrdinalIgnoreCase)))
        {
            errors["name"] =
            [
                $"A {normalizedMediaType} custom rule named '{name.Trim()}' already exists. Edit that rule instead of creating a duplicate that would report the same match twice."
            ];
        }
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
}
