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
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Intake;

public sealed class IntakeSyncService(
    IPlatformSettingsRepository platformSettingsRepository,
    IJobScheduler jobScheduler,
    IJobQueueRepository jobQueueRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IMetadataProvider metadataProvider,
    IMediaDecisionService mediaDecisionService,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    ILogger<IntakeSyncService> logger)
    : IIntakeSyncService
{
    private static readonly Regex ImdbListIdRegex = new(@"ls\d{4,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TmdbListIdRegex = new(@"(?:^|/)(\d{3,})(?:$|[/?#])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YearRegex = new(@"(?:\(|\b)(19\d{2}|20\d{2}|2100)(?:\)|\b)", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> PlanDueSyncJobsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var sources = await platformSettingsRepository.ListIntakeSourcesAsync(cancellationToken);
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

    public async Task<IntakeSyncRunResult> RunAsync(string sourceId, string? relatedJobId, bool manual, CancellationToken cancellationToken)
    {
        var source = await platformSettingsRepository.GetIntakeSourceAsync(sourceId, cancellationToken);
        if (source is null)
        {
            throw new InvalidOperationException("Intake source not found.");
        }

        var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
        var targetLibrary = ResolveTargetLibrary(source, libraries);
        if (targetLibrary is null)
        {
            var failureSummary = "No compatible target library exists for this source media type.";
            await platformSettingsRepository.RecordIntakeSourceSyncResultAsync(source.Id, timeProvider.GetUtcNow(), "error", failureSummary, cancellationToken);
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
        try
        {
            entries = await FetchEntriesAsync(source, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Intake source {SourceId} fetch failed.", source.Id);
            var failureSummary = $"Provider fetch failed: {ex.Message}";
            await platformSettingsRepository.RecordIntakeSourceSyncResultAsync(source.Id, timeProvider.GetUtcNow(), "error", failureSummary, cancellationToken);
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

        var movieIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seriesIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (source.MediaType == "tv")
        {
            foreach (var item in await seriesCatalogRepository.ListAsync(cancellationToken))
            {
                IndexTitle(seriesIndex, item.Id, item.Title, item.StartYear, item.ImdbId);
                knownIds.Add(item.Id);
            }
        }
        else
        {
            foreach (var item in await movieCatalogRepository.ListAsync(cancellationToken))
            {
                IndexTitle(movieIndex, item.Id, item.Title, item.ReleaseYear, item.ImdbId);
                knownIds.Add(item.Id);
            }
        }

        var skipReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
                var duplicateKey = BuildKey(resolvedTitle!, resolvedYear, resolvedImdb);
                var mediaType = source.MediaType == "tv" ? "tv" : "movies";

                var existingId = mediaType == "tv"
                    ? Lookup(seriesIndex, duplicateKey)
                    : Lookup(movieIndex, duplicateKey);

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
                        IndexTitle(seriesIndex, created.Id, created.Title, created.StartYear, created.ImdbId);
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
                        IndexTitle(movieIndex, created.Id, created.Title, created.ReleaseYear, created.ImdbId);
                    }

                    if (knownIds.Add(existingId!))
                    {
                        added++;
                    }
                    else
                    {
                        duplicates++;
                    }
                }
                else
                {
                    duplicates++;
                }

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

                shouldRequestSearch = shouldRequestSearch || source.SearchOnAdd;
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

        await platformSettingsRepository.RecordIntakeSourceSyncResultAsync(source.Id, timeProvider.GetUtcNow(), status, summary, cancellationToken);
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
            "imdb" => await FetchImdbListAsync(source, mediaType, cancellationToken),
            "trakt" => await FetchTraktListAsync(source, mediaType, cancellationToken),
            "rss" or "letterboxd" or "url-list" => await FetchGenericListAsync(source, mediaType, cancellationToken),
            _ => await FetchGenericListAsync(source, mediaType, cancellationToken)
        };
    }

    private async Task<IReadOnlyList<IntakeEntry>> FetchTmdbListAsync(IntakeSourceItem source, string mediaType, CancellationToken cancellationToken)
    {
        var apiKey = await platformSettingsRepository.GetMetadataProviderSecretAsync("tmdb", cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("TMDB API key is not configured.");
        }

        var listId = ResolveTmdbListId(source.FeedUrl);
        if (string.IsNullOrWhiteSpace(listId))
        {
            throw new InvalidOperationException("TMDB source requires a list id or TMDB list URL.");
        }

        var url = $"https://api.themoviedb.org/3/list/{Uri.EscapeDataString(listId)}?api_key={Uri.EscapeDataString(apiKey)}";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
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

    private async Task<IReadOnlyList<IntakeEntry>> FetchImdbListAsync(IntakeSourceItem source, string mediaType, CancellationToken cancellationToken)
    {
        var url = ResolveImdbCsvUrl(source.FeedUrl);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
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
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var xml = await client.GetStringAsync(rssUrl, cancellationToken);
        return ParseRss(xml, mediaType);
    }

    private async Task<IReadOnlyList<IntakeEntry>> FetchGenericListAsync(IntakeSourceItem source, string mediaType, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(source.FeedUrl, UriKind.Absolute, out var uri))
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var body = await client.GetStringAsync(uri, cancellationToken);
            if (LooksLikeXml(body))
            {
                return ParseRss(body, mediaType);
            }

            return ParsePlainList(body, mediaType);
        }

        return ParsePlainList(source.FeedUrl, mediaType);
    }

    private async Task<MetadataSearchResult?> ResolveMetadataAsync(
        IntakeSourceItem source,
        IntakeEntry entry,
        string title,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(entry.ImdbId))
        {
            var byImdb = await metadataProvider.SearchAsync(
                new MetadataLookupRequest(entry.ImdbId, source.MediaType, entry.Year, entry.ImdbId),
                cancellationToken);
            if (byImdb.Count > 0)
            {
                return byImdb[0];
            }
        }

        var matches = await metadataProvider.SearchAsync(
            new MetadataLookupRequest(title, source.MediaType, entry.Year, null),
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

    private static void IndexTitle(IDictionary<string, string> index, string id, string title, int? year, string? imdbId)
    {
        var key = BuildKey(title, year, imdbId);
        if (!index.ContainsKey(key))
        {
            index[key] = id;
        }
    }

    private static string? Lookup(IDictionary<string, string> index, string key)
        => index.TryGetValue(key, out var value) ? value : null;

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
        string? Audience);
}
