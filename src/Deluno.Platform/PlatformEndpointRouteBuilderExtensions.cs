using Microsoft.AspNetCore.Mvc;
using Deluno.Contracts;
using Deluno.Quality.Presets;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Resilience;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migration;
using Deluno.Platform.Processing;
using Deluno.Quality;
using Deluno.Notifications;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Security.Contracts;
using Deluno.Security;
using System.Net.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Quality.Contracts;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Platform;

public static class PlatformEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var write = endpoints.MapGroup(string.Empty)
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);
        var settings = write.MapGroup("/api/settings");

        var recycleBin = write.MapGroup("/api/recycle-bin");
        recycleBin.MapGet(string.Empty, async (
            IRecycleBinService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        recycleBin.MapGet("/settings", async (
            IRecycleBinService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetSettingsAsync(cancellationToken)));

        recycleBin.MapPut("/settings", async (
            [FromBody] RecycleBinSettings request,
            IRecycleBinService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.SaveSettingsAsync(request, cancellationToken)));

        // Says what an empty would take, before it takes it. Decision 15:
        // permanent deletion that only tells you afterwards is not a choice,
        // it is a report.
        recycleBin.MapGet("/cleanup/preview", async (
            IRecycleBinService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.PreviewCleanupAsync(cancellationToken)));

        recycleBin.MapPost("/cleanup", async (
            IRecycleBinService service,
            CancellationToken cancellationToken) =>
            Results.Ok(new { removed = await service.CleanupAsync(cancellationToken) }));

        recycleBin.MapPost("/{id}/restore", async (
            string id,
            IRecycleBinService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.RestoreAsync(id, cancellationToken);
            return result.Success
                ? Results.Ok(result)
                : Results.Conflict(result);
        });

        recycleBin.MapDelete("/{id}", async (
            string id,
            IRecycleBinService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.PermanentlyDeleteAsync(id, cancellationToken);
            return result.Success
                ? Results.Ok(result)
                : Results.NotFound(result);
        });

        settings.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var snapshot = await repository.GetAsync(cancellationToken);
            return Results.Ok(snapshot);
        });

        settings.MapPut(string.Empty, async (
            HttpContext httpContext,
            [FromBody] UpdatePlatformSettingsRequest request,
            IPlatformSettingsRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            IHealthCheckRepository healthChecks,
            IEnumerable<IMetadataServiceReset> metadataReset,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateSettings(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var before = await repository.GetAsync(cancellationToken);
            var snapshot = await repository.SaveAsync(request, cancellationToken);
            // A corrected URL or key must not wait out a pause earned by the old one (#1090).
            if (!string.Equals(before.MetadataProviderMode, request.MetadataProviderMode, StringComparison.Ordinal)
                || !string.Equals(before.MetadataBrokerUrl, request.MetadataBrokerUrl, StringComparison.Ordinal)
                || !string.IsNullOrWhiteSpace(request.MetadataTmdbApiKey)
                || !string.IsNullOrWhiteSpace(request.MetadataOmdbApiKey))
            {
                foreach (var reset in metadataReset)
                {
                    await reset.ResetAsync(cancellationToken);
                }
            }
            await realtimeEventPublisher.PublishEntityChangedAsync("Settings", "settings", cancellationToken);
            // #909: the library free-space thresholds live on this settings
            // record, so any save might have changed them. Asking the library
            // checks to run now costs one extra fast pass; not asking risks
            // showing a stale pause/warn state for up to fifteen minutes.
            await healthChecks.RequestImmediateRunAsync(HealthCheckCategories.Library, cancellationToken);
            // The metadata provider mode, broker URL and keys live here too,
            // so a save that fixes (or breaks) TMDb should not wait fifteen
            // minutes to be believed either.
            await healthChecks.RequestImmediateRunAsync(HealthCheckCategories.Metadata, cancellationToken);
            return Results.Ok(snapshot);
        });

        settings.MapPatch(string.Empty, async (
            HttpContext httpContext,
            [FromBody] PatchPlatformSettingsRequest request,
            IPlatformSettingsRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            IHealthCheckRepository healthChecks,
            IEnumerable<IMetadataServiceReset> metadataReset,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var current = await repository.GetAsync(cancellationToken);
            var merged = PlatformSettingsPatchMerger.Apply(current, request);
            var errors = ValidateSettings(merged);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var snapshot = await repository.SaveAsync(merged, cancellationToken);
            if (request.MetadataProviderMode is not null || request.MetadataBrokerUrl is not null
                || request.MetadataTmdbApiKey is not null || request.MetadataOmdbApiKey is not null)
            {
                foreach (var reset in metadataReset)
                {
                    await reset.ResetAsync(cancellationToken);
                }
            }
            await realtimeEventPublisher.PublishEntityChangedAsync("Settings", "settings", cancellationToken);
            await healthChecks.RequestImmediateRunAsync(HealthCheckCategories.Library, cancellationToken);
            await healthChecks.RequestImmediateRunAsync(HealthCheckCategories.Metadata, cancellationToken);
            return Results.Ok(snapshot);
        });

        settings.MapPut("/automation", async (
            HttpContext httpContext,
            [FromBody] UpdateGlobalAutomationRequest request,
            IPlatformSettingsRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var snapshot = await repository.SetGlobalAutomationEnabledAsync(request.IsEnabled, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Settings", "settings", cancellationToken);
            return Results.Ok(snapshot);
        });

        var tags = write.MapGroup("/api/tags");

        var setup = write.MapGroup("/api/setup");

        setup.MapGet("/progress", async (
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await repository.GetSetupProgressAsync(cancellationToken));
        });

        setup.MapPut("/progress", async (
            HttpContext httpContext,
            [FromBody] UpdateSetupProgressRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await repository.SaveSetupProgressAsync(request, cancellationToken));
        });

        setup.MapGet("/draft", async (
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await repository.GetSetupDraftAsync(cancellationToken));
        });

        setup.MapPut("/draft", async (
            HttpContext httpContext,
            [FromBody] UpdateSetupDraftRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await repository.SaveSetupDraftAsync(request, cancellationToken));
        });

        setup.MapDelete("/draft", async (
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            await repository.ClearSetupDraftAsync(cancellationToken);
            return Results.NoContent();
        });

        setup.MapPost("/completed", async (
            HttpContext httpContext,
            [FromBody] SetupCompletedRequest request,
            IPlatformSettingsRepository repository,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var details = JsonSerializer.Serialize(new
            {
                libraries = request.Libraries ?? [],
                qualityProfiles = request.QualityProfiles ?? [],
                customFormatCount = request.CustomFormatCount,
                indexerName = request.IndexerName,
                clientName = request.ClientName,
                firstTitle = request.FirstTitle
            });

            var activity = await activityFeedRepository.RecordActivityAsync(
                "system",
                "Guided setup completed.",
                details,
                null,
                "setup",
                "guided",
                cancellationToken);

            await repository.SaveSetupProgressAsync(
                new UpdateSetupProgressRequest(4, IsCompleted: true),
                cancellationToken);

            return Results.Ok(activity);
        });

        tags.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListTagsAsync(cancellationToken);
            return Results.Ok(items);
        });

        tags.MapGet("/usage", async (
            IPlatformSettingsRepository repository,
            IMediaTagStore tagStore,
            ITagReferenceStore tagReferences,
            CancellationToken cancellationToken) =>
        {
            var definitions = await repository.ListTagsAsync(cancellationToken);
            var users = await tagReferences.ListAllUsersAsync(cancellationToken);
            var usage = new Dictionary<string, (int Movies, int Series)>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in await tagStore.ListUsageAsync(MediaKind.Movie, cancellationToken))
            {
                usage[item.Name] = usage.TryGetValue(item.Name, out var current)
                    ? (current.Movies + item.TitleCount, current.Series)
                    : (item.TitleCount, 0);
            }

            foreach (var item in await tagStore.ListUsageAsync(MediaKind.Series, cancellationToken))
            {
                usage[item.Name] = usage.TryGetValue(item.Name, out var current)
                    ? (current.Movies, current.Series + item.TitleCount)
                    : (0, item.TitleCount);
            }

            return Results.Ok(definitions.Select(tag =>
            {
                usage.TryGetValue(tag.Name, out var counts);
                users.TryGetValue(tag.Name.Trim().ToLowerInvariant(), out var tagUsers);
                return new TagUsageItem(tag.Id, tag.Name, counts.Movies, counts.Series, tagUsers ?? []);
            }));
        });

        // Unused tags are removed only when the user asks, in one go (#1039).
        tags.MapPost("/delete-unused", async (
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            IMediaTagStore tagStore,
            ITagReferenceStore tagReferences,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var definitions = await repository.ListTagsAsync(cancellationToken);
            var users = await tagReferences.ListAllUsersAsync(cancellationToken);
            var titleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kind in new[] { MediaKind.Movie, MediaKind.Series })
            {
                foreach (var item in await tagStore.ListUsageAsync(kind, cancellationToken))
                {
                    if (item.TitleCount > 0)
                    {
                        titleNames.Add(item.Name.Trim());
                    }
                }
            }

            var removed = new List<string>();
            foreach (var tag in definitions)
            {
                if (titleNames.Contains(tag.Name.Trim()) || users.ContainsKey(tag.Name.Trim().ToLowerInvariant()))
                {
                    continue;
                }

                if (await tagReferences.DeleteAsync(tag.Id, tag.Name, cancellationToken))
                {
                    removed.Add(tag.Name);
                }
            }

            if (removed.Count > 0)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "tags.unused.deleted",
                    $"Deleted {removed.Count} unused tag{(removed.Count == 1 ? string.Empty : "s")}: {string.Join(", ", removed)}.",
                    null,
                    null,
                    "tag",
                    null,
                    cancellationToken);
            }

            return Results.Ok(new { removed = removed.Count, names = removed });
        });

        // #1044: add, remove or replace tags on several indexers, download clients
        // or notification connections at once, with the title editor's modes.
        tags.MapPost("/bulk-connections", async (
            HttpContext httpContext,
            [FromBody] BulkConnectionTagsRequest request,
            ITagReferenceStore tagReferences,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var (target, noun) = (request.Kind ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "indexer" or "indexers" => (BulkTagTarget.Indexer as BulkTagTarget?, "indexer"),
                "downloadclient" or "downloadclients" => (BulkTagTarget.DownloadClient, "download client"),
                "notification" or "notifications" => (BulkTagTarget.Notification, "notification connection"),
                _ => (null, string.Empty)
            };
            if (target is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["kind"] = ["Kind must be indexer, downloadClient or notification."]
                });
            }

            if (request.Ids is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ids"] = ["Choose at least one connection before applying tags."]
                });
            }

            if (!MediaTagBulkOperations.TryParseMode(request.Mode, out var mode))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["mode"] = ["Mode must be add, remove or replace."]
                });
            }

            var names = (request.Tags ?? [])
                .SelectMany(tag => (tag ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (mode != BulkTagMode.Replace && names.Length == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["tags"] = ["Pick at least one tag."]
                });
            }

            var changed = await tagReferences.BulkApplyAsync(target.Value, request.Ids, mode, names, cancellationToken);
            if (changed.Count > 0)
            {
                var plural = changed.Count == 1 ? noun : noun + "s";
                var list = names.Length == 0 ? "no tags" : string.Join(", ", names);
                var message = mode switch
                {
                    BulkTagMode.Add => $"Added the tags {list} to {changed.Count} {plural}: {string.Join(", ", changed)}.",
                    BulkTagMode.Remove => $"Removed the tags {list} from {changed.Count} {plural}: {string.Join(", ", changed)}.",
                    _ => names.Length == 0
                        ? $"Cleared every tag from {changed.Count} {plural}: {string.Join(", ", changed)}."
                        : $"Replaced the tags on {changed.Count} {plural} with {list}: {string.Join(", ", changed)}."
                };
                await activityFeedRepository.RecordActivityAsync(
                    "tags.bulk.connections",
                    message,
                    null,
                    null,
                    "tag",
                    null,
                    cancellationToken);
            }

            return Results.Ok(new { updated = changed.Count, tags = names, mode = mode.ToString().ToLowerInvariant() });
        });

        tags.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateTagRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
            [FromBody] UpdateTagRequest request,
            IPlatformSettingsRepository repository,
            IMediaTagStore tagStore,
            ITagReferenceStore tagReferences,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateTag(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var all = await repository.ListTagsAsync(cancellationToken);
            var previous = all.FirstOrDefault(tag => string.Equals(tag.Id, id, StringComparison.Ordinal));
            if (previous is null)
            {
                return Results.NotFound();
            }

            var nextName = request.Name?.Trim();
            if (!string.IsNullOrEmpty(nextName)
                && !string.Equals(previous.Name, nextName, StringComparison.Ordinal))
            {
                if (all.Any(tag => tag.Id != id && string.Equals(tag.Name.Trim(), nextName, StringComparison.OrdinalIgnoreCase)))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["name"] = ["Another tag already has that name."]
                    });
                }

                // Platform-database uses and the definition change together in one
                // transaction; the movie and series catalogues follow straight after
                // (they live in other databases).
                await tagReferences.RenameAsync(id, previous.Name, nextName, cancellationToken);
                await tagStore.RenameAsync(MediaKind.Movie, id, previous.Name, nextName, cancellationToken);
                await tagStore.RenameAsync(MediaKind.Series, id, previous.Name, nextName, cancellationToken);
                await activityFeedRepository.RecordActivityAsync(
                    "tags.renamed",
                    $"Renamed the tag \"{previous.Name}\" to \"{nextName}\" everywhere it was used.",
                    null,
                    null,
                    "tag",
                    id,
                    cancellationToken);
            }

            var item = await repository.UpdateTagAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        // #1024: auto-tagging rules. Nothing exists on a new install; the person adds rules here.
        var autoTagRules = write.MapGroup("/api/auto-tag-rules");

        autoTagRules.MapGet(string.Empty, async (IAutoTagRuleStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAsync(cancellationToken)));

        autoTagRules.MapPost(string.Empty, async (
            HttpContext httpContext,
            SaveAutoTagRuleRequest request,
            IAutoTagRuleStore store,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = AutoTagRuleValidator.Validate(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var created = await store.CreateAsync(request, cancellationToken);
            return Results.Created($"/api/auto-tag-rules/{created.Id}", created);
        });

        autoTagRules.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            SaveAutoTagRuleRequest request,
            IAutoTagRuleStore store,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = AutoTagRuleValidator.Validate(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var updated = await store.UpdateAsync(id, request, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        autoTagRules.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IAutoTagRuleStore store,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            return await store.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();
        });

        // Runs every enabled rule over the titles already in the library, so a new rule
        // does not have to wait for each title's next refresh. Only when the person asks.
        autoTagRules.MapPost("/run", async (
            HttpContext httpContext,
            IAutoTaggingService autoTagging,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var changed = await autoTagging.ApplyToAllAsync(null, cancellationToken);
            await activityFeedRepository.RecordActivityAsync(
                "tags.auto",
                changed == 0
                    ? "Auto-tagging checked every title against the rules and had nothing to change."
                    : $"Auto-tagging checked every title against the rules and changed the tags on {changed} {(changed == 1 ? "title" : "titles")}.",
                null,
                null,
                "tag",
                null,
                cancellationToken);
            return Results.Ok(new { changedTitles = changed });
        });

        tags.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            IMediaTagStore tagStore,
            ITagReferenceStore tagReferences,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var tag = (await repository.ListTagsAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (tag is null)
            {
                return Results.NotFound();
            }

            // Like Sonarr and Radarr, a tag in use is not deleted (#1039): the
            // owner removes it from its users first.
            var inUse = await DescribeTagUseAsync(tag.Name, tagStore, tagReferences, cancellationToken);
            if (inUse is not null)
            {
                return Results.Conflict(new { message = $"The tag \"{tag.Name}\" is still in use by {inUse}. Remove it from those first." });
            }

            var removed = await tagReferences.DeleteAsync(id, tag.Name, cancellationToken);
            if (!removed)
            {
                return Results.NotFound();
            }

            await activityFeedRepository.RecordActivityAsync(
                "tags.deleted",
                $"Deleted the unused tag \"{tag.Name}\".",
                null,
                null,
                "tag",
                id,
                cancellationToken);
            return Results.NoContent();
        });

        // #771: lives here rather than in Deluno.Quality's own endpoint group
        // because it needs to write to Activity, and Deluno.Quality must not
        // reference Deluno.Jobs (that reference used to be circular; see the
        // fix in commit 2f3507de). Deluno.Platform already depends on both.
        write.MapPost("/api/quality-model/reset", async (
            HttpContext httpContext,
            IQualityModelService qualityModelService,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var model = await qualityModelService.ResetToDefaultsAsync(cancellationToken);
            await activityFeedRepository.RecordActivityAsync(
                "quality.model.reset",
                "Reset the Quality Definitions table (Min, Preferred and Max sizes) to Deluno's shipped defaults.",
                null,
                null,
                "qualityModel",
                null,
                cancellationToken);
            return Results.Ok(model);
        });

        // The seeding-rule matrix (#820): category/tracker/tag exceptions to
        // the global sharing rule, evaluated in priority order.
        var sharingRules = write.MapGroup("/api/sharing-rules");

        sharingRules.MapGet(string.Empty, async (ISharingRulesRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListAsync(cancellationToken)));

        sharingRules.MapPost(string.Empty, async (
            CreateSharingRuleRequest request,
            ISharingRulesRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateSharingRule(request.CategoryPattern, request.TrackerPattern, request.TagsCsv, request.MinSeeders, request.MaxInactiveDays);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            return Results.Ok(await repository.CreateAsync(request, cancellationToken));
        });

        sharingRules.MapPut("/{id}", async (
            string id,
            UpdateSharingRuleRequest request,
            ISharingRulesRepository repository,
            CancellationToken cancellationToken) =>
        {
            // Unlike create, null here can mean "leave unchanged" rather than
            // "not set" — the same field carries two meanings depending on
            // context, so only the two guards (which are never ambiguous)
            // are checked before an update reaches the repository.
            var errors = ValidateSharingRuleGuards(request.MinSeeders, request.MaxInactiveDays);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var updated = await repository.UpdateAsync(id, request, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        sharingRules.MapDelete("/{id}", async (
            string id,
            ISharingRulesRepository repository,
            CancellationToken cancellationToken) =>
            await repository.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        return endpoints;
    }

    private static Dictionary<string, string[]> ValidateSharingRule(
        string? categoryPattern,
        string? trackerPattern,
        string? tagsCsv,
        int? minSeeders,
        int? maxInactiveDays)
    {
        var errors = ValidateSharingRuleGuards(minSeeders, maxInactiveDays);

        if (string.IsNullOrWhiteSpace(categoryPattern) && string.IsNullOrWhiteSpace(trackerPattern) && string.IsNullOrWhiteSpace(tagsCsv))
        {
            errors["categoryPattern"] = ["Match this rule against a category, a tracker, or a tag — a rule that matches nothing would never apply."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateSharingRuleGuards(int? minSeeders, int? maxInactiveDays)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (minSeeders is < 0)
        {
            errors["minSeeders"] = ["Minimum seeders cannot be negative."];
        }

        if (maxInactiveDays is < 0)
        {
            errors["maxInactiveDays"] = ["Maximum inactive days cannot be negative."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateSettings(UpdatePlatformSettingsRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (request.HostPort <= 0)
        {
            errors["hostPort"] = ["Choose a valid port number."];
        }

        if (!string.IsNullOrWhiteSpace(request.SearchScoringMode) &&
            !SearchScoringModes.IsSupported(request.SearchScoringMode))
        {
            errors["searchScoringMode"] = ["Choose one of: hybrid, rules-only, or ml-only."];
        }

        // #909: the repository clamps both into range and quietly pulls pause
        // down to meet warn on save, which is the right last resort against a
        // corrupt value -- but silently rewriting what a person just typed is
        // not a substitute for telling them why, on the form, before it saves.
        if (request.LibraryFreeSpaceWarnPercent is { } warnPercent && (warnPercent < 1 || warnPercent > 50))
        {
            errors["libraryFreeSpaceWarnPercent"] = ["Choose a percentage between 1 and 50."];
        }

        if (!string.IsNullOrWhiteSpace(request.ProperRepackAcquisitionMode) &&
            request.ProperRepackAcquisitionMode.Trim().ToLowerInvariant() is not ("prefer-and-upgrade" or "do-not-upgrade-automatically" or "do-not-prefer"))
        {
            errors["properRepackAcquisitionMode"] = ["Choose one of: prefer-and-upgrade, do-not-upgrade-automatically, or do-not-prefer."];
        }

        if (!string.IsNullOrWhiteSpace(request.FolderChmod) &&
            (request.FolderChmod.Trim().Length is not (3 or 4) || !request.FolderChmod.Trim().All(digit => digit is >= '0' and <= '7')))
        {
            errors["folderChmod"] = ["Enter a 3- or 4-digit octal mode, such as 755 or 0755."];
        }

        if (request.LibraryFreeSpacePausePercent is { } pausePercent && (pausePercent < 1 || pausePercent > 50))
        {
            errors["libraryFreeSpacePausePercent"] = ["Choose a percentage between 1 and 50."];
        }

        if (!string.IsNullOrWhiteSpace(request.CloudflareHelperUrl) &&
            (!Uri.TryCreate(request.CloudflareHelperUrl.Trim(), UriKind.Absolute, out var helperAddress) || helperAddress.Scheme is not ("http" or "https")))
        {
            errors["cloudflareHelperUrl"] = ["Enter the helper's web address starting with http:// or https://, such as http://127.0.0.1:8191."];
        }

        if (request.CloudflareHelperTimeoutSeconds is { } helperTimeout &&(helperTimeout < 1 || helperTimeout > 180))
        {
            errors["cloudflareHelperTimeoutSeconds"] = ["Choose a timeout between 1 and 180 seconds."];
        }

        if (!errors.ContainsKey("libraryFreeSpaceWarnPercent") &&
            !errors.ContainsKey("libraryFreeSpacePausePercent") &&
            request.LibraryFreeSpacePausePercent is { } pauseValue &&
            pauseValue > (request.LibraryFreeSpaceWarnPercent ?? 10))
        {
            errors["libraryFreeSpacePausePercent"] = ["The pause threshold must be at or below the warn threshold."];
        }

        return errors;
    }

    /// <summary>
    /// Plain-words list of what still uses a tag ("3 titles and 1 download
    /// client"), or null when nothing does.
    /// </summary>
    private static async Task<string?> DescribeTagUseAsync(
        string tagName,
        IMediaTagStore tagStore,
        ITagReferenceStore tagReferences,
        CancellationToken cancellationToken)
    {
        var titles = 0;
        foreach (var kind in new[] { MediaKind.Movie, MediaKind.Series })
        {
            titles += (await tagStore.ListUsageAsync(kind, cancellationToken))
                .Where(item => string.Equals(item.Name.Trim(), tagName.Trim(), StringComparison.OrdinalIgnoreCase))
                .Sum(item => item.TitleCount);
        }

        var parts = new List<string>();
        if (titles > 0)
        {
            parts.Add($"{titles} {(titles == 1 ? "title" : "titles")}");
        }

        var users = (await tagReferences.ListAllUsersAsync(cancellationToken))
            .GetValueOrDefault(tagName.Trim().ToLowerInvariant()) ?? [];
        foreach (var group in users.GroupBy(user => user.Kind))
        {
            var (one, many) = group.Key switch
            {
                "indexer" => ("indexer", "indexers"),
                "downloadClient" => ("download client", "download clients"),
                "notification" => ("notification", "notifications"),
                "sharingRule" => ("seeding rule", "seeding rules"),
                "librarySource" => ("library source rule", "library source rules"),
                "acquisitionRule" => ("release rule", "release rules"),
                "destinationRule" => ("destination rule", "destination rules"),
                _ => ("setting", "settings")
            };
            var count = group.Count();
            parts.Add($"{count} {(count == 1 ? one : many)}");
        }

        return parts.Count == 0
            ? null
            : parts.Count == 1
                ? parts[0]
                : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
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

}

public sealed record SetupCompletedRequest(
    IReadOnlyList<string>? Libraries,
    IReadOnlyList<string>? QualityProfiles,
    int CustomFormatCount,
    string? IndexerName,
    string? ClientName,
    string? FirstTitle);

