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
        guideEndpoints.MapGet("/trash/source-inventory", async (
            IGuidePackageStore store,
            CancellationToken cancellationToken) =>
        {
            var source = (await store.GetCurrentAsync(cancellationToken)).Package.SourceInventory;
            return source is null ? Results.NotFound() : Results.Json(source, ReleasePreferenceJson.Options);
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

        releasePreferences.MapGet("/registry", (string? mediaType) =>
        {
            var normalizedMediaType = PreferenceTraitRegistry.NormalizeMediaType(mediaType);
            var traits = PreferenceTraitRegistry.Current.Traits
                .Where(trait => normalizedMediaType == "both"
                    || trait.NormalizedMediaTypes.Contains("both", StringComparer.Ordinal)
                    || trait.NormalizedMediaTypes.Contains(normalizedMediaType, StringComparer.Ordinal))
                .OrderBy(trait => trait.Dimension, StringComparer.Ordinal)
                .ThenBy(trait => trait.DisplayName, StringComparer.Ordinal)
                .ToArray();
            return Results.Json(new
            {
                version = PreferenceTraitRegistry.Current.Version,
                mediaType = normalizedMediaType,
                traits,
                relationships = PreferenceTraitRegistry.Current.Relationships
            }, ReleasePreferenceJson.Options);
        });

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
        var scenarioWrites = endpoints
            .MapGroup("/api/media-plan-scenarios")
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

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

        scenarioReads.MapPost("/{id}/preview", async (
            string id,
            [FromBody] ApplyMediaPlanScenarioRequest request,
            [FromServices] IQualityRepository repository,
            CancellationToken cancellationToken) =>
        {
            MediaPlanScenarioCompilation compilation;
            try
            {
                compilation = MediaPlanScenarioCompiler.Compile(id, request.MediaType, request.Name, request.IsEnabled);
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

            var matchingPlans = (await repository.ListPolicySetsAsync(cancellationToken))
                .Where(plan => MediaPlanScenarioPlanIdentity.Matches(plan, compilation.ScenarioId, compilation.MediaType))
                .ToArray();
            if (matchingPlans.Length > 1)
            {
                return Results.Conflict(new
                {
                    code = "scenario_plan_ambiguous",
                    message = "More than one Media Plan claims this scenario and media type. Review the duplicate plans before updating either one.",
                    scenarioId = compilation.ScenarioId,
                    mediaType = compilation.MediaType,
                    planIds = matchingPlans.Select(plan => plan.Id).ToArray()
                });
            }

            var current = matchingPlans.SingleOrDefault();
            if (current is null)
            {
                return Results.NotFound(new
                {
                    code = "scenario_plan_not_installed",
                    message = "This scenario has not been applied for this media type yet. Review its compilation before creating a new Media Plan.",
                    scenario = compilation
                });
            }

            var qualityProfile = (await repository.ListQualityProfilesAsync(cancellationToken))
                .FirstOrDefault(profile =>
                    string.Equals(profile.MediaType, compilation.MediaType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(profile.PresetId, compilation.QualityPresetId, StringComparison.OrdinalIgnoreCase));
            if (qualityProfile is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["qualityProfileId"] = ["The scenario needs its matching Quality Profile before an update can be previewed. Create or restore that profile, then review again."]
                });
            }

            var scenarioRequest = ToUpdateRequest(compilation.PolicySet with { QualityProfileId = qualityProfile.Id });
            var preview = await BuildMediaPlanPreviewAsync(current, scenarioRequest, repository, cancellationToken);
            return Results.Ok(new MediaPlanScenarioUpdatePreview(compilation, preview));
        });

        scenarioWrites.MapPost("/{id}/apply", async (
            string id,
            HttpContext httpContext,
            [FromBody] ApplyMediaPlanScenarioRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IPolicySetLibraryApplier libraryApplier,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            MediaPlanScenarioCompilation compilation;
            try
            {
                compilation = MediaPlanScenarioCompiler.Compile(id, request.MediaType, request.Name, request.IsEnabled);
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

            // A scenario deliberately has separate Movie and TV variants. Its
            // structured marker and media type together are the identity, so a
            // TV update can never select and overwrite the Movie plan.
            var matchingPlans = (await repository.ListPolicySetsAsync(cancellationToken))
                .Where(plan => MediaPlanScenarioPlanIdentity.Matches(plan, compilation.ScenarioId, compilation.MediaType))
                .ToArray();
            if (matchingPlans.Length > 1)
            {
                return Results.Conflict(new
                {
                    code = "scenario_plan_ambiguous",
                    message = "More than one Media Plan claims this scenario and media type. Review the duplicate plans before updating either one.",
                    scenarioId = compilation.ScenarioId,
                    mediaType = compilation.MediaType,
                    planIds = matchingPlans.Select(plan => plan.Id).ToArray()
                });
            }

            var existing = matchingPlans.SingleOrDefault();
            var qualityProfile = (await repository.ListQualityProfilesAsync(cancellationToken))
                .FirstOrDefault(profile =>
                    string.Equals(profile.MediaType, compilation.MediaType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(profile.PresetId, compilation.QualityPresetId, StringComparison.OrdinalIgnoreCase));
            var createdQualityProfile = false;

            if (existing is not null)
            {
                var versionMarker = $"Scenario: {compilation.ScenarioId} v{compilation.ScenarioVersion}";
                if (existing.Notes?.Contains(versionMarker, StringComparison.OrdinalIgnoreCase) == true
                    && qualityProfile is not null
                    && string.Equals(existing.QualityProfileId, qualityProfile.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Ok(new
                    {
                        scenario = compilation,
                        qualityProfile,
                        policySet = existing,
                        created = false,
                        updated = false,
                        createdQualityProfile
                    });
                }

                if (qualityProfile is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["qualityProfileId"] = ["The scenario needs its matching Quality Profile before an update can be previewed. Create or restore that profile, then review again."]
                    });
                }

                var scenarioRequest = ToUpdateRequest(compilation.PolicySet with { QualityProfileId = qualityProfile.Id });
                var preview = await BuildMediaPlanPreviewAsync(existing, scenarioRequest, repository, cancellationToken);
                if (!preview.HasChanges)
                {
                    return Results.Ok(new
                    {
                        scenario = compilation,
                        qualityProfile,
                        policySet = existing,
                        created = false,
                        updated = false,
                        createdQualityProfile
                    });
                }

                if (string.IsNullOrWhiteSpace(request.BasePlanHash))
                {
                    return Results.Conflict(new
                    {
                        code = "scenario_update_requires_preview",
                        message = "This scenario would change an existing Media Plan. Review the returned diff, then apply using its basePlanHash.",
                        scenario = compilation,
                        preview
                    });
                }

                if (!string.Equals(request.BasePlanHash.Trim(), preview.BasePlanHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Conflict(new
                    {
                        code = "scenario_preview_stale",
                        message = "This Media Plan changed after the scenario preview. Review a fresh diff before applying it.",
                        scenario = compilation,
                        preview
                    });
                }

                PolicySetItem? updated;
                try
                {
                    updated = await repository.UpdatePolicySetAsync(
                        existing.Id,
                        scenarioRequest,
                        cancellationToken,
                        "scenario-update",
                        preview.BasePlanHash);
                }
                catch (MediaPlanVersionConflictException)
                {
                    return Results.Conflict(new
                    {
                        code = "scenario_preview_stale",
                        message = "This Media Plan changed while the scenario update was being applied. Review a fresh diff before trying again."
                    });
                }
                if (updated is null)
                {
                    return Results.NotFound();
                }

                await libraryApplier.ApplyToAssignedLibrariesAsync(updated.Id, cancellationToken);
                await realtimeEventPublisher.PublishEntityChangedAsync("PolicySet", updated.Id, cancellationToken);
                return Results.Ok(new
                {
                    scenario = compilation,
                    qualityProfile,
                    policySet = updated,
                    created = false,
                    updated = true,
                    createdQualityProfile
                });
            }

            if (qualityProfile is null)
            {
                qualityProfile = await repository.CreateQualityProfileFromPresetAsync(
                    compilation.QualityPresetId,
                    null,
                    cancellationToken);
                createdQualityProfile = true;
            }

            var policySet = await repository.CreatePolicySetAsync(
                compilation.PolicySet with { QualityProfileId = qualityProfile.Id },
                cancellationToken);
            await libraryApplier.ApplyToAssignedLibrariesAsync(policySet.Id, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("PolicySet", policySet.Id, cancellationToken);
            return Results.Ok(new
            {
                scenario = compilation,
                qualityProfile,
                policySet,
                created = true,
                updated = false,
                createdQualityProfile
            });
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

            var item = await repository.UpdateQualityProfileAsync(id, request, cancellationToken);
            if (item is not null)
            {
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

        qualityProfiles.MapPut("order", async (
            HttpContext httpContext,
            [FromBody] ReorderQualityProfilesRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
            foreach (var id in request.Ids)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("QualityProfile", id, cancellationToken);
            }
            return Results.NoContent();
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

        var qualityPresets = endpoints.MapGroup("/api/quality-profile-presets");

        qualityPresets.MapGet(string.Empty, () =>
        {
            var items = QualityProfilePresetCatalog.All.Select(p => new QualityProfilePresetItem(
                p.Id, p.Name, p.Description, p.MediaType, p.CutoffQuality,
                p.AllowedQualities, p.UpgradeUntilCutoff, p.UpgradeUnknownItems, p.Version));
            return Results.Ok(items);
        });

        qualityPresets.MapPost("{presetId}/apply", async (
            string presetId,
            [FromBody] ApplyQualityPresetRequest request,
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

            if (QualityProfilePresetCatalog.FindById(presetId) is null)
            {
                return Results.NotFound();
            }

            var item = await repository.CreateQualityProfileFromPresetAsync(presetId, request.Name, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("QualityProfile", item.Id, cancellationToken);
            return Results.Ok(item);
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
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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

        policySets.MapGet("{id}/diff", async (
            string id,
            int? fromVersion,
            int? toVersion,
            [FromServices] IQualityRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (fromVersion is <= 0 || toVersion is <= 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["version"] = ["Plan versions must be positive integers."]
                });
            }

            var from = fromVersion.HasValue
                ? await repository.GetMediaPlanVersionAsync(id, fromVersion.Value, cancellationToken)
                : await repository.GetLatestMediaPlanVersionAsync(id, cancellationToken);
            var to = toVersion.HasValue
                ? await repository.GetMediaPlanVersionAsync(id, toVersion.Value, cancellationToken)
                : await repository.GetLatestMediaPlanVersionAsync(id, cancellationToken);
            if (from is null || to is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                planId = id,
                fromVersion = from.Version,
                toVersion = to.Version,
                changes = MediaPlanVersionCodec.Diff(from.Snapshot, to.Snapshot),
                hasChanges = !string.Equals(from.PlanHash, to.PlanHash, StringComparison.OrdinalIgnoreCase),
                fromPlanHash = from.PlanHash,
                toPlanHash = to.PlanHash
            });
        });

        policySets.MapPost("{id}/preview", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdatePolicySetRequest request,
            [FromServices] IQualityRepository repository,
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
                    var proposedProfile = (await repository.ListQualityProfilesAsync(cancellationToken))
                        .FirstOrDefault(profile => string.Equals(profile.Id, requestedQualityProfileId, StringComparison.OrdinalIgnoreCase));
                    if (proposedProfile is null)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["qualityProfileId"] = ["Choose an existing quality profile before previewing this Media Plan."]
                        });
                    }

                    if (!string.Equals(proposedProfile.MediaType, request.MediaType ?? current.MediaType, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["qualityProfileId"] = ["The selected quality profile must use the same media type as the Media Plan."]
                        });
                    }

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

        policySets.MapPost("{id}/effective-preview", async (
            string id,
            HttpContext httpContext,
            [FromBody] MediaPlanEffectivePreviewRequest request,
            [FromServices] IQualityRepository repository,
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

            var resolution = MediaPlanInheritanceResolver.Resolve(
                MediaPlanSnapshot.From(current),
                request.LibraryOverride,
                request.TitleOverride,
                new MediaPlanGlobalSafety(request.GlobalAutomationEnabled),
                request.LibraryId,
                request.TitleId);
            return Results.Ok(resolution);
        });

        policySets.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreatePolicySetRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IPolicySetLibraryApplier libraryApplier,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
            await libraryApplier.ApplyToAssignedLibrariesAsync(item.Id, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("PolicySet", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        policySets.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdatePolicySetRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IPolicySetLibraryApplier libraryApplier,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
            if (item is not null)
            {
                await libraryApplier.ApplyToAssignedLibrariesAsync(item.Id, cancellationToken);
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
            CancellationToken cancellationToken) =>
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

    private static UpdatePolicySetRequest ToUpdateRequest(CreatePolicySetRequest request)
        => new(
            request.Name,
            request.MediaType,
            request.QualityProfileId,
            request.DestinationRuleId,
            request.CustomFormatIds,
            request.SearchIntervalOverrideHours,
            request.RetryDelayOverrideHours,
            request.UpgradeUntilCutoff,
            request.IsEnabled,
            request.Notes,
            request.AutomationIntent);

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

    private static async Task<MediaPlanPreview> BuildMediaPlanPreviewAsync(
        PolicySetItem current,
        UpdatePolicySetRequest request,
        IQualityRepository repository,
        CancellationToken cancellationToken)
    {
        var currentSnapshot = MediaPlanSnapshot.From(current);
        var requestedQualityProfileId = string.IsNullOrWhiteSpace(request.QualityProfileId)
            ? null
            : request.QualityProfileId.Trim();
        var proposedReleasePreferencePlan = current.ReleasePreferencePlan;
        if (!string.Equals(requestedQualityProfileId, current.QualityProfileId, StringComparison.OrdinalIgnoreCase))
        {
            proposedReleasePreferencePlan = requestedQualityProfileId is null
                ? null
                : (await repository.ListQualityProfilesAsync(cancellationToken))
                    .FirstOrDefault(profile => string.Equals(profile.Id, requestedQualityProfileId, StringComparison.OrdinalIgnoreCase))
                    ?.ReleasePreferencePlan;
        }

        var proposedSnapshot = BuildProposedMediaPlanSnapshot(
            current,
            request,
            proposedReleasePreferencePlan);
        var changes = MediaPlanVersionCodec.Diff(currentSnapshot, proposedSnapshot);
        var latest = await repository.GetLatestMediaPlanVersionAsync(current.Id, cancellationToken);
        return new MediaPlanPreview(
            current.Id,
            latest?.Version,
            currentSnapshot,
            proposedSnapshot,
            changes,
            changes.Count > 0,
            latest?.PlanHash ?? MediaPlanVersionCodec.ComputeHash(currentSnapshot));
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

    private static Dictionary<string, string[]> ValidateCustomFormat(string? name)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this custom format a name."];
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
}
