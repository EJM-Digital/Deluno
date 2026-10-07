using Deluno.Contracts;
using System.Text.Json;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Deluno.Libraries.Data;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Quality.Data;
using Deluno.Quality.Guides;

namespace Deluno.Media;

public sealed record MediaReleaseGrabRequest(
    string? ReleaseName,
    string? IndexerId,
    string? IndexerName,
    string? DownloadUrl,
    string? CandidateQuality,
    long? SizeBytes,
    int? Seeders,
    bool? Force,
    string? OverrideReason);

public delegate Task RecordMediaSearchAttemptAsync(
    string mediaId,
    string libraryId,
    string triggerKind,
    string outcome,
    DateTimeOffset now,
    DateTimeOffset? nextEligibleSearchUtc,
    string? lastSearchResult,
    string? releaseName,
    string? indexerName,
    string? detailsJson,
    CancellationToken cancellationToken);

public sealed record MediaGrabResult(
    bool NotFound,
    IReadOnlyDictionary<string, string[]>? ValidationErrors,
    string? ReleaseName,
    string? IndexerName,
    bool ForceOverride,
    string? OverrideReason,
    string? DispatchStatus,
    string? DispatchMessage)
{
    public static MediaGrabResult NotFoundResult()
        => new(true, null, null, null, false, null, null, null);

    public static MediaGrabResult ValidationResult(IReadOnlyDictionary<string, string[]> errors)
        => new(false, errors, null, null, false, null, null, null);
}

/// <summary>
/// Shared workflow for manually selecting a release and dispatching it.
/// Movie and Series only provide their media-specific search-attempt writer;
/// all validation, decision, dispatch, audit, and activity behavior lives here.
/// </summary>
public static class MediaGrabHandler
{
    public static async Task<MediaGrabResult> ExecuteAsync(
        MediaKind kind,
        string id,
        MediaReleaseGrabRequest request,
        IMediaStateRepository mediaStateRepository,
        IPlatformSettingsRepository platformSettingsRepository,
        ILibrariesRepository librariesRepository,
        IQualityRepository qualityRepository,
        IJobQueueRepository jobQueueRepository,
        IAcquisitionDecisionPipeline acquisitionPipeline,
        IDownloadClientGrabService downloadClientGrabService,
        IActivityFeedRepository activityFeedRepository,
        TimeProvider timeProvider,
        RecordMediaSearchAttemptAsync recordSearchAttemptAsync,
        CancellationToken cancellationToken,
        IGuidePackageStore? guidePackageStore = null,
        IReleasePreferencePlanRepository? releasePreferencePlanRepository = null,
        string? libraryId = null,
        IMediaTagStore? mediaTagStore = null)
    {
        var item = await mediaStateRepository.GetByIdAsync(kind, id, cancellationToken);
        if (item is null)
        {
            return MediaGrabResult.NotFoundResult();
        }

        var validation = ValidateReleaseGrab(request);
        if (validation.Count > 0)
        {
            return MediaGrabResult.ValidationResult(validation);
        }

        var entityType = kind == MediaKind.Movie ? "movie" : "series";
        var mediaType = kind == MediaKind.Movie ? "movies" : "tv";
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var rows = (await mediaStateRepository.ListWantedByIdsAsync(kind, [id], cancellationToken))
            .Where(candidate => candidate.Id == id)
            .ToArray();
        var (wantedItem, choiceProblem) = MediaLibraryChoice.Pick(rows, libraryId, libraries, entityType);
        if (wantedItem is null)
        {
            return MediaGrabResult.ValidationResult(new Dictionary<string, string[]>
            {
                ["libraryId"] = [choiceProblem!]
            });
        }

        var library = libraries.FirstOrDefault(candidate => candidate.Id == wantedItem.LibraryId);
        if (library is null)
        {
            return MediaGrabResult.ValidationResult(new Dictionary<string, string[]>
            {
                ["libraryId"] = [$"Deluno could not find the linked library for this {entityType}."]
            });
        }

        library = await TitleQualityProfile.ApplyAsync(mediaStateRepository, qualityRepository, kind, id, library, cancellationToken);

        var routing = await librariesRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
        if (routing is null || routing.DownloadClients.Count == 0)
        {
            return MediaGrabResult.ValidationResult(new Dictionary<string, string[]>
            {
                ["downloadClient"] = ["Link a download client to this library before grabbing a release."]
            });
        }

        // The first routed client used to be taken whatever it was, so a torrent
        // picked here could be handed to a Usenet client, which accepts the
        // address and then cannot read what is at it.
        var titleTags = mediaTagStore is null
            ? null
            : (await mediaTagStore.ListAsync(kind, id, cancellationToken)).Select(assignment => assignment.Name).ToArray();
        var downloadClient = await acquisitionPipeline.ChooseDownloadClientAsync(request.IndexerId, routing.DownloadClients, cancellationToken, titleTags);
        if (downloadClient is null)
        {
            return MediaGrabResult.ValidationResult(new Dictionary<string, string[]>
            {
                ["downloadClient"] = [$"{library.Name} has no download client that can take a release from {request.IndexerName ?? "this source"}. Link a matching torrent or Usenet client in Library Routing."]
            });
        }

        var platformSettings = await platformSettingsRepository.GetAsync(cancellationToken);
        var customFormats = await ResolveCustomFormatsAsync(
            qualityRepository,
            library.QualityProfileId,
            cancellationToken);
        var preferencePlan = await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
            qualityRepository,
            releasePreferencePlanRepository,
            library.QualityProfileId,
            cancellationToken,
            customFormats,
            platformSettings.ProperRepackAcquisitionMode);
        var currentPreferenceEvaluation = wantedItem.HasFile
            ? await mediaStateRepository.GetLatestPreferenceEvaluationSnapshotAsync(
                kind,
                item.Id,
                library.Id,
                 fileIdentity: null,
                 cancellationToken,
                 filePath: wantedItem.FilePath,
                 fileSizeBytes: wantedItem.FileSizeBytes)
            : null;
        // The per-library routing override, or nothing. It must not be a literal:
        // passing "movies" here looked like a category and behaved like one, so
        // DownloadClientHelpers.ResolveCategory never reached its fallback and
        // the Movies/TV categories configured on the client were dead settings.
        // Every grab landed in a category named "movies", which on a fresh
        // client does not exist — so the download saved to the client's default
        // folder instead of the one the library and its processor watch.
        var category = string.IsNullOrWhiteSpace(downloadClient.Category) ? null : downloadClient.Category.Trim();
        var forceOverride = request.Force == true;
        var overrideReason = string.IsNullOrWhiteSpace(request.OverrideReason)
            ? "User manually forced this release from search results."
            : request.OverrideReason.Trim();
        var sourcePriorityScore = routing?.Sources
            .FirstOrDefault(source => string.Equals(source.IndexerId, request.IndexerId, StringComparison.OrdinalIgnoreCase)) is { } selectedSource
                ? Math.Max(0, 200 - selectedSource.Priority)
                : 0;
        var selectedDecision = acquisitionPipeline.EvaluateSelectedRelease(
            new AcquisitionSelectedReleaseRequest(
                request.ReleaseName!.Trim(),
                request.IndexerId?.Trim(),
                request.IndexerName?.Trim(),
                request.DownloadUrl!.Trim(),
                wantedItem.CurrentQuality,
                wantedItem.TargetQuality,
                request.CandidateQuality?.Trim(),
                request.SizeBytes,
                request.Seeders,
                sourcePriorityScore,
                customFormats,
                ForceOverride: forceOverride,
                 OverrideReason: forceOverride ? overrideReason : null,
                 PreventLowerQualityReplacements: wantedItem.PreventLowerQualityReplacements,
                 ScoringMode: platformSettings.SearchScoringMode,
                 MediaType: mediaType,
                 CurrentReleaseName: wantedItem.FilePath,
                 PreferencePlan: preferencePlan,
                 CurrentPreferenceEvaluation: currentPreferenceEvaluation,
                 CurrentFilePresent: wantedItem.HasFile,
                 RuntimeMinutes: item.RuntimeMinutes,
                 // A profile-pinned plan is the immutable source of truth for
                 // manual selection. Do not require the mutable guide store
                 // when that plan is available; a guide refresh or an
                 // unavailable guide service must not change or block the
                 // selected-release decision.
                 GuidePackage: preferencePlan is not null || guidePackageStore is null
                     ? null
                     : (await guidePackageStore.GetCurrentAsync(cancellationToken)).Package));
        if (!selectedDecision.CanDispatch)
        {
            var hint = selectedDecision.RequiresOverride
                ? " Use force override if you still want this exact release."
                : string.Empty;
            return MediaGrabResult.ValidationResult(new Dictionary<string, string[]>
            {
                ["force"] = [$"{selectedDecision.Reason}{hint}"]
            });
        }

        var releaseName = request.ReleaseName.Trim();
        var indexerName = request.IndexerName?.Trim();
        // Plain-English routing reason (#808): null for the ordinary case of
        // an untagged client, which is why this never shows up as noise on
        // every grab — only a title sent somewhere because of its tags says so.
        var routingReason = await acquisitionPipeline.ExplainDownloadClientRoutingAsync(
            downloadClient.DownloadClientId,
            titleTags,
            cancellationToken);
        var grabResult = await downloadClientGrabService.GrabAsync(
            downloadClient.DownloadClientId,
            new DownloadClientGrabRequest(
                releaseName,
                request.DownloadUrl.Trim(),
                mediaType,
                category,
                indexerName,
                IndexerId: request.IndexerId?.Trim()),
            cancellationToken);

        var auditPayload = new
        {
            selectedRelease = request,
            decision = selectedDecision,
            forceOverride,
            overrideReason = forceOverride ? overrideReason : null,
            grabResult
        };
        var serializedAudit = JsonSerializer.Serialize(auditPayload);

        await jobQueueRepository.RecordDownloadDispatchAsync(
            library.Id,
            mediaType,
            entityType,
            item.Id,
            releaseName,
            string.IsNullOrWhiteSpace(indexerName) ? "Manual selection" : indexerName,
            downloadClient.DownloadClientId,
            downloadClient.DownloadClientName,
            grabResult.Status,
            serializedAudit,
            grabResponseCode: grabResult.Succeeded ? 200 : 400,
            grabFailureCode: grabResult.Failure?.Code ?? grabResult.FailureCode,
            cancellationToken: cancellationToken,
            replacementAuthorized: wantedItem.HasFile,
            forceReplacementAuthorized: wantedItem.HasFile && forceOverride,
            replacementExpectedPath: wantedItem.FilePath,
            clientExternalId: grabResult.ExternalId,
            startedBy: DownloadStartedBy.Manual);

        var now = timeProvider.GetUtcNow();

        // It is on its way, so the shelf stops saying "go and get this" and the
        // cycle stops looking for it. The automatic searches take the same step.
        await mediaStateRepository.MarkDownloadingIfSentAsync(kind, item.Id, grabResult.Status, now, cancellationToken);

        await recordSearchAttemptAsync(
            item.Id,
            library.Id,
            forceOverride ? "manual-force-grab" : "manual-grab",
            grabResult.Status == "sent" ? "matched" : "checked",
            now,
            now.AddHours(Math.Max(1, library.RetryDelayHours)),
            forceOverride ? $"{grabResult.Message} Force override: {overrideReason}" : grabResult.Message,
            releaseName,
            indexerName,
            serializedAudit,
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            forceOverride ? $"{entityType}.release.force-grabbed" : $"{entityType}.release.grabbed",
            // Naming the release is the point of the row. "A release was
            // selected" leaves the one fact a person came to find - which
            // release Deluno actually took - only in the drawer behind it.
            (forceOverride
                ? $"{releaseName} was forced for {item.Title} and sent to {downloadClient.DownloadClientName}."
                : $"{releaseName} was chosen for {item.Title} by hand and sent to {downloadClient.DownloadClientName}.")
                + (routingReason is null ? string.Empty : $" {routingReason}"),
            serializedAudit,
            null,
            entityType,
            item.Id,
            cancellationToken);

        await activityFeedRepository.RecordDecisionAsync(
            new DecisionExplanationPayload(
                Scope: forceOverride ? $"{entityType}.grab.force" : $"{entityType}.grab.manual",
                Status: grabResult.Status,
                Reason: forceOverride
                    ? $"User override selected {releaseName}: {overrideReason}"
                    : selectedDecision.Reason,
                Inputs: new Dictionary<string, string?>
                {
                    ["releaseName"] = releaseName,
                    ["indexerName"] = indexerName,
                    ["downloadClientId"] = downloadClient.DownloadClientId,
                    ["downloadClientName"] = downloadClient.DownloadClientName,
                    ["downloadClientRoutingReason"] = routingReason,
                    ["policyVersion"] = selectedDecision.PolicyVersion,
                    ["forceOverride"] = forceOverride.ToString()
                },
                Outcome: grabResult.Message,
                Alternatives: selectedDecision.Alternatives),
            null,
            entityType,
            item.Id,
            cancellationToken);

        return new MediaGrabResult(
            NotFound: false,
            ValidationErrors: null,
            ReleaseName: releaseName,
            IndexerName: indexerName,
            ForceOverride: forceOverride,
            OverrideReason: forceOverride ? overrideReason : null,
            DispatchStatus: grabResult.Status,
            DispatchMessage: grabResult.Message);
    }

    public static Dictionary<string, string[]> ValidateReleaseGrab(MediaReleaseGrabRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.ReleaseName))
        {
            errors["releaseName"] = ["Choose a release before sending it to a download client."];
        }

        if (string.IsNullOrWhiteSpace(request.DownloadUrl))
        {
            errors["downloadUrl"] = ["This release does not include a downloadable URL. Choose a different release or check the indexer configuration."];
        }
        else if (!Uri.TryCreate(request.DownloadUrl, UriKind.Absolute, out _))
        {
            errors["downloadUrl"] = ["The selected release has an invalid download URL."];
        }

        return errors;
    }

    private static async Task<IReadOnlyList<Deluno.Quality.Contracts.CustomFormatItem>> ResolveCustomFormatsAsync(
        IQualityRepository repository,
        string? qualityProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return [];
        }

        var profiles = await repository.ListQualityProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(item => item.Id == qualityProfileId);
        if (profile is null || string.IsNullOrWhiteSpace(profile.CustomFormatIds))
        {
            return [];
        }

        var ids = profile.CustomFormatIds
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 0)
        {
            return [];
        }

        var formats = await repository.ListCustomFormatsAsync(cancellationToken);
        return formats.Where(item => ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
}
