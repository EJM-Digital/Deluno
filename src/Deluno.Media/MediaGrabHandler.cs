using System.Text.Json;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Deluno.Libraries.Data;
using Deluno.Platform.Data;
using Deluno.Quality.Data;

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
        CancellationToken cancellationToken)
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

        var wanted = await mediaStateRepository.GetWantedSummaryAsync(kind, cancellationToken);
        var wantedItem = wanted.RecentItems.FirstOrDefault(candidate => candidate.Id == id);
        var entityType = kind == MediaKind.Movie ? "movie" : "series";
        var mediaType = kind == MediaKind.Movie ? "movies" : "tv";
        if (wantedItem is null || string.IsNullOrWhiteSpace(wantedItem.LibraryId))
        {
            return MediaGrabResult.ValidationResult(new Dictionary<string, string[]>
            {
                [$"{entityType}Id"] = [$"This {entityType} is not currently linked to a searchable library."]
            });
        }

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(candidate => candidate.Id == wantedItem.LibraryId);
        if (library is null)
        {
            return MediaGrabResult.ValidationResult(new Dictionary<string, string[]>
            {
                ["libraryId"] = [$"Deluno could not find the linked library for this {entityType}."]
            });
        }

        var routing = await librariesRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
        var downloadClient = routing?.DownloadClients.OrderBy(candidate => candidate.Priority).FirstOrDefault();
        if (downloadClient is null)
        {
            return MediaGrabResult.ValidationResult(new Dictionary<string, string[]>
            {
                ["downloadClient"] = ["Link a download client to this library before grabbing a release."]
            });
        }

        var platformSettings = await platformSettingsRepository.GetAsync(cancellationToken);
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
        var customFormats = await ResolveCustomFormatsAsync(
            qualityRepository,
            library.QualityProfileId,
            cancellationToken);
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
                ScoringMode: platformSettings.SearchScoringMode));
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
        var grabResult = await downloadClientGrabService.GrabAsync(
            downloadClient.DownloadClientId,
            new DownloadClientGrabRequest(
                releaseName,
                request.DownloadUrl.Trim(),
                mediaType,
                category,
                indexerName),
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
            grabFailureCode: null,
            cancellationToken: cancellationToken);

        var now = timeProvider.GetUtcNow();
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
            forceOverride
                ? $"{item.Title} release was force grabbed and sent to {downloadClient.DownloadClientName}."
                : $"{item.Title} release was manually selected and sent to {downloadClient.DownloadClientName}.",
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
