using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Intake;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Intake;

public sealed class IntakeSyncService(
    IPlatformSettingsRepository platformSettingsRepository,
    ILibrariesRepository librariesRepository,
    IIntakeRepository intakeRepository,
    IJobScheduler jobScheduler,
    IJobQueueRepository jobQueueRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IMetadataProvider metadataProvider,
    IMediaDecisionService mediaDecisionService,
    IActivityFeedRepository activityFeedRepository,
    IConfiguration configuration,
    TimeProvider timeProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<IntakeSyncService> logger)
    : IIntakeSyncService, IIntakeListPreviewService, IIntakeListApprovalService
{
    private const int PreviewLimit = 100;
    private const string IntakeHttpClientName = "deluno-intake";
    private static readonly Regex ImdbListIdRegex = new(@"ls\d{4,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TmdbListIdRegex = new(@"(?:^|/)(\d{3,})(?:$|[/?#])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YearRegex = new(@"(?:\(|\b)(19\d{2}|20\d{2}|2100)(?:\)|\b)", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> PlanDueSyncJobsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var sources = await intakeRepository.ListIntakeSourcesAsync(cancellationToken);
        var queued = 0;

        foreach (var source in sources.Where(item => item.IsEnabled))
        {
            var interval = TimeSpan.FromHours(Math.Clamp(source.SyncIntervalHours, 1, 168));
            var last = source.LastSyncUtc ?? source.CreatedUtc;
            if (now - last < interval)
            {
                continue;
            }

            var bucket = $"{now:yyyyMMddHH}";
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "intake.sync",
                    Source: "intake",
                    PayloadJson: JsonSerializer.Serialize(new IntakeSyncPayload(source.Id, false), JsonOptions),
                    RelatedEntityType: "intake-source",
                    RelatedEntityId: source.Id,
                    IdempotencyKey: $"intake.sync.auto:{source.Id}:{bucket}",
                    DedupeKey: $"intake.sync:{source.Id}"),
                cancellationToken);
            queued++;
        }

        return queued;
    }

    public async Task<IntakeListPreviewResult> PreviewAsync(string sourceId, CancellationToken cancellationToken)
    {
        var source = await intakeRepository.GetIntakeSourceAsync(sourceId, cancellationToken)
            ?? throw new InvalidOperationException("Import list not found.");
        var targetLibrary = ResolveTargetLibrary(source, await librariesRepository.ListLibrariesAsync(cancellationToken));
        var entries = await FetchEntriesAsync(source, cancellationToken);
        var mediaType = source.MediaType == "tv" ? "tv" : "movies";
        var exclusions = await intakeRepository.ListActiveIntakeListExclusionsAsync(source.Id, cancellationToken);
        var excludedKeys = exclusions.ToDictionary(item => item.EntryKey, item => item, StringComparer.OrdinalIgnoreCase);

        var items = new List<IntakeListPreviewItem>();
        foreach (var entry in entries.Take(PreviewLimit))
        {
            var entryMediaType = entry.MediaType == "tv" ? "tv" : "movies";
            if (!TryResolveTitle(entry, out var title))
            {
                items.Add(new IntakeListPreviewItem("Untitled entry", entry.Year, entryMediaType, entry.ImdbId,
                    "not eligible", "This list entry has no usable title.", "none"));
                continue;
            }

            if (excludedKeys.TryGetValue(BuildKey(title, entry.Year, entry.ImdbId), out var exclusion))
            {
                items.Add(new IntakeListPreviewItem(title, entry.Year, entryMediaType, entry.ImdbId,
                    "excluded", "You previously chose not to add this entry from this list.", GetMatchConfidence(entry), exclusion.Id));
                continue;
            }

            if (!PassEntryFilters(source, entry, timeProvider.GetUtcNow(), out var filterReason))
            {
                items.Add(new IntakeListPreviewItem(title, entry.Year, entryMediaType, entry.ImdbId,
                    "not eligible", filterReason, GetMatchConfidence(entry)));
                continue;
            }

            var existing = await FindExistingIdAsync(
                entryMediaType,
                title,
                entry.Year,
                entry.ImdbId,
                string.IsNullOrWhiteSpace(entry.ProviderId) ? null : "tmdb",
                entry.ProviderId,
                cancellationToken);
            items.Add(existing is not null
                ? new IntakeListPreviewItem(title, entry.Year, entryMediaType, entry.ImdbId,
                    "already in library", "A matching title is already in this Deluno library.", GetMatchConfidence(entry))
                : new IntakeListPreviewItem(title, entry.Year, entryMediaType, entry.ImdbId,
                    "would add", "This title passes the list's available filters and would be added on sync.", GetMatchConfidence(entry)));
        }

        var warnings = new List<string>();
        if (targetLibrary is null)
        {
            warnings.Add("No compatible target library is configured. Sync will not add any titles until you choose one.");
        }
        if (!string.IsNullOrWhiteSpace(source.RequiredGenres) || source.MinimumRating is not null || !string.IsNullOrWhiteSpace(source.AllowedCertifications))
        {
            warnings.Add("Genre, rating, and certification checks are verified against title metadata during sync when the list does not provide them.");
        }
        if (entries.Count > PreviewLimit)
        {
            warnings.Add($"Showing the first {PreviewLimit} entries. Sync will evaluate all {entries.Count} entries using the same rules.");
        }

        return new IntakeListPreviewResult(
            source.Id,
            source.Name,
            source.Provider,
            mediaType,
            targetLibrary?.Name,
            entries.Count,
            items.Count,
            entries.Count > PreviewLimit,
            items,
            warnings);
    }

    public async Task<IntakeSyncRunResult> RunAsync(string sourceId, string? relatedJobId, bool manual, CancellationToken cancellationToken)
    {
        var source = await intakeRepository.GetIntakeSourceAsync(sourceId, cancellationToken);
        if (source is null)
        {
            throw new InvalidOperationException("Intake source not found.");
        }

        return await RunSourceAsync(source, relatedJobId, manual, null, null, cancellationToken);
    }

    public async Task<IntakeListApprovalResult> ApproveAsync(
        string sourceId,
        ApproveIntakeListPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Entries is null || request.Entries.Count == 0)
        {
            throw new InvalidOperationException("Choose at least one preview entry to add.");
        }

        if (request.Entries.Count > PreviewLimit)
        {
            throw new InvalidOperationException($"Choose no more than {PreviewLimit} preview entries at a time.");
        }

        var source = await intakeRepository.GetIntakeSourceAsync(sourceId, cancellationToken)
            ?? throw new InvalidOperationException("Import list not found.");
        var selectedKeys = request.Entries
            .Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .Select(item => BuildKey(item.Title, item.Year, item.ImdbId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = await FetchEntriesAsync(source, cancellationToken);
        var matched = entries.Where(entry =>
            TryResolveTitle(entry, out var title) &&
            selectedKeys.Contains(BuildKey(title, entry.Year, entry.ImdbId)))
            .ToArray();

        var sync = await RunSourceAsync(source, null, true, matched, request.SearchAfterAdd, cancellationToken);
        await activityFeedRepository.RecordActivityAsync(
            "intake.preview.approved",
            $"{source.Name}: approved {matched.Length} of {request.Entries.Count} previewed entries.",
            JsonSerializer.Serialize(new
            {
                source.Id,
                source.Name,
                selected = request.Entries.Count,
                matched = matched.Length,
                request.SearchAfterAdd,
                sync.AddedCount,
                sync.DuplicateCount,
                sync.SkippedCount,
                sync.ErrorCount
            }, JsonOptions),
            null,
            "intake-source",
            source.Id,
            cancellationToken);

        return new IntakeListApprovalResult(
            request.Entries.Count,
            matched.Length,
            sync.AddedCount,
            sync.DuplicateCount,
            sync.SkippedCount,
            sync.ErrorCount,
            sync.SearchRequested,
            sync.Summary);
    }

    private async Task<IntakeSyncRunResult> RunSourceAsync(
        IntakeSourceItem source,
        string? relatedJobId,
        bool manual,
        IReadOnlyList<IntakeEntry>? suppliedEntries,
        bool? searchOnAddOverride,
        CancellationToken cancellationToken)
    {

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var targetLibrary = ResolveTargetLibrary(source, libraries);
        if (targetLibrary is null)
        {
            var failureSummary = "No compatible target library exists for this source media type.";
            await intakeRepository.RecordIntakeSourceSyncResultAsync(source.Id, timeProvider.GetUtcNow(), "error", failureSummary, cancellationToken);
            await activityFeedRepository.RecordActivityAsync(
                "intake.sync.failed",
                $"{source.Name} sync failed: {failureSummary}",
                JsonSerializer.Serialize(new { source.Id, source.Name, source.MediaType }, JsonOptions),
                relatedJobId,
                "intake-source",
                source.Id,
                cancellationToken);

            return new IntakeSyncRunResult(source.Id, source.Name, "error", 0, 0, 0, 0, 1, false, failureSummary);
        }

        IReadOnlyList<IntakeEntry> entries;
        if (suppliedEntries is not null)
        {
            entries = suppliedEntries;
        }
        else
        {
            try
            {
                entries = await FetchEntriesAsync(source, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Intake source {SourceId} fetch failed.", source.Id);
                var failureSummary = $"Provider fetch failed: {ex.Message}";
                await intakeRepository.RecordIntakeSourceSyncResultAsync(source.Id, timeProvider.GetUtcNow(), "error", failureSummary, cancellationToken);
                await activityFeedRepository.RecordActivityAsync(
                    "intake.sync.failed",
                    $"{source.Name} sync failed during fetch.",
                    JsonSerializer.Serialize(new { source.Id, source.Name, source.Provider, source.FeedUrl, error = ex.Message }, JsonOptions),
                    relatedJobId,
                    "intake-source",
                    source.Id,
                    cancellationToken);

                return new IntakeSyncRunResult(source.Id, source.Name, "error", 0, 0, 0, 0, 1, false, failureSummary);
            }
        }

        var skipReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var excludedKeys = (await intakeRepository.ListActiveIntakeListExclusionsAsync(source.Id, cancellationToken))
            .Select(item => item.EntryKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var duplicates = 0;
        var added = 0;
        var errors = 0;
        var shouldRequestSearch = false;

        foreach (var entry in entries)
        {
            try
            {
                if (!TryResolveTitle(entry, out var baseTitle))
                {
                    skipped++;
                    Increment(skipReasons, "Entry had no usable title.");
                    continue;
                }

                if (excludedKeys.Contains(BuildKey(baseTitle, entry.Year, entry.ImdbId)))
                {
                    skipped++;
                    Increment(skipReasons, "Entry was excluded by the user.");
                    continue;
                }

                if (!PassEntryFilters(source, entry, timeProvider.GetUtcNow(), out var preReason))
                {
                    skipped++;
                    Increment(skipReasons, preReason);
                    continue;
                }

                var metadata = await ResolveMetadataAsync(source, entry, baseTitle, cancellationToken);
                if (!PassMetadataFilters(source, entry, metadata, timeProvider.GetUtcNow(), out var metadataReason))
                {
                    skipped++;
                    Increment(skipReasons, metadataReason);
                    continue;
                }

                var resolvedTitle = metadata?.Title?.Trim();
                if (string.IsNullOrWhiteSpace(resolvedTitle))
                {
                    resolvedTitle = baseTitle;
                }

                var resolvedYear = metadata?.Year ?? entry.Year;
                var resolvedImdb = metadata?.ImdbId ?? entry.ImdbId;
                var mediaType = entry.MediaType == "tv" ? "tv" : "movies";

                // One indexed lookup per list entry, asked with everything the
                // metadata lookup resolved. This used to be a dictionary built
                // from the whole catalogue, rebuilt on every sync -- and the
                // dictionary was the weaker check, keyed on IMDb id or on
                // title+year but never on the provider id.
                var existingId = await FindExistingIdAsync(
                    mediaType,
                    resolvedTitle!,
                    resolvedYear,
                    resolvedImdb,
                    metadata?.Provider,
                    metadata?.ProviderId,
                    cancellationToken);

                if (existingId is null)
                {
                    if (mediaType == "tv")
                    {
                        var created = await seriesCatalogRepository.AddAsync(
                            new CreateSeriesRequest(
                                Title: resolvedTitle,
                                StartYear: resolvedYear,
                                ImdbId: resolvedImdb,
                                Monitored: true,
                                MetadataProvider: metadata?.Provider,
                                MetadataProviderId: metadata?.ProviderId,
                                OriginalTitle: metadata?.OriginalTitle,
                                Overview: metadata?.Overview,
                                PosterUrl: metadata?.PosterUrl,
                                BackdropUrl: metadata?.BackdropUrl,
                                Rating: metadata?.Rating,
                                Genres: metadata is null ? entry.GenresCsv : string.Join(", ", metadata.Genres),
                                ExternalUrl: metadata?.ExternalUrl,
                                MetadataJson: metadata is null ? null : JsonSerializer.Serialize(metadata, JsonOptions)),
                            cancellationToken);
                        existingId = created.Id;
                    }
                    else
                    {
                        var created = await movieCatalogRepository.AddAsync(
                            new CreateMovieRequest(
                                Title: resolvedTitle,
                                ReleaseYear: resolvedYear,
                                ImdbId: resolvedImdb,
                                Monitored: true,
                                MetadataProvider: metadata?.Provider,
                                MetadataProviderId: metadata?.ProviderId,
                                OriginalTitle: metadata?.OriginalTitle,
                                Overview: metadata?.Overview,
                                PosterUrl: metadata?.PosterUrl,
                                BackdropUrl: metadata?.BackdropUrl,
                                Rating: metadata?.Rating,
                                Genres: metadata is null ? entry.GenresCsv : string.Join(", ", metadata.Genres),
                                ExternalUrl: metadata?.ExternalUrl,
                                MetadataJson: metadata is null ? null : JsonSerializer.Serialize(metadata, JsonOptions)),
                            cancellationToken);
                        existingId = created.Id;
                    }

                    added++;
                }
                else
                {
                    duplicates++;
                }

                await intakeRepository.RecordIntakeTitleOriginAsync(
                    new CreateIntakeTitleOriginRequest(
                        source.Id,
                        source.Name,
                        source.Provider,
                        mediaType,
                        existingId!,
                        BuildKey(resolvedTitle!, resolvedYear, resolvedImdb),
                        resolvedTitle!,
                        resolvedYear,
                        resolvedImdb),
                    cancellationToken);

                var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                    MediaType: targetLibrary.MediaType,
                    HasFile: false,
                    CurrentQuality: null,
                    CutoffQuality: targetLibrary.CutoffQuality,
                    UpgradeUntilCutoff: targetLibrary.UpgradeUntilCutoff,
                    UpgradeUnknownItems: targetLibrary.UpgradeUnknownItems));

                if (mediaType == "tv")
                {
                    await seriesCatalogRepository.EnsureWantedStateAsync(
                        existingId!,
                        targetLibrary.Id,
                        decision.WantedStatus,
                        decision.WantedReason,
                        false,
                        decision.CurrentQuality,
                        decision.TargetQuality,
                        decision.QualityCutoffMet,
                        cancellationToken);

                    if (!string.IsNullOrWhiteSpace(source.QualityProfileId))
                    {
                        await seriesCatalogRepository.UpdateQualityProfileAsync(existingId!, source.QualityProfileId!, cancellationToken);
                    }
                }
                else
                {
                    await movieCatalogRepository.EnsureWantedStateAsync(
                        existingId!,
                        targetLibrary.Id,
                        decision.WantedStatus,
                        decision.WantedReason,
                        false,
                        decision.CurrentQuality,
                        decision.TargetQuality,
                        decision.QualityCutoffMet,
                        cancellationToken);

                    if (!string.IsNullOrWhiteSpace(source.QualityProfileId))
                    {
                        await movieCatalogRepository.UpdateQualityProfileAsync(existingId!, source.QualityProfileId!, cancellationToken);
                    }
                }

                shouldRequestSearch = shouldRequestSearch || (searchOnAddOverride ?? source.SearchOnAdd);
            }
            catch (Exception ex)
            {
                errors++;
                logger.LogWarning(ex, "Intake source {SourceId} failed processing entry.", source.Id);
                Increment(skipReasons, $"Entry error: {ex.Message}");
            }
        }

        var searchRequested = false;
        if (shouldRequestSearch)
        {
            searchRequested = await jobQueueRepository.RequestLibrarySearchAsync(
                new LibraryAutomationPlanItem(
                    LibraryId: targetLibrary.Id,
                    LibraryName: targetLibrary.Name,
                    MediaType: targetLibrary.MediaType,
                    AutoSearchEnabled: targetLibrary.AutoSearchEnabled,
                    MissingSearchEnabled: targetLibrary.MissingSearchEnabled,
                    UpgradeSearchEnabled: targetLibrary.UpgradeSearchEnabled,
                    SearchIntervalHours: targetLibrary.SearchIntervalHours,
                    RetryDelayHours: targetLibrary.RetryDelayHours,
                    MaxItemsPerRun: targetLibrary.MaxItemsPerRun,
                    SearchWindowStartHour: targetLibrary.SearchWindowStartHour,
                    SearchWindowEndHour: targetLibrary.SearchWindowEndHour),
                cancellationToken);
        }

        var status = errors > 0
            ? "partial"
            : "success";
        var summary = $"Fetched {entries.Count}, added {added}, duplicates {duplicates}, skipped {skipped}, errors {errors}.";

        await intakeRepository.RecordIntakeSourceSyncResultAsync(source.Id, timeProvider.GetUtcNow(), status, summary, cancellationToken);
        await activityFeedRepository.RecordActivityAsync(
            "intake.sync.completed",
            $"{source.Name} sync completed ({status}). {summary}",
            JsonSerializer.Serialize(new
            {
                source.Id,
                source.Name,
                source.Provider,
                source.MediaType,
                targetLibrary = new { targetLibrary.Id, targetLibrary.Name },
                manual,
                fetched = entries.Count,
                added,
                duplicates,
                skipped,
                errors,
                searchRequested,
                skipReasons
            }, JsonOptions),
            relatedJobId,
            "intake-source",
            source.Id,
            cancellationToken);

        foreach (var pair in skipReasons.OrderByDescending(item => item.Value).Take(10))
        {
            await activityFeedRepository.RecordActivityAsync(
                "intake.sync.skipped",
                $"{source.Name}: {pair.Key} ({pair.Value})",
                null,
                relatedJobId,
                "intake-source",
                source.Id,
                cancellationToken);
        }

        return new IntakeSyncRunResult(
            source.Id,
            source.Name,
            status,
            entries.Count,
            added,
            duplicates,
            skipped,
            errors,
            searchRequested,
            summary);
    }

    private async Task<IReadOnlyList<IntakeEntry>> FetchEntriesAsync(IntakeSourceItem source, CancellationToken cancellationToken)
    {
        var provider = source.Provider.Trim().ToLowerInvariant();
        var mediaType = source.MediaType == "tv" ? "tv" : "movies";
        return provider switch
        {
            "tmdb" => await FetchTmdbListAsync(source, mediaType, cancellationToken),
            "tmdb-person" => await FetchTmdbPersonCreditsAsync(source, mediaType, cancellationToken),
            "mdblist" => await FetchMdbListAsync(source, mediaType, cancellationToken),
            "imdb" => await FetchImdbListAsync(source, mediaType, cancellationToken),
            "trakt" => await FetchTraktListAsync(source, mediaType, cancellationToken),
            "rss" or "letterboxd" or "url-list" => await FetchGenericListAsync(source, mediaType, cancellationToken),
            _ => await FetchGenericListAsync(source, mediaType, cancellationToken)
        };
    }

    private async Task<IReadOnlyList<IntakeEntry>> FetchTmdbPersonCreditsAsync(
        IntakeSourceItem source,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var apiKey = await GetManagedSecretAsync(
            "Deluno:Metadata:TMDbApiKey",
            "TMDB_API_KEY",
            "tmdb",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("The Deluno metadata service is not configured for TMDb person import lists.");
        }

        if (!TmdbPersonSource.TryParse(source.FeedUrl, out var personId, out var creditTypes))
        {
            throw new InvalidOperationException("TMDb person source requires a person URL or numeric person ID.");
        }

        var url = $"https://api.themoviedb.org/3/person/{Uri.EscapeDataString(personId)}/combined_credits?api_key={Uri.EscapeDataString(apiKey)}&language=en-US";
        using var client = httpClientFactory.CreateClient(IntakeHttpClientName);
        var json = await client.GetStringAsync(url, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var results = new List<IntakeEntry>();

        if (creditTypes.Contains("cast", StringComparer.OrdinalIgnoreCase) &&
            document.RootElement.TryGetProperty("cast", out var cast) &&
            cast.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in cast.EnumerateArray())
            {
                AddTmdbPersonEntry(results, item, mediaType);
            }
        }

        if (document.RootElement.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in crew.EnumerateArray())
            {
                if (creditTypes.Any(type => MatchesTmdbPersonCrewRole(item, type)))
                {
                    AddTmdbPersonEntry(results, item, mediaType);
                }
            }
        }

        return results
            .GroupBy(entry => $"{entry.ProviderId ?? entry.Title}|{entry.Year}|{entry.MediaType}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static void AddTmdbPersonEntry(List<IntakeEntry> results, JsonElement item, string fallbackMediaType)
    {
        var title = ReadString(item, "title") ?? ReadString(item, "name");
        var itemMediaType = NormalizeMediaType(ReadString(item, "media_type"), fallbackMediaType);
        var providerId = item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var numericId)
            ? numericId.ToString(CultureInfo.InvariantCulture)
            : ReadString(item, "id");

        if (string.IsNullOrWhiteSpace(title) || itemMediaType != fallbackMediaType)
        {
            return;
        }

        results.Add(new IntakeEntry(
            Title: title,
            Year: ParseYear(ReadString(item, "release_date") ?? ReadString(item, "first_air_date")),
            MediaType: itemMediaType,
            ImdbId: null,
            GenresCsv: string.Empty,
            Rating: ReadNumber(item, "vote_average"),
            ReleaseDateUtc: ParseDate(ReadString(item, "release_date") ?? ReadString(item, "first_air_date")),
            Certification: null,
            Audience: ReadBoolean(item, "adult") ? "adult" : "any",
            ProviderId: providerId));
    }

    private static bool MatchesTmdbPersonCrewRole(JsonElement item, string creditType)
    {
        var department = ReadString(item, "department");
        var job = ReadString(item, "job");
        return creditType switch
        {
            "director" => string.Equals(department, "Directing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(job, "Director", StringComparison.OrdinalIgnoreCase),
            "producer" => string.Equals(department, "Production", StringComparison.OrdinalIgnoreCase) ||
                job?.Contains("producer", StringComparison.OrdinalIgnoreCase) == true,
            "sound" => string.Equals(department, "Sound", StringComparison.OrdinalIgnoreCase),
            "writing" => string.Equals(department, "Writing", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private async Task<IReadOnlyList<IntakeEntry>> FetchTmdbListAsync(IntakeSourceItem source, string mediaType, CancellationToken cancellationToken)
    {
        var apiKey = await GetManagedSecretAsync(
            "Deluno:Metadata:TMDbApiKey",
            "TMDB_API_KEY",
            "tmdb",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("The Deluno metadata service is not configured for TMDb import lists.");
        }

        var listId = ResolveTmdbListId(source.FeedUrl);
        if (string.IsNullOrWhiteSpace(listId))
        {
            throw new InvalidOperationException("TMDB source requires a list id or TMDB list URL.");
        }

        var url = $"https://api.themoviedb.org/3/list/{Uri.EscapeDataString(listId)}?api_key={Uri.EscapeDataString(apiKey)}";
        using var client = httpClientFactory.CreateClient(IntakeHttpClientName);
        var json = await client.GetStringAsync(url, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<IntakeEntry>();
        foreach (var item in items.EnumerateArray())
        {
            var title = ReadString(item, "title") ?? ReadString(item, "name");
            var year = ParseYear(ReadString(item, "release_date") ?? ReadString(item, "first_air_date"));
            var rating = ReadNumber(item, "vote_average");
            var releaseDate = ParseDate(ReadString(item, "release_date") ?? ReadString(item, "first_air_date"));
            var adult = ReadBoolean(item, "adult");
            var itemMediaType = NormalizeMediaType(ReadString(item, "media_type"), mediaType);
            results.Add(new IntakeEntry(
                Title: title,
                Year: year,
                MediaType: itemMediaType,
                ImdbId: null,
                GenresCsv: string.Empty,
                Rating: rating,
                ReleaseDateUtc: releaseDate,
                Certification: null,
                Audience: adult ? "adult" : "any"));
        }

        return results;
    }

    private async Task<IReadOnlyList<IntakeEntry>> FetchMdbListAsync(IntakeSourceItem source, string mediaType, CancellationToken cancellationToken)
    {
        var apiKey = await GetManagedSecretAsync(
            "Deluno:Metadata:MDbListApiKey",
            "MDBLIST_API_KEY",
            "mdblist",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("The Deluno metadata service is not configured for MDbList import lists.");
        }

        var list = ResolveMdbListReference(source.FeedUrl);
        if (list is null)
        {
            throw new InvalidOperationException("MDbList source requires a public list URL in the form https://mdblist.com/lists/owner/list-name.");
        }

        using var client = httpClientFactory.CreateClient(IntakeHttpClientName);

        var results = new List<IntakeEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var url = BuildMdbListItemsUrl(list.Value.Owner, list.Value.Slug, apiKey, cursor);
            var json = await client.GetStringAsync(url, cancellationToken);
            using var document = JsonDocument.Parse(json);
            foreach (var entry in ParseMdbListEntries(document.RootElement, mediaType))
            {
                var key = $"{entry.ImdbId}|{entry.Title}|{entry.Year}|{entry.MediaType}";
                if (seen.Add(key))
                {
                    results.Add(entry);
                }
            }

            cursor = ReadMdbNextCursor(document.RootElement);
            if (string.IsNullOrWhiteSpace(cursor))
            {
                break;
            }
        }

        return results;
    }

    private async Task<string?> GetManagedSecretAsync(
        string configurationKey,
        string environmentKey,
        string legacySecretName,
        CancellationToken cancellationToken)
    {
        var configured = configuration[configurationKey]
                         ?? configuration[environmentKey]
                         ?? Environment.GetEnvironmentVariable(environmentKey);
        return string.IsNullOrWhiteSpace(configured)
            ? await platformSettingsRepository.GetMetadataProviderSecretAsync(legacySecretName, cancellationToken)
            : configured.Trim();
    }

    private async Task<IReadOnlyList<IntakeEntry>> FetchImdbListAsync(IntakeSourceItem source, string mediaType, CancellationToken cancellationToken)
    {
        var url = ResolveImdbCsvUrl(source.FeedUrl);
        using var client = httpClientFactory.CreateClient(IntakeHttpClientName);
        var csv = await client.GetStringAsync(url, cancellationToken);
        if (string.IsNullOrWhiteSpace(csv))
        {
            return [];
        }

        var lines = csv
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
        if (lines.Length < 2)
        {
            return [];
        }

        var header = ParseCsvLine(lines[0]);
        var titleIndex = FindIndex(header, "Title");
        var yearIndex = FindIndex(header, "Year");
        var idIndex = FindIndex(header, "Const");
        var genresIndex = FindIndex(header, "Genres");
        var ratingIndex = FindIndex(header, "IMDb Rating");
        var certIndex = FindIndex(header, "Certificate");

        var results = new List<IntakeEntry>();
        foreach (var line in lines.Skip(1))
        {
            var cells = ParseCsvLine(line);
            if (cells.Length == 0)
            {
                continue;
            }

            var title = ValueAt(cells, titleIndex);
            var year = ParseInt(ValueAt(cells, yearIndex));
            var imdbId = NormalizeImdbId(ValueAt(cells, idIndex));
            var genres = ValueAt(cells, genresIndex) ?? string.Empty;
            var rating = ParseDouble(ValueAt(cells, ratingIndex));
            var cert = ValueAt(cells, certIndex);
            results.Add(new IntakeEntry(
                Title: title,
                Year: year,
                MediaType: mediaType,
                ImdbId: imdbId,
                GenresCsv: genres,
                Rating: rating,
                ReleaseDateUtc: year is null ? null : new DateTimeOffset(year.Value, 12, 31, 0, 0, 0, TimeSpan.Zero),
                Certification: cert,
                Audience: GuessAudience(cert, genres)));
        }

        return results;
    }

    private async Task<IReadOnlyList<IntakeEntry>> FetchTraktListAsync(IntakeSourceItem source, string mediaType, CancellationToken cancellationToken)
    {
        var rssUrl = ResolveTraktRssUrl(source.FeedUrl);
        using var client = httpClientFactory.CreateClient(IntakeHttpClientName);
        var xml = await client.GetStringAsync(rssUrl, cancellationToken);
        return ParseRss(xml, mediaType);
    }

    private async Task<IReadOnlyList<IntakeEntry>> FetchGenericListAsync(IntakeSourceItem source, string mediaType, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(source.FeedUrl, UriKind.Absolute, out var uri))
        {
            if (ResolveMdbListReference(source.FeedUrl) is not null)
            {
                return await FetchPublicMdbListAsync(uri, mediaType, cancellationToken);
            }

            using var client = httpClientFactory.CreateClient(IntakeHttpClientName);
            var body = await client.GetStringAsync(uri, cancellationToken);
            if (LooksLikeXml(body))
            {
                return ParseRss(body, mediaType);
            }

            return ParsePlainList(body, mediaType);
        }

        return ParsePlainList(source.FeedUrl, mediaType);
    }

    private async Task<IReadOnlyList<IntakeEntry>> FetchPublicMdbListAsync(Uri listUrl, string mediaType, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(IntakeHttpClientName);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        // MDbList documents public list URLs for direct Radarr/Sonarr use. Its
        // compatible response is selected by media type and requires no API key.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(mediaType == "tv" ? "Sonarr/4.0" : "Radarr/6.0");
        var json = await client.GetStringAsync(listUrl, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("MDbList did not return a compatible public list response.");
        }

        return ParseMdbListEntries(document.RootElement, mediaType);
    }

    private static (string Owner, string Slug)? ResolveMdbListReference(string feedUrl)
    {
        if (!Uri.TryCreate(feedUrl?.Trim(), UriKind.Absolute, out var uri) ||
            !(uri.Host.Equals("mdblist.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.mdblist.com", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || !string.Equals(segments[0], "lists", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segments[2], "external", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return (segments[1], segments[2]);
    }

    private static string BuildMdbListItemsUrl(string owner, string slug, string apiKey, string? cursor)
    {
        // MDbList API keys use the apikey query parameter. OAuth access tokens
        // use Authorization: Bearer and will be handled by a future account-connection flow.
        // Import needs a title, year, and provider IDs. `extended=ids_only`
        // deliberately omits title data, so it cannot be used for this flow.
        var query = $"apikey={Uri.EscapeDataString(apiKey)}&limit=1000{(string.IsNullOrWhiteSpace(cursor) ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}")}";
        return $"https://api.mdblist.com/lists/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(slug)}/items?{query}";
    }

    private static IReadOnlyList<IntakeEntry> ParseMdbListEntries(JsonElement root, string fallbackMediaType)
    {
        var entries = new List<IntakeEntry>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            AddMdbItems(root, fallbackMediaType, entries);
            return entries;
        }

        AddMdbItems(root, "items", fallbackMediaType, entries);
        AddMdbItems(root, "movies", "movies", entries);
        AddMdbItems(root, "shows", "tv", entries);
        return entries;
    }

    private static void AddMdbItems(JsonElement root, string property, string fallbackMediaType, ICollection<IntakeEntry> entries)
    {
        if (!root.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        AddMdbItems(items, fallbackMediaType, entries);
    }

    private static void AddMdbItems(JsonElement items, string fallbackMediaType, ICollection<IntakeEntry> entries)
    {
        foreach (var item in items.EnumerateArray())
        {
            var title = ReadString(item, "title") ?? ReadString(item, "name");
            var year = ReadInt(item, "release_year") ?? ReadInt(item, "year");
            var imdbId = ReadString(item, "imdb_id") ?? ReadNestedString(item, "ids", "imdb") ?? ReadNestedString(item, "ids", "imdbid");
            var rating = ReadNumber(item, "score_average") ?? ReadNumber(item, "imdb_rating") ?? ReadNumber(item, "score");
            var releaseDate = ParseDate(ReadString(item, "released") ?? ReadString(item, "release_date"));
            var itemMediaType = NormalizeMediaType(ReadString(item, "mediatype") ?? ReadString(item, "type"), fallbackMediaType);
            entries.Add(new IntakeEntry(
                Title: title,
                Year: year,
                MediaType: itemMediaType,
                ImdbId: NormalizeImdbId(imdbId),
                GenresCsv: ReadString(item, "genres") ?? string.Empty,
                Rating: rating,
                ReleaseDateUtc: releaseDate,
                Certification: ReadString(item, "certification"),
                Audience: ReadBoolean(item, "adult") ? "adult" : "any"));
        }
    }

    private static string? ReadMdbNextCursor(JsonElement root)
        => ReadNestedString(root, "pagination", "next_cursor") ?? ReadString(root, "next_cursor");

    private async Task<MetadataSearchResult?> ResolveMetadataAsync(
        IntakeSourceItem source,
        IntakeEntry entry,
        string title,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(entry.ImdbId))
        {
            var byImdb = await metadataProvider.SearchAsync(
                new MetadataLookupRequest(entry.ImdbId, entry.MediaType, entry.Year, entry.ImdbId),
                cancellationToken);
            if (byImdb.Count > 0)
            {
                return byImdb[0];
            }
        }

        var matches = await metadataProvider.SearchAsync(
            new MetadataLookupRequest(title, entry.MediaType, entry.Year, entry.ProviderId),
            cancellationToken);
        return matches.FirstOrDefault();
    }

    private static bool PassEntryFilters(IntakeSourceItem source, IntakeEntry entry, DateTimeOffset now, out string reason)
    {
        if (source.MinimumYear is not null && (entry.Year is null || entry.Year.Value < source.MinimumYear.Value))
        {
            reason = $"Below minimum year ({source.MinimumYear}).";
            return false;
        }

        if (source.MinimumRating is not null && (entry.Rating is null || entry.Rating.Value < source.MinimumRating.Value))
        {
            reason = $"Below minimum rating ({source.MinimumRating:0.0}).";
            return false;
        }

        if (source.MaximumAgeDays is not null)
        {
            if (entry.ReleaseDateUtc is null)
            {
                reason = "Missing release date for maximum age filter.";
                return false;
            }

            if ((now - entry.ReleaseDateUtc.Value).TotalDays > source.MaximumAgeDays.Value)
            {
                reason = $"Older than maximum age ({source.MaximumAgeDays} days).";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool PassMetadataFilters(
        IntakeSourceItem source,
        IntakeEntry entry,
        MetadataSearchResult? metadata,
        DateTimeOffset now,
        out string reason)
    {
        var requiredGenres = SplitCsv(source.RequiredGenres);
        if (requiredGenres.Length > 0)
        {
            var actualGenres = MergeGenres(entry.GenresCsv, metadata?.Genres);
            if (actualGenres.Count == 0)
            {
                reason = "Missing genre metadata for required genre filter.";
                return false;
            }

            if (!requiredGenres.Any(required => actualGenres.Contains(required, StringComparer.OrdinalIgnoreCase)))
            {
                reason = $"No required genres matched ({string.Join(", ", requiredGenres)}).";
                return false;
            }
        }

        if (source.MinimumRating is not null)
        {
            var rating = metadata?.Rating ?? entry.Rating;
            if (rating is null || rating.Value < source.MinimumRating.Value)
            {
                reason = $"Below minimum rating ({source.MinimumRating:0.0}).";
                return false;
            }
        }

        if (source.MinimumYear is not null)
        {
            var year = metadata?.Year ?? entry.Year;
            if (year is null || year.Value < source.MinimumYear.Value)
            {
                reason = $"Below minimum year ({source.MinimumYear}).";
                return false;
            }
        }

        if (source.MaximumAgeDays is not null)
        {
            var releaseDate = entry.ReleaseDateUtc ?? (metadata?.Year is null
                ? null
                : new DateTimeOffset(metadata.Year.Value, 12, 31, 0, 0, 0, TimeSpan.Zero));
            if (releaseDate is null)
            {
                reason = "Missing release date for maximum age filter.";
                return false;
            }

            if ((now - releaseDate.Value).TotalDays > source.MaximumAgeDays.Value)
            {
                reason = $"Older than maximum age ({source.MaximumAgeDays} days).";
                return false;
            }
        }

        var allowedCertifications = SplitCsv(source.AllowedCertifications);
        if (allowedCertifications.Length > 0)
        {
            var cert = entry.Certification?.Trim();
            if (string.IsNullOrWhiteSpace(cert))
            {
                reason = "Missing certification for certification filter.";
                return false;
            }

            if (!allowedCertifications.Any(item => cert.Equals(item, StringComparison.OrdinalIgnoreCase)))
            {
                reason = $"Certification '{cert}' not allowed.";
                return false;
            }
        }

        if (!string.Equals(source.Audience, "any", StringComparison.OrdinalIgnoreCase))
        {
            var audience = entry.Audience ?? GuessAudience(entry.Certification, entry.GenresCsv);
            if (!string.Equals(source.Audience, audience, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Audience '{audience}' did not match required audience '{source.Audience}'.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static LibraryItem? ResolveTargetLibrary(IntakeSourceItem source, IReadOnlyList<LibraryItem> libraries)
    {
        var mediaType = source.MediaType == "tv" ? "tv" : "movies";
        var candidates = libraries.Where(item => string.Equals(item.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(source.LibraryId))
        {
            var exact = candidates.FirstOrDefault(item => string.Equals(item.Id, source.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return candidates[0];
    }

    /// <summary>
    /// Whether this title is already in the catalogue, and if so which entry it
    /// is. The repositories answer it with the same rules their AddAsync uses
    /// to decide it has seen a title before, so "already there" and "adding it
    /// would land on an existing row" cannot disagree.
    /// </summary>
    private Task<string?> FindExistingIdAsync(
        string mediaType,
        string title,
        int? year,
        string? imdbId,
        string? metadataProvider,
        string? metadataProviderId,
        CancellationToken cancellationToken)
        => mediaType == "tv"
            ? seriesCatalogRepository.FindExistingIdAsync(title, year, imdbId, metadataProvider, metadataProviderId, cancellationToken)
            : movieCatalogRepository.FindExistingIdAsync(title, year, imdbId, metadataProvider, metadataProviderId, cancellationToken);

    private static string BuildKey(string title, int? year, string? imdbId)
    {
        var normalizedTitle = title.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            return $"imdb:{imdbId.Trim().ToLowerInvariant()}";
        }

        return $"title:{normalizedTitle}:{year?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
    }

    private static bool TryResolveTitle(IntakeEntry entry, out string title)
    {
        title = entry.Title?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(title);
    }

    private static string ResolveTmdbListId(string feedUrl)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            return string.Empty;
        }

        var trimmed = feedUrl.Trim();
        if (trimmed.All(char.IsDigit))
        {
            return trimmed;
        }

        var match = TmdbListIdRegex.Match(trimmed);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string ResolveImdbCsvUrl(string feedUrl)
    {
        var trimmed = feedUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (uri.AbsolutePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return uri.ToString();
            }

            var id = ImdbListIdRegex.Match(uri.ToString());
            if (id.Success)
            {
                return $"https://www.imdb.com/list/{id.Value}/export";
            }
        }

        var inlineId = ImdbListIdRegex.Match(trimmed);
        if (inlineId.Success)
        {
            return $"https://www.imdb.com/list/{inlineId.Value}/export";
        }

        throw new InvalidOperationException("IMDb source requires a list id (ls...) or an IMDb export URL.");
    }

    private static string ResolveTraktRssUrl(string feedUrl)
    {
        var trimmed = feedUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (uri.Host.Contains("trakt.tv", StringComparison.OrdinalIgnoreCase))
            {
                var path = uri.AbsolutePath.TrimEnd('/');
                if (path.EndsWith(".rss", StringComparison.OrdinalIgnoreCase))
                {
                    return uri.ToString();
                }

                if (path.Contains("/lists/", StringComparison.OrdinalIgnoreCase))
                {
                    return $"https://trakt.tv{path}.rss";
                }

                if (path.Contains("/watchlist", StringComparison.OrdinalIgnoreCase))
                {
                    return $"https://trakt.tv{path}.rss";
                }
            }

            return uri.ToString();
        }

        return $"https://trakt.tv/users/{Uri.EscapeDataString(trimmed)}/watchlist.rss";
    }

    private static IReadOnlyList<IntakeEntry> ParseRss(string xml, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return [];
        }

        var document = XDocument.Parse(xml);
        var items = document.Descendants("item").ToArray();
        var result = new List<IntakeEntry>();
        foreach (var item in items)
        {
            var title = item.Element("title")?.Value?.Trim();
            var description = item.Element("description")?.Value;
            var year = ParseYear(title) ?? ParseYear(description);
            var genres = item.Elements().FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "category", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
            var published = ParseDate(item.Element("pubDate")?.Value);
            result.Add(new IntakeEntry(
                Title: CleanTitle(title),
                Year: year,
                MediaType: mediaType,
                ImdbId: NormalizeImdbId(title),
                GenresCsv: genres,
                Rating: null,
                ReleaseDateUtc: published,
                Certification: null,
                Audience: GuessAudience(null, genres)));
        }

        return result;
    }

    private static IReadOnlyList<IntakeEntry> ParsePlainList(string body, string mediaType)
    {
        var entries = new List<IntakeEntry>();
        foreach (var line in (body ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var year = ParseYear(trimmed);
            entries.Add(new IntakeEntry(
                Title: CleanTitle(trimmed),
                Year: year,
                MediaType: mediaType,
                ImdbId: NormalizeImdbId(trimmed),
                GenresCsv: string.Empty,
                Rating: null,
                ReleaseDateUtc: null,
                Certification: null,
                Audience: "any"));
        }

        return entries;
    }

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var buffer = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    buffer.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                values.Add(buffer.ToString());
                buffer.Clear();
                continue;
            }

            buffer.Append(c);
        }

        values.Add(buffer.ToString());
        return values.ToArray();
    }

    private static int FindIndex(string[] header, string name)
        => Array.FindIndex(header, value => string.Equals(value?.Trim(), name, StringComparison.OrdinalIgnoreCase));

    private static string? ValueAt(string[] values, int index)
        => index >= 0 && index < values.Length ? values[index]?.Trim() : null;

    private static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static double? ParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static int? ParseYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = YearRegex.Match(value);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, out var year) ? year : null;
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return date.ToUniversalTime();
        }

        return null;
    }

    private static string CleanTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var title = value.Trim();
        title = YearRegex.Replace(title, string.Empty).Trim();
        return title.Trim(['-', '|', ':', ' ']);
    }

    private static string NormalizeImdbId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var match = Regex.Match(value, @"tt\d{4,}", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToLowerInvariant() : string.Empty;
    }

    private static string GetMatchConfidence(IntakeEntry entry)
        => !string.IsNullOrWhiteSpace(entry.ImdbId) ? "high"
            : entry.Year is not null ? "medium"
            : "low";

    private static string NormalizeMediaType(string? value, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "tv" or "show" or "series" => "tv",
            "movie" or "movies" => "movies",
            _ => fallback
        };
    }

    private static string[] SplitCsv(string csv)
        => (csv ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static HashSet<string> MergeGenres(string entryGenres, IReadOnlyList<string>? metadataGenres)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var genre in SplitCsv(entryGenres))
        {
            set.Add(genre);
        }

        if (metadataGenres is not null)
        {
            foreach (var genre in metadataGenres.Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                set.Add(genre.Trim());
            }
        }

        return set;
    }

    private static bool LooksLikeXml(string value)
        => value.TrimStart().StartsWith("<", StringComparison.Ordinal);

    private static string GuessAudience(string? certification, string? genres)
    {
        var cert = certification?.ToLowerInvariant() ?? string.Empty;
        if (cert.Contains("nc-17", StringComparison.Ordinal) ||
            cert.Contains("tv-ma", StringComparison.Ordinal) ||
            cert.Equals("r", StringComparison.Ordinal))
        {
            return "adult";
        }

        var genreSet = SplitCsv(genres ?? string.Empty);
        if (genreSet.Any(item => item.Contains("family", StringComparison.OrdinalIgnoreCase) ||
                                 item.Contains("animation", StringComparison.OrdinalIgnoreCase) ||
                                 item.Contains("children", StringComparison.OrdinalIgnoreCase)))
        {
            return "kids";
        }

        return "any";
    }

    private static void Increment(IDictionary<string, int> counts, string reason)
    {
        if (counts.TryGetValue(reason, out var current))
        {
            counts[reason] = current + 1;
            return;
        }

        counts[reason] = 1;
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadNumber(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static int? ReadInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? ReadNestedString(JsonElement element, string parent, string property)
        => element.TryGetProperty(parent, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadString(nested, property)
            : null;

    private static bool ReadBoolean(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True;

    private sealed record IntakeSyncPayload(string SourceId, bool Manual);

    private sealed record IntakeEntry(
        string? Title,
        int? Year,
        string MediaType,
        string? ImdbId,
        string GenresCsv,
        double? Rating,
        DateTimeOffset? ReleaseDateUtc,
        string? Certification,
        string? Audience,
        string? ProviderId = null);
}
