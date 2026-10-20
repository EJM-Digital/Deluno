using System.Globalization;
using System.Data.Common;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Contracts;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Integrations.Metadata;

public sealed partial class TmdbMetadataProvider(
    HttpClient httpClient,
    IConfiguration configuration,
    IPlatformSettingsRepository platformRepository,
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IOptions<StoragePathOptions> storageOptions,
    TimeProvider timeProvider,
    IIntegrationResiliencePolicy resiliencePolicy,
    IOutboundRequestThrottle outboundRequestThrottle,
    ILogger<TmdbMetadataProvider> logger,
    MetadataServiceHealth? serviceHealth = null)
    : IMetadataProvider
{
    /// <summary>
    /// The longest a metadata lookup will wait for its turn.
    ///
    /// Short, because unlike an indexer search there is nothing to protect: a
    /// metadata refresh that does not happen this minute happens next minute,
    /// and the backfill re-queues it. Better to hand the lease back than to sit
    /// on it.
    /// </summary>
    private static readonly TimeSpan MaxMetadataThrottleWait = TimeSpan.FromSeconds(10);

    private const string ProviderName = "tmdb";
    private const string BrokerProviderName = "deluno";
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _artworkRootPath = Path.Combine(
        Path.GetFullPath(storageOptions.Value.DataRoot),
        "artwork-cache");
    /// <summary>
    /// One lock for the whole artwork cache in this process: the API, the
    /// cleanup job and every search share it, so none can delete a file another
    /// is writing (#1089). It guards only short database and file steps.
    /// </summary>
    private static readonly SemaphoreSlim ArtworkCacheGate = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> ArtworkDownloadLocks = new();

    /// <summary>The longest one artwork download may take before it is given up.</summary>
    private static readonly TimeSpan ArtworkDownloadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The most a search will spend saving its results' artwork.</summary>
    private static readonly TimeSpan ArtworkSearchDeadline = TimeSpan.FromSeconds(30);
    private const int ArtworkSearchConcurrency = 6;
    private const int MaxArtworkBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Process-wide, not per instance: this provider is a typed HttpClient and is
    /// rebuilt for nearly every request, so anything kept on it was gone before
    /// the status endpoint could report it (#1090).
    /// </summary>
    private readonly MetadataServiceHealth _health = serviceHealth ?? new MetadataServiceHealth(timeProvider);

    private static readonly AsyncLocal<FailureScope?> CurrentScope = new();

    /// <summary>
    /// The last boundary failure from a live metadata operation. Empty search
    /// results remain valid data; this property distinguishes them from an
    /// unavailable, unauthorized, or malformed provider response.
    /// </summary>
    public IntegrationFailure? LastFailure => Volatile.Read(ref _lastFailure);

    // What this instance's own calls last hit, exactly as before: a caller that
    // has just made one call reads the answer to that call (a 404 means the
    // record is gone; a 503 means it may not be). The process-wide record in
    // _health is what the status and the screens read; it cannot stand in for
    // this, because another lane's call would overwrite it (#1090).
    private IntegrationFailure? _lastFailure;

    public IMetadataFailureScope BeginFailureScope()
    {
        var scope = new FailureScope(CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    private sealed class FailureScope(FailureScope? outer) : IMetadataFailureScope
    {
        private IntegrationFailure? _failure;

        public IntegrationFailure? Failure => Volatile.Read(ref _failure);

        public void Report(IntegrationFailure failure)
        {
            Interlocked.Exchange(ref _failure, failure);
            outer?.Report(failure);
        }

        public void Dispose() => CurrentScope.Value = outer;
    }

    private static void ReportToScope(IntegrationFailure failure) => CurrentScope.Value?.Report(failure);

    /// <summary>
    /// Only a service that is not answering counts: a 404 for one title is an
    /// answer, and must not make the whole service look down.
    /// </summary>
    private static bool CountsAsServiceFailure(IntegrationFailure failure)
        => failure.IsTransient
           || failure.Kind is IntegrationFailureKind.Authentication or IntegrationFailureKind.Configuration;

    public async Task ClearServiceTroubleAsync(CancellationToken cancellationToken)
    {
        _health.Reset();
        Interlocked.Exchange(ref _lastFailure, null);
        foreach (var source in MetadataServiceHealth.Sources)
        {
            await resiliencePolicy.CloseCircuitsAsync("metadata", source, cancellationToken);
        }
    }

    public async Task<MetadataProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var config = await GetMetadataConfigurationAsync(cancellationToken);
        var directConfigured = !string.IsNullOrWhiteSpace(config.TmdbApiKey);
        var brokerConfigured = !string.IsNullOrWhiteSpace(config.BrokerUrl);
        var sources = BuildSourceStatuses(config, directConfigured, brokerConfigured);
        var lastFailure = _health.LastFailure;

        var status = config.ProviderMode switch
        {
            "broker" => new MetadataProviderStatus(
                BrokerProviderName,
                brokerConfigured,
                brokerConfigured ? "broker" : "unconfigured",
                brokerConfigured
                    ? "Deluno's managed metadata service is ready for title matching."
                    : "Title matching has not been configured for this Deluno installation yet.",
                sources,
                lastFailure),
            "hybrid" => new MetadataProviderStatus(
                BrokerProviderName,
                brokerConfigured || directConfigured,
                brokerConfigured ? "hybrid" : directConfigured ? "direct-fallback" : "unconfigured",
                brokerConfigured
                    ? "Deluno's managed metadata service is ready for title matching."
                    : directConfigured
                        ? "Deluno's metadata service is ready using this installation's configured fallback."
                        : "Title matching has not been configured for this Deluno installation yet.",
                sources,
                lastFailure),
            _ => new MetadataProviderStatus(
                ProviderName,
                directConfigured,
                directConfigured ? "direct" : "unconfigured",
                directConfigured
                    ? string.IsNullOrWhiteSpace(config.OmdbApiKey)
                        ? "Deluno's title-matching service is ready."
                        : "Deluno's title-matching and ratings service is ready."
                    : "Title matching has not been configured for this Deluno installation yet.",
                sources,
                lastFailure)
        };

        // Ready is only true of the settings. When the service last failed, say
        // so in plain words instead of "ready" (#1090).
        return lastFailure is not null && status.IsConfigured
            ? status with { Message = MetadataFailureWords.Describe(lastFailure) }
            : status;
    }

    public async Task<MetadataProviderStatus> GetDirectStatusAsync(CancellationToken cancellationToken)
    {
        var apiKey = await GetApiKeyAsync(cancellationToken);
        var omdbApiKey = await GetOmdbApiKeyAsync(cancellationToken);
        var configured = !string.IsNullOrWhiteSpace(apiKey);
        return new MetadataProviderStatus(
            ProviderName,
            configured,
            configured ? "direct" : "unconfigured",
            configured
                ? string.IsNullOrWhiteSpace(omdbApiKey)
                    ? "Deluno's title-matching service is ready."
                    : "Deluno's title-matching and ratings service is ready."
                : "Title matching has not been configured for this Deluno installation yet.",
            BuildSourceStatuses(
                new MetadataProviderConfiguration("direct", null, apiKey, omdbApiKey),
                configured,
                false),
            LastFailure);
    }

    /// <summary>A fixed, always-findable title, so a probe result reflects reachability, not whether a made-up query happens to match anything.</summary>
    private static readonly MetadataLookupRequest ReachabilityProbeRequest = new("Inception", "movies", null, null);
    private const string ReachabilityProbeQuery = "Inception";

    public async Task<MetadataProviderStatus> CheckReachabilityAsync(CancellationToken cancellationToken)
    {
        var config = await GetMetadataConfigurationAsync(cancellationToken);

        if (config.ProviderMode is "broker" or "hybrid" && !string.IsNullOrWhiteSpace(config.BrokerUrl))
        {
            var brokerResult = await TryBrokerSearchAsync(
                config.BrokerUrl, "movies", ReachabilityProbeRequest, ReachabilityProbeQuery, cancellationToken);

            // A response (even an empty one) means the broker answered. Only
            // fall through to a direct-TMDb probe when hybrid mode has one
            // configured to fall back to -- broker-only mode reports exactly
            // what it found, the same way a real search would.
            if (brokerResult is not null || config.ProviderMode == "broker" || string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                return await GetStatusAsync(cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            await ProbeDirectTmdbAsync(config.TmdbApiKey, cancellationToken);
        }

        return await GetStatusAsync(cancellationToken);
    }

    /// <summary>
    /// The lightest reachable TMDb endpoint there is -- no query, no cache
    /// write, no artwork fetch. Its only job is to make <see cref="LastFailure"/>
    /// reflect whether TMDb answered just now.
    /// </summary>
    private Task ProbeDirectTmdbAsync(string apiKey, CancellationToken cancellationToken)
        => GetJsonWithResilienceAsync<JsonElement?>(
            $"https://api.themoviedb.org/3/configuration?api_key={Uri.EscapeDataString(apiKey)}",
            "metadata:tmdb:reachability",
            "metadata.tmdb.reachability",
            cancellationToken);

    private Task<IReadOnlyList<MetadataSearchResult>> SearchTmdbAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken)
        => !string.IsNullOrWhiteSpace(request.IdSource)
            ? SearchByExternalIdAsync(request, cancellationToken)
            : SearchWithTypedYearAsync(request, SearchByQueryAsync, cancellationToken);

    /// <summary>
    /// A prefixed id (tmdb:/imdb:/tvdb:) typed into the add-title search box.
    /// This is an identity assertion, not a search hint: it returns the one
    /// matching title, or nothing at all - never a fuzzy list of near
    /// matches for an id that does not resolve.
    /// </summary>
    private async Task<IReadOnlyList<MetadataSearchResult>> SearchByExternalIdAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken)
    {
        var externalId = request.ProviderId?.Trim();
        if (string.IsNullOrWhiteSpace(externalId))
        {
            return [];
        }

        var config = await GetMetadataConfigurationAsync(cancellationToken);
        var apiKey = config.TmdbApiKey ?? await GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        var mediaType = NormalizeMediaType(request.MediaType);

        if (string.Equals(request.IdSource, "tmdb", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(externalId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
            {
                return [];
            }

            var exact = await GetDetailsByIdAsync(tmdbId, mediaType, apiKey, config.Language, config.CertificationCountry, cancellationToken);
            return exact is null ? [] : await LocalizeArtworkAsync([exact], cancellationToken);
        }

        var externalSource = string.Equals(request.IdSource, "tvdb", StringComparison.OrdinalIgnoreCase)
            ? "tvdb_id"
            : "imdb_id";
        var url = $"https://api.themoviedb.org/3/find/{Uri.EscapeDataString(externalId)}?api_key={Uri.EscapeDataString(apiKey)}&external_source={externalSource}";
        var found = await GetJsonWithResilienceAsync<TmdbFindResponse>(
            url,
            "metadata:tmdb:find",
            "metadata.tmdb.find",
            cancellationToken);

        // The find endpoint answers whichever bucket matches, regardless of
        // what mediaType was asked for - a tvdb id is always a TV show, and
        // an imdb id can be either, so the movie/tv media type the caller
        // sent is only a preference when both buckets somehow have a hit.
        var tvHit = found?.TvResults?.FirstOrDefault();
        var movieHit = found?.MovieResults?.FirstOrDefault();
        var (hit, resolvedMediaType) = mediaType == "tv"
            ? (tvHit is not null ? (tvHit, "tv") : (movieHit, "movies"))
            : (movieHit is not null ? (movieHit, "movies") : (tvHit, "tv"));
        if (hit is null)
        {
            return [];
        }

        var detail = await GetDetailsByIdAsync(hit.Id, resolvedMediaType, apiKey, config.Language, config.CertificationCountry, cancellationToken);
        return detail is null ? [] : await LocalizeArtworkAsync([detail], cancellationToken);
    }

    /// <summary>
    /// Runs a title search with any year the person typed moved into the
    /// year filter, and falls back to the query as typed when that finds
    /// nothing — so a title that ends in a year still finds itself.
    /// </summary>
    private async Task<IReadOnlyList<MetadataSearchResult>> SearchWithTypedYearAsync(
        MetadataLookupRequest request,
        Func<MetadataLookupRequest, CancellationToken, Task<IReadOnlyList<MetadataSearchResult>>> search,
        CancellationToken cancellationToken)
    {
        if (request.Year is null &&
            string.IsNullOrWhiteSpace(request.ProviderId) &&
            MetadataQueryYear.TrySplit(request.Query, timeProvider.GetUtcNow().Year, out var title, out var year))
        {
            var byYear = await search(request with { Query = title, Year = year }, cancellationToken);
            if (byYear.Count > 0)
            {
                return byYear;
            }
        }

        return await search(request, cancellationToken);
    }

    private async Task<IReadOnlyList<MetadataSearchResult>> SearchByQueryAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken)
    {
        var query = request.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var config = await GetMetadataConfigurationAsync(cancellationToken);
        var mediaType = NormalizeMediaType(request.MediaType);
        var cacheKey = BuildSearchCacheKey(config.ProviderMode, mediaType, query, request.Year, request.ProviderId, config.Language);
        var cached = await TryReadSearchCacheAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return await LocalizeArtworkAsync(cached, cancellationToken);
        }

        if (config.ProviderMode is "broker" or "hybrid" && !string.IsNullOrWhiteSpace(config.BrokerUrl))
        {
            logger.LogDebug("Attempting metadata search via broker for query: {Query}", query);
            var brokerResults = await TryBrokerSearchAsync(config.BrokerUrl, mediaType, request, query, cancellationToken);
            if (brokerResults is { Count: > 0 })
            {
                var localizedBrokerResults = await LocalizeArtworkAsync(brokerResults, cancellationToken);
                logger.LogDebug("Broker search returned {ResultCount} results for query: {Query}", brokerResults.Count, query);
                await WriteSearchCacheAsync(cacheKey, mediaType, query, localizedBrokerResults, cancellationToken);
                return localizedBrokerResults;
            }

            if (config.ProviderMode == "broker")
            {
                logger.LogWarning("Broker search returned no results and broker-only mode is active for query: {Query}", query);

                // The gateway did not answer (null, not an empty list): an
                // older answer to this same search beats "nothing found" (#1090).
                if (brokerResults is null)
                {
                    var brokerStale = await TryReadStaleCacheAsync(cacheKey, cancellationToken);
                    if (brokerStale is not null)
                    {
                        return await LocalizeArtworkAsync(brokerStale, cancellationToken);
                    }
                }

                return [];
            }

            logger.LogInformation("Broker search returned no results, falling back to direct TMDb search for query: {Query}", query);
            DelunoObservability.MetadataBrokerFallbacks.Add(1, [new KeyValuePair<string, object?>("query", query)]);
        }

        if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            var omdbFallback = await SearchOmdbFallbackAsync(request, mediaType, query, config.OmdbApiKey, cancellationToken);
            if (omdbFallback.Count > 0)
            {
                await WriteSearchCacheAsync(cacheKey, mediaType, query, omdbFallback, cancellationToken);
                return omdbFallback;
            }

            var stale = await TryReadStaleCacheAsync(cacheKey, cancellationToken);
            return stale is null ? [] : await LocalizeArtworkAsync(stale, cancellationToken);
        }

        var live = await SearchDirectAsync(
            request,
            config.TmdbApiKey,
            cacheKey,
            mediaType,
            query,
            config.Language,
            config.CertificationCountry,
            cancellationToken);
        if (live.Count > 0)
        {
            return await LocalizeArtworkAsync(live, cancellationToken);
        }

        var omdbFallbackAfterTmdb = await SearchOmdbFallbackAsync(request, mediaType, query, config.OmdbApiKey, cancellationToken);
        if (omdbFallbackAfterTmdb.Count > 0)
        {
            await WriteSearchCacheAsync(cacheKey, mediaType, query, omdbFallbackAfterTmdb, cancellationToken);
            return omdbFallbackAfterTmdb;
        }

        var staleFallback = await TryReadStaleCacheAsync(cacheKey, cancellationToken);
        return staleFallback is null ? [] : await LocalizeArtworkAsync(staleFallback, cancellationToken);
    }

    private async Task<MetadataProviderRecordLookup> ResolveTmdbProviderRecordAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken)
    {
        var providerIdText = request.ProviderId?.Trim();
        if (string.IsNullOrWhiteSpace(providerIdText) ||
            !int.TryParse(providerIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var providerId))
        {
            return new MetadataProviderRecordLookup(
                MetadataProviderRecordStatus.Unavailable,
                ProviderName,
                providerIdText ?? string.Empty,
                Failure: IntegrationFailureFactory.FromLegacy(
                    "metadata",
                    ProviderName,
                    "TMDb",
                    "metadata.provider.resolve",
                    "configuration",
                    "The stored TMDb id is not a valid numeric identifier."));
        }

        var mediaType = NormalizeMediaType(request.MediaType);
        var config = await GetMetadataConfigurationAsync(cancellationToken);

        if (config.ProviderMode is "broker" or "hybrid" && !string.IsNullOrWhiteSpace(config.BrokerUrl))
        {
            var brokerLookup = await ResolveBrokerProviderRecordAsync(
                config.BrokerUrl,
                mediaType,
                request,
                providerIdText,
                cancellationToken);
            if (brokerLookup.Status is MetadataProviderRecordStatus.Found or MetadataProviderRecordStatus.Missing ||
                config.ProviderMode == "broker")
            {
                return brokerLookup;
            }
        }

        var apiKey = config.TmdbApiKey ?? await GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new MetadataProviderRecordLookup(
                MetadataProviderRecordStatus.Unavailable,
                ProviderName,
                providerIdText,
                Failure: IntegrationFailureFactory.FromLegacy(
                    "metadata",
                    ProviderName,
                    "TMDb",
                    "metadata.provider.resolve",
                    "configuration",
                    "TMDb is not configured for exact provider-id lookup."));
        }

        var result = await GetDetailsByIdAsync(providerId, mediaType, apiKey, config.Language, config.CertificationCountry, cancellationToken);
        if (result is not null)
        {
            var localized = await LocalizeArtworkAsync([result], cancellationToken);
            return new MetadataProviderRecordLookup(
                MetadataProviderRecordStatus.Found,
                ProviderName,
                providerIdText,
                localized[0]);
        }

        var failure = LastFailure;
        return new MetadataProviderRecordLookup(
            failure?.HttpStatus == 404
                ? MetadataProviderRecordStatus.Missing
                : MetadataProviderRecordStatus.Unavailable,
            ProviderName,
            providerIdText,
            Failure: failure);
    }

    public Task<IReadOnlyList<MetadataSearchResult>> SearchDirectAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken)
        => SearchWithTypedYearAsync(request, SearchDirectByQueryAsync, cancellationToken);

    private async Task<IReadOnlyList<MetadataSearchResult>> SearchDirectByQueryAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken)
    {
        var query = request.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var apiKey = await GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        var (language, certificationCountry) = await GetLocalizationAsync(cancellationToken);
        var mediaType = NormalizeMediaType(request.MediaType);
        var cacheKey = BuildSearchCacheKey($"{ProviderName}:direct", mediaType, query, request.Year, request.ProviderId, language);
        var cached = await TryReadSearchCacheAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return await LocalizeArtworkAsync(cached, cancellationToken);
        }

        return await SearchDirectAsync(request, apiKey, cacheKey, mediaType, query, language, certificationCountry, cancellationToken);
    }

    private async Task<IReadOnlyList<MetadataSearchResult>> SearchDirectAsync(
        MetadataLookupRequest request,
        string apiKey,
        string cacheKey,
        string mediaType,
        string query,
        string? language,
        string certificationCountry,
        CancellationToken cancellationToken)
    {

        if (!string.IsNullOrWhiteSpace(request.ProviderId) &&
            int.TryParse(request.ProviderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var providerId))
        {
            var exact = await GetDetailsByIdAsync(providerId, mediaType, apiKey, language, certificationCountry, cancellationToken);
            if (exact is not null)
            {
                var result = await LocalizeArtworkAsync(new[] { exact }, cancellationToken);
                await WriteSearchCacheAsync(cacheKey, mediaType, query, result, cancellationToken);
                return result;
            }

            // A supplied id is an identity assertion, not a search hint. If it
            // no longer exists, fuzzy matching here can silently relink the
            // title to an unrelated result with a similar name.
            return [];
        }

        var endpoint = mediaType == "tv" ? "search/tv" : "search/movie";
        var url =
            $"https://api.themoviedb.org/3/{endpoint}?api_key={Uri.EscapeDataString(apiKey)}&query={Uri.EscapeDataString(query)}&include_adult=false";
        if (!string.IsNullOrWhiteSpace(language))
        {
            url += $"&language={Uri.EscapeDataString(language)}";
        }

        if (request.Year is > 0)
        {
            url += mediaType == "tv"
                ? $"&first_air_date_year={request.Year.Value.ToString(CultureInfo.InvariantCulture)}"
                : $"&year={request.Year.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        var response = await GetJsonWithResilienceAsync<TmdbSearchResponse>(
            url,
            $"metadata:tmdb:search:{mediaType}",
            "metadata.tmdb.search",
            cancellationToken);
        var items = response?.Results?
            .Where(item => !string.IsNullOrWhiteSpace(item.Title ?? item.Name))
            .Take(12)
            .ToArray() ?? [];

        var results = new List<MetadataSearchResult>(items.Length);
        foreach (var item in items)
        {
            results.Add(await ToResultAsync(item, mediaType, apiKey, cancellationToken));
        }

        var localizedResults = await LocalizeArtworkAsync(results, cancellationToken);
        await WriteSearchCacheAsync(cacheKey, mediaType, query, localizedResults, cancellationToken);
        return localizedResults;
    }

    private async Task<IReadOnlyList<MetadataSearchResult>> SearchOmdbFallbackAsync(
        MetadataLookupRequest request,
        string mediaType,
        string query,
        string? omdbApiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(omdbApiKey))
        {
            return [];
        }

        var type = mediaType == "tv" ? "series" : "movie";
        var url =
            $"https://www.omdbapi.com/?apikey={Uri.EscapeDataString(omdbApiKey)}&s={Uri.EscapeDataString(query)}&type={Uri.EscapeDataString(type)}";
        if (request.Year is > 0)
        {
            url += $"&y={request.Year.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        var response = await GetJsonWithResilienceAsync<OmdbSearchResponse>(
            url,
            $"metadata:omdb:search:{mediaType}",
            "metadata.omdb.search",
            cancellationToken);
        if (response is null || string.Equals(response.Response, "False", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var items = response.Search?
            .Where(item => !string.IsNullOrWhiteSpace(item.ImdbId) && !string.IsNullOrWhiteSpace(item.Title))
            .Take(10)
            .ToArray() ?? [];
        if (items.Length == 0)
        {
            return [];
        }

        var results = new List<MetadataSearchResult>(items.Length);
        foreach (var item in items)
        {
            var detailUrl =
                $"https://www.omdbapi.com/?apikey={Uri.EscapeDataString(omdbApiKey)}&i={Uri.EscapeDataString(item.ImdbId!)}&plot=short&r=json";
            var detail = await GetJsonWithResilienceAsync<OmdbDetailResponse>(
                detailUrl,
                "metadata:omdb:detail",
                "metadata.omdb.detail",
                cancellationToken);

            var ratings = new List<MetadataRatingItem>();
            AddRatingIfPresent(
                ratings,
                "imdb",
                "IMDb",
                ParseFraction(detail?.ImdbRating, 10),
                10,
                ParseVotes(detail?.ImdbVotes),
                BuildImdbUrl(item.ImdbId!),
                "community");

            foreach (var rating in detail?.Ratings ?? [])
            {
                var normalized = NormalizeOmdbSource(rating.Source);
                if (normalized is null)
                {
                    continue;
                }

                var parsed = ParseOmdbRating(rating.Value);
                AddRatingIfPresent(
                    ratings,
                    normalized.Value.Source,
                    normalized.Value.Label,
                    parsed.Score,
                    parsed.MaxScore,
                    null,
                    null,
                    normalized.Value.Kind);
            }

            if (int.TryParse(detail?.Metascore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var metascore))
            {
                AddRatingIfPresent(ratings, "metacritic", "Metacritic", metascore, 100, null, null, "critic");
            }

            var fallback = new MetadataSearchResult(
                Provider: "omdb",
                ProviderId: item.ImdbId!,
                MediaType: mediaType,
                Title: item.Title!,
                OriginalTitle: item.Title,
                Year: TryParseYear(item.Year),
                Overview: detail?.Plot,
                PosterUrl: NormalizeOmdbPoster(item.Poster),
                BackdropUrl: null,
                Rating: ParseFraction(detail?.ImdbRating, 10),
                Ratings: ratings
                    .GroupBy(rating => rating.Source, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray(),
                Genres: SplitGenres(detail?.Genre),
                ImdbId: item.ImdbId,
                ExternalUrl: BuildImdbUrl(item.ImdbId!));
            results.Add(fallback);
        }

        return await LocalizeArtworkAsync(results, cancellationToken);
    }

    private async Task<IReadOnlyList<MetadataSearchResult>> LocalizeArtworkAsync(
        IReadOnlyList<MetadataSearchResult> results,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return results;
        }

        // Up to 24 results a search, each with a poster and a backdrop: fetched
        // a few at a time and within one deadline, so a slow image host cannot
        // hold the whole search. Whatever is not saved by then keeps its remote
        // address and is localised again by the next refresh (#1089).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ArtworkSearchDeadline);
        using var slots = new SemaphoreSlim(ArtworkSearchConcurrency);

        async Task<MetadataSearchResult> LocalizeOneAsync(MetadataSearchResult result)
        {
            try
            {
                await slots.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return result;
            }

            try
            {
                var poster = await CacheArtworkUrlAsync(result.MediaType, result.PosterUrl, deadline.Token);
                var backdrop = await CacheArtworkUrlAsync(result.MediaType, result.BackdropUrl, deadline.Token);
                return result with
                {
                    PosterUrl = poster ?? result.PosterUrl,
                    BackdropUrl = backdrop ?? result.BackdropUrl
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return result;
            }
            finally
            {
                slots.Release();
            }
        }

        return await Task.WhenAll(results.Select(LocalizeOneAsync));
    }

    public async Task<string?> CacheArtworkUrlAsync(
        string mediaType,
        string? remoteUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var remoteUri) ||
            (remoteUri.Scheme != Uri.UriSchemeHttps && remoteUri.Scheme != Uri.UriSchemeHttp))
        {
            return remoteUrl;
        }

        var cacheKey = ComputeSha256(remoteUri.ToString());
        var file = await FindCachedArtworkFileAsync(cacheKey, cancellationToken)
            ?? await DownloadArtworkAsync(cacheKey, mediaType, remoteUri, cancellationToken);
        return file is null ? remoteUrl : $"/api/metadata/artwork/{cacheKey}";
    }

    /// <summary>
    /// The cached file for a key: the one the row names (found by its own name
    /// in this data folder, #1088), or failing that any <c>&lt;key&gt;.*</c>
    /// left in <c>artwork-cache</c> with no row at all.
    /// </summary>
    private async Task<string?> FindCachedArtworkFileAsync(string cacheKey, CancellationToken cancellationToken)
    {
        await ArtworkCacheGate.WaitAsync(cancellationToken);
        try
        {
            var cachedPath = await ReadArtworkLocalPathAsync(cacheKey, cancellationToken);
            if (cachedPath is not null
                && TryGetManagedArtworkPath(cacheKey, cachedPath, out var cachedFile)
                && File.Exists(cachedFile))
            {
                return cachedFile;
            }
        }
        finally
        {
            ArtworkCacheGate.Release();
        }

        try
        {
            if (!Directory.Exists(_artworkRootPath))
            {
                return null;
            }

            return Directory
                .EnumerateFiles(_artworkRootPath, $"{cacheKey}.*")
                .FirstOrDefault(path =>
                    !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileNameWithoutExtension(path), cacheKey, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads one artwork file and saves it, once per key however many
    /// requests want it. Only a real image is kept: the answer must say it is
    /// an image and start with an image's first bytes, so a captive portal's
    /// page or an empty body is never served as a poster. The file is written
    /// beside its final name and renamed, so a half-written poster is never
    /// visible. Returns null when nothing was saved; the failure is written
    /// down so the address is still on the row for a later repair.
    /// </summary>
    private async Task<string?> DownloadArtworkAsync(
        string cacheKey,
        string mediaType,
        Uri remoteUri,
        CancellationToken cancellationToken)
    {
        var flight = ArtworkDownloadLocks.GetOrAdd(
            Path.Combine(_artworkRootPath, cacheKey),
            _ => new SemaphoreSlim(1, 1));
        await flight.WaitAsync(cancellationToken);
        try
        {
            // Another request may have saved it while this one waited.
            var existing = await FindCachedArtworkFileAsync(cacheKey, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            // Artwork comes from the same provider and counts against the same
            // budget. A backfill fetching a poster per title is the larger half
            // of the traffic, not an afterthought.
            if (await outboundRequestThrottle.TryAcquireAsync(
                    remoteUri.Host,
                    OutboundRate.MetadataProviderDefault,
                    MaxMetadataThrottleWait,
                    cancellationToken) is null)
            {
                return null;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ArtworkDownloadTimeout);

                using var response = await httpClient.GetAsync(
                    remoteUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    await RecordArtworkFailureAsync(cacheKey, mediaType, remoteUri);
                    return null;
                }

                var declaredType = response.Content.Headers.ContentType?.MediaType;
                if (declaredType is null
                    || !declaredType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    || response.Content.Headers.ContentLength > MaxArtworkBytes)
                {
                    await RecordArtworkFailureAsync(cacheKey, mediaType, remoteUri);
                    return null;
                }

                var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                var extension = body.Length <= MaxArtworkBytes ? SniffImageExtension(body) : null;
                if (extension is null)
                {
                    await RecordArtworkFailureAsync(cacheKey, mediaType, remoteUri);
                    return null;
                }

                Directory.CreateDirectory(_artworkRootPath);
                var destinationPath = Path.Combine(Path.GetFullPath(_artworkRootPath), $"{cacheKey}{extension}");
                var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, body, cancellationToken);
                    await ArtworkCacheGate.WaitAsync(cancellationToken);
                    try
                    {
                        File.Move(temporaryPath, destinationPath, overwrite: true);
                        await UpsertArtworkCacheAsync(cacheKey, mediaType, remoteUri.ToString(), destinationPath, cancellationToken);
                    }
                    finally
                    {
                        ArtworkCacheGate.Release();
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A leftover temporary file is harmless; it is never served.
                    }
                }

                return destinationPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Includes the download timeout. A failed fetch is remembered so
                // a broken address is not treated as fresh on every request, and
                // a cache-database fault must not hide the provider failure.
                await RecordArtworkFailureAsync(cacheKey, mediaType, remoteUri);
                return null;
            }
        }
        finally
        {
            flight.Release();
        }
    }

    private async Task RecordArtworkFailureAsync(string cacheKey, string mediaType, Uri remoteUri)
    {
        try
        {
            await ArtworkCacheGate.WaitAsync(CancellationToken.None);
            try
            {
                await UpsertArtworkCacheAsync(cacheKey, mediaType, remoteUri.ToString(), null, CancellationToken.None);
            }
            finally
            {
                ArtworkCacheGate.Release();
            }
        }
        catch
        {
            // The remote address is still the useful fallback when the cache
            // database is unavailable.
        }
    }

    /// <summary>The file extension for an image's first bytes, or null when they are not an image.</summary>
    internal static string? SniffImageExtension(ReadOnlySpan<byte> body)
    {
        if (body.Length >= 3 && body[0] == 0xFF && body[1] == 0xD8 && body[2] == 0xFF)
        {
            return ".jpg";
        }

        if (body.Length >= 8 && body[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ".png";
        }

        if (body.Length >= 6 && (body[..6].SequenceEqual("GIF87a"u8) || body[..6].SequenceEqual("GIF89a"u8)))
        {
            return ".gif";
        }

        if (body.Length >= 12 && body[..4].SequenceEqual("RIFF"u8) && body[8..12].SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }

        if (body.Length >= 2 && body[0] == 0x42 && body[1] == 0x4D)
        {
            return ".bmp";
        }

        return null;
    }

    /// <summary>
    /// Removes localized artwork that is no longer referenced by a movie or
    /// show. The caller supplies references from both catalogues because the
    /// cache is shared by them.
    ///
    /// <para>The safety window is intentionally supplied by the scheduler. A
    /// refresh can replace a title's artwork while a browser still has the old
    /// URL, so deleting immediately would turn a normal refresh into a broken
    /// image. Files are also required to be inside the managed cache directory
    /// and to have the cache key as their filename before they are touched.</para>
    /// </summary>
    public async Task<ArtworkCacheCleanupResult> CleanupArtworkCacheAsync(
        IReadOnlySet<string> referencedKeys,
        DateTimeOffset deleteBeforeUtc,
        CancellationToken cancellationToken)
    {
        var referenced = new HashSet<string>(referencedKeys, StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        var deleted = 0;
        var reclaimedBytes = 0L;
        var skippedReferenced = 0;
        var failed = 0;

        await ArtworkCacheGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
                DelunoDatabaseNames.Cache,
                cancellationToken);

            // A search that is out of date is still answered from the cache when
            // the provider is down, so the artwork its results point at stays.
            using (var searchCommand = connection.CreateCommand())
            {
                searchCommand.CommandText = "SELECT result_json FROM search_result_cache;";
                using var searchReader = await searchCommand.ExecuteReaderAsync(cancellationToken);
                while (await searchReader.ReadAsync(cancellationToken))
                {
                    if (searchReader.IsDBNull(0))
                    {
                        continue;
                    }

                    foreach (System.Text.RegularExpressions.Match match in CachedSearchArtworkKey().Matches(searchReader.GetString(0)))
                    {
                        referenced.Add(match.Groups[1].Value.ToLowerInvariant());
                    }
                }
            }

            var candidates = new List<ArtworkCacheEntry>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT cache_key, local_path, fetched_utc
                    FROM artwork_cache
                    WHERE fetched_utc < @deleteBeforeUtc
                    ORDER BY fetched_utc ASC;
                    """;
                AddParameter(command, "@deleteBeforeUtc", deleteBeforeUtc.ToString("O"));

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var fetchedText = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    if (!DateTimeOffset.TryParse(
                            fetchedText,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var fetchedUtc) || fetchedUtc >= deleteBeforeUtc)
                    {
                        // An unknown timestamp is not safe to age out.
                        continue;
                    }

                    candidates.Add(new ArtworkCacheEntry(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        fetchedText,
                        fetchedUtc));
                }
            }

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;

                if (referenced.Contains(candidate.CacheKey))
                {
                    skippedReferenced++;
                    continue;
                }

                var filePath = candidate.LocalPath;
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    if (await DeleteArtworkCacheRowAsync(connection, candidate, cancellationToken))
                    {
                        deleted++;
                    }

                    continue;
                }

                if (!TryGetManagedArtworkPath(candidate.CacheKey, filePath, out var managedPath))
                {
                    failed++;
                    continue;
                }

                long fileSize = 0;
                if (File.Exists(managedPath))
                {
                    try
                    {
                        // A file touched after the database row was fetched is
                        // potentially in use by a concurrent refresh. Leave it
                        // for the next pass rather than racing the writer.
                        if (File.GetLastWriteTimeUtc(managedPath) > deleteBeforeUtc.UtcDateTime)
                        {
                            continue;
                        }

                        fileSize = new FileInfo(managedPath).Length;
                        File.Delete(managedPath);
                        reclaimedBytes += fileSize;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failed++;
                        continue;
                    }
                }

                try
                {
                    if (await DeleteArtworkCacheRowAsync(connection, candidate, cancellationToken))
                    {
                        deleted++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                }
            }
        }
        finally
        {
            ArtworkCacheGate.Release();
        }

        return new ArtworkCacheCleanupResult(
            scanned,
            deleted,
            reclaimedBytes,
            skippedReferenced,
            failed);
    }

    private async Task<bool> DeleteArtworkCacheRowAsync(
        DbConnection connection,
        ArtworkCacheEntry candidate,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM artwork_cache
            WHERE cache_key = @cacheKey
              AND fetched_utc = @fetchedUtc
              AND local_path IS @localPath;
            """;
        AddParameter(command, "@cacheKey", candidate.CacheKey);
        AddParameter(command, "@fetchedUtc", candidate.FetchedText);
        AddParameter(command, "@localPath", candidate.LocalPath);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Where a cached artwork file is now (#1088). The row stores the absolute
    /// path it was written to, which is wrong as soon as the data folder moves
    /// or a backup is restored elsewhere; the file travels inside
    /// <c>artwork-cache</c>, so it is found by its own name in the current
    /// data folder's cache. Only a file named for its cache key, directly in
    /// that folder, is ever served or deleted.
    /// </summary>
    private bool TryGetManagedArtworkPath(
        string cacheKey,
        string localPath,
        out string managedPath)
    {
        managedPath = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(localPath))
            {
                return false;
            }

            // The stored path may come from another machine, so both separators count.
            var fileName = localPath[(localPath.LastIndexOfAny(['\\', '/']) + 1)..];
            if (fileName.Length == 0 ||
                !string.Equals(
                    Path.GetFileNameWithoutExtension(fileName),
                    cacheKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            managedPath = Path.Combine(Path.GetFullPath(_artworkRootPath), fileName);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public async Task<MetadataArtworkAsset?> GetCachedArtworkAsync(string cacheKey, CancellationToken cancellationToken)
    {
        if (!IsArtworkCacheKey(cacheKey, out var key))
        {
            return null;
        }

        var file = await FindCachedArtworkFileAsync(key, cancellationToken);
        if (file is null)
        {
            // The file is gone (deleted, cleaned up, or only partly restored):
            // fetch it again from the address the row remembers (#1089).
            var row = await ReadArtworkRowAsync(key, cancellationToken);
            if (row is null
                || !Uri.TryCreate(row.RemoteUrl, UriKind.Absolute, out var remoteUri)
                || !IsArtworkSourceAllowed(remoteUri)
                || !string.Equals(ComputeSha256(remoteUri.ToString()), key, StringComparison.Ordinal))
            {
                return null;
            }

            file = await DownloadArtworkAsync(key, row.MediaType, remoteUri, cancellationToken);
        }

        return file is null
            ? null
            : new MetadataArtworkAsset(file, ResolveContentType(file));
    }

    private static bool IsArtworkCacheKey(string? cacheKey, out string key)
    {
        key = string.Empty;
        if (cacheKey is null)
        {
            return false;
        }

        var candidate = cacheKey.Trim();
        if (candidate.Length != 64 || candidate.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        key = candidate.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Deluno only fetches artwork over http(s) from a named host, never from
    /// the machine itself or its local network, and the address on a row must
    /// hash to the row's own key, so nothing but what Deluno itself once
    /// localised can be fetched here.
    /// </summary>
    private static bool IsArtworkSourceAllowed(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        if (uri.IsLoopback
            || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Contains('.'))
        {
            return false;
        }

        return true;
    }

    private async Task<ArtworkRow?> ReadArtworkRowAsync(string cacheKey, CancellationToken cancellationToken)
    {
        await ArtworkCacheGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
                DelunoDatabaseNames.Cache,
                cancellationToken);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT remote_url, media_type
                FROM artwork_cache
                WHERE cache_key = @cacheKey
                LIMIT 1;
                """;
            AddParameter(command, "@cacheKey", cacheKey);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
            {
                return null;
            }

            return new ArtworkRow(reader.GetString(0), reader.IsDBNull(1) ? "movies" : reader.GetString(1));
        }
        finally
        {
            ArtworkCacheGate.Release();
        }
    }

    private async Task<string?> ReadArtworkLocalPathAsync(string cacheKey, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Cache,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT local_path
            FROM artwork_cache
            WHERE cache_key = @cacheKey
            LIMIT 1;
            """;
        AddParameter(command, "@cacheKey", cacheKey);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task UpsertArtworkCacheAsync(
        string cacheKey,
        string mediaType,
        string remoteUrl,
        string? localPath,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Cache,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO artwork_cache (
                cache_key, media_type, remote_url, local_path, fetched_utc, expires_utc
            )
            VALUES (
                @cacheKey, @mediaType, @remoteUrl, @localPath, @fetchedUtc, @expiresUtc
            )
            ON CONFLICT(cache_key) DO UPDATE SET
                media_type = excluded.media_type,
                remote_url = excluded.remote_url,
                local_path = excluded.local_path,
                fetched_utc = excluded.fetched_utc,
                expires_utc = excluded.expires_utc;
            """;
        AddParameter(command, "@cacheKey", cacheKey);
        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@remoteUrl", remoteUrl);
        AddParameter(command, "@localPath", localPath);
        AddParameter(command, "@fetchedUtc", now.ToString("O"));
        AddParameter(command, "@expiresUtc", now.AddDays(30).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<MetadataSearchResult>?> TryReadSearchCacheAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Cache,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT result_json
            FROM search_result_cache
            WHERE cache_key = @cacheKey
              AND (expires_utc IS NULL OR expires_utc > @now)
            LIMIT 1;
            """;
        AddParameter(command, "@cacheKey", cacheKey);
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));

        var payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<MetadataSearchResult>>(payload, CacheJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<MetadataSearchResult>?> TryReadStaleCacheAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Cache,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT result_json
            FROM search_result_cache
            WHERE cache_key = @cacheKey
            ORDER BY created_utc DESC
            LIMIT 1;
            """;
        AddParameter(command, "@cacheKey", cacheKey);

        var payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<MetadataSearchResult>>(payload, CacheJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task WriteSearchCacheAsync(
        string cacheKey,
        string mediaType,
        string query,
        IReadOnlyList<MetadataSearchResult> results,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Cache,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO search_result_cache (
                cache_key, media_type, query_text, result_json, created_utc, expires_utc
            )
            VALUES (
                @cacheKey, @mediaType, @queryText, @resultJson, @createdUtc, @expiresUtc
            )
            ON CONFLICT(cache_key) DO UPDATE SET
                result_json = excluded.result_json,
                created_utc = excluded.created_utc,
                expires_utc = excluded.expires_utc;
            """;
        AddParameter(command, "@cacheKey", cacheKey);
        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@queryText", query);
        AddParameter(command, "@resultJson", JsonSerializer.Serialize(results, CacheJsonOptions));
        AddParameter(command, "@createdUtc", now.ToString("O"));
        AddParameter(command, "@expiresUtc", now.AddHours(12).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildSearchCacheKey(string mediaType, string query, int? year, string? providerId)
        => BuildSearchCacheKey(ProviderName, mediaType, query, year, providerId, null);

    /// <summary>
    /// The shape of a cached metadata result, not the query.
    ///
    /// A cached payload written under an older shape is not a cheaper answer to
    /// the same question — it is a *different* answer, missing whatever the
    /// contract has learnt to carry since. When the broker gained runtime,
    /// certification, studio, network and status, every install with a warm
    /// cache would have kept serving results without them, and the only symptom
    /// would have been columns that stayed empty for no visible reason.
    ///
    /// Bump this whenever <see cref="MetadataSearchResult"/> gains a field.
    /// </summary>
    private const string SearchCacheShape = "v6";

    // The language is part of the key, not the shape: two installs asking for
    // the same title in different languages are asking a different question,
    // and a cached Portuguese title must never answer an English request.
    private static string BuildSearchCacheKey(
        string source,
        string mediaType,
        string query,
        int? year,
        string? providerId,
        string? language)
        => $"{source}:search:{SearchCacheShape}:{mediaType}:{query.Trim().ToLowerInvariant()}:{year?.ToString(CultureInfo.InvariantCulture) ?? "any"}:{providerId?.Trim() ?? "none"}:{language?.Trim().ToLowerInvariant() ?? "default"}";

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private async Task<MetadataProviderConfiguration> GetMetadataConfigurationAsync(CancellationToken cancellationToken)
    {
        var settings = await platformRepository.GetAsync(cancellationToken);
        var providerMode = ResolveProviderMode(settings.MetadataProviderMode);
        var brokerUrl = ResolveBrokerUrl(settings.MetadataBrokerUrl);

        // Broker mode is the normal product route. Do not even read a legacy
        // per-install provider secret there: it is neither needed nor an
        // acceptable implicit fallback for a managed-metadata installation.
        var requiresDirectFallback = providerMode is "direct" or "hybrid";
        return new MetadataProviderConfiguration(
            providerMode,
            brokerUrl,
            requiresDirectFallback ? await GetApiKeyAsync(cancellationToken) : null,
            requiresDirectFallback ? await GetOmdbApiKeyAsync(cancellationToken) : null,
            NormalizeLanguage(settings.MetadataLanguage),
            NormalizeCountry(settings.MetadataCertificationCountry),
            requiresDirectFallback && (await GetTvdbCredentialsAsync(cancellationToken)).Key is not null);
    }

    /// <summary>
    /// The saved details language and ratings-region country, for the callers
    /// that talk to TMDb directly outside <see cref="GetMetadataConfigurationAsync"/>
    /// (the broker-bypassing debug search). A broker or hybrid call never
    /// reaches here: the managed gateway contract accepts title lookup terms,
    /// media type/year and a TMDb id only, so neither setting has anywhere to
    /// go on that path (see docs/metadata-broker-contract.md).
    /// </summary>
    private async Task<(string? Language, string CertificationCountry)> GetLocalizationAsync(CancellationToken cancellationToken)
    {
        var settings = await platformRepository.GetAsync(cancellationToken);
        return (NormalizeLanguage(settings.MetadataLanguage), NormalizeCountry(settings.MetadataCertificationCountry));
    }

    /// <summary>
    /// Blank reads as "no preference", which lets TMDb apply its own default
    /// (en-US) rather than Deluno forcing a language parameter that was never
    /// actually set.
    /// </summary>
    private static string? NormalizeLanguage(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    /// <summary>Unlike language, a country always resolves to something: certification lookup needs one to compare against, and US is the board Deluno showed before this setting existed.</summary>
    private static string NormalizeCountry(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "US" : trimmed;
    }

    private string ResolveProviderMode(string? legacySettingsValue)
    {
        var value = configuration["Deluno:Metadata:ProviderMode"]
                    ?? configuration["DELUNO_METADATA_PROVIDER_MODE"]
                    ?? Environment.GetEnvironmentVariable("DELUNO_METADATA_PROVIDER_MODE")
                    ?? legacySettingsValue;

        return value?.Trim().ToLowerInvariant() switch
        {
            "broker" => "broker",
            "hybrid" => "hybrid",
            _ => "direct"
        };
    }

    private string? ResolveBrokerUrl(string? settingsValue)
    {
        var value = configuration["Deluno:Metadata:BrokerUrl"]
                    ?? configuration["DELUNO_METADATA_BROKER_URL"]
                    ?? Environment.GetEnvironmentVariable("DELUNO_METADATA_BROKER_URL");
        value = string.IsNullOrWhiteSpace(value) ? settingsValue : value;
        return string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('/');
    }

    private static IReadOnlyList<MetadataSourceStatus> BuildSourceStatuses(
        MetadataProviderConfiguration config,
        bool directConfigured,
        bool brokerConfigured)
    {
        return
        [
            new MetadataSourceStatus(
                "broker",
                "Deluno broker",
                "Primary managed lookup",
                brokerConfigured,
                config.ProviderMode is "broker" or "hybrid" ? config.ProviderMode : "available",
                brokerConfigured
                    ? "Broker URL is configured for managed metadata lookup."
                    : "Not configured. Add a broker URL when hosted metadata is available."),
            new MetadataSourceStatus(
                "tmdb",
                "TMDb",
                "Movies, TV, artwork, genres, IDs",
                directConfigured,
                config.ProviderMode == "direct" ? "primary" : "fallback",
                directConfigured
                    ? "Direct TMDb key is stored and can resolve title search and artwork."
                    : "No direct TMDb key is stored."),
            new MetadataSourceStatus(
                "omdb",
                "OMDb",
                "IMDb, Rotten Tomatoes, Metacritic",
                !string.IsNullOrWhiteSpace(config.OmdbApiKey),
                "enrichment",
                !string.IsNullOrWhiteSpace(config.OmdbApiKey)
                    ? "OMDb ratings enrichment is configured."
                    : "Optional ratings enrichment is not configured."),
            new MetadataSourceStatus(
                "tvdb",
                "TVDb",
                "TV first: series details, artwork, episode numbering",
                config.TvdbDirectKey || (brokerConfigured && config.ProviderMode is "broker" or "hybrid"),
                config.TvdbDirectKey || config.ProviderMode is "broker" or "hybrid" ? "primary-tv" : "unavailable",
                config.TvdbDirectKey
                    ? "A TheTVDB key is set on this installation; TV titles are matched against TheTVDB first, with TMDb filling any gaps."
                    : brokerConfigured && config.ProviderMode is "broker" or "hybrid"
                        ? "TV titles are matched against TheTVDB through Deluno's managed metadata service, with TMDb filling any gaps."
                        : "No TheTVDB key is set, so TV titles use TMDb only."),
            new MetadataSourceStatus(
                "fanart",
                "Fanart.tv",
                "Future artwork enrichment",
                false,
                "planned",
                "Reserved for richer poster, logo, and background artwork.")
        ];
    }

    private async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken)
        => configuration["Deluno:Metadata:TMDbApiKey"]
           ?? configuration["TMDB_API_KEY"]
           ?? Environment.GetEnvironmentVariable("TMDB_API_KEY")
           // Legacy per-install secrets remain as a compatibility fallback. The normal UI no longer writes them.
           ?? await platformRepository.GetMetadataProviderSecretAsync(ProviderName, cancellationToken);

    private async Task<string?> GetOmdbApiKeyAsync(CancellationToken cancellationToken)
        => configuration["Deluno:Metadata:OMDbApiKey"]
           ?? configuration["OMDB_API_KEY"]
           ?? Environment.GetEnvironmentVariable("OMDB_API_KEY")
           // Legacy per-install secrets remain as a compatibility fallback. The normal UI no longer writes them.
           ?? await platformRepository.GetMetadataProviderSecretAsync("omdb", cancellationToken);

    private async Task<IReadOnlyList<MetadataSearchResult>?> TryBrokerSearchAsync(
        string brokerUrl,
        string mediaType,
        MetadataLookupRequest request,
        string query,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BuildBrokerSearchBaseUrl(brokerUrl)}?mediaType={Uri.EscapeDataString(mediaType)}&query={Uri.EscapeDataString(query)}";
        if (request.Year is > 0)
        {
            url += $"&year={request.Year.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        if (!string.IsNullOrWhiteSpace(request.ProviderId))
        {
            url += $"&providerId={Uri.EscapeDataString(request.ProviderId.Trim())}";
        }

        var brokerKey = BuildHostKey(brokerUrl);
        var brokerResponse = await GetJsonWithResilienceAsync<MetadataBrokerSearchResponse>(
            url,
            $"metadata:broker:{brokerKey}",
            "metadata.broker.search",
            cancellationToken);

        if (brokerResponse is null)
        {
            logger.LogWarning("Broker search failed or returned null response from {BrokerHost} for query: {Query}", brokerKey, query);
            return null;
        }

        return brokerResponse.Results?.Take(12).ToArray();
    }

    private async Task<MetadataProviderRecordLookup> ResolveBrokerProviderRecordAsync(
        string brokerUrl,
        string mediaType,
        MetadataLookupRequest request,
        string providerId,
        CancellationToken cancellationToken)
    {
        var query = request.Query?.Trim() ?? providerId;
        var url =
            $"{BuildBrokerSearchBaseUrl(brokerUrl)}?mediaType={Uri.EscapeDataString(mediaType)}&query={Uri.EscapeDataString(query)}&providerId={Uri.EscapeDataString(providerId)}";
        if (request.Year is > 0)
        {
            url += $"&year={request.Year.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        var brokerKey = BuildHostKey(brokerUrl);
        var response = await GetJsonWithResilienceAsync<MetadataBrokerSearchResponse>(
            url,
            $"metadata:broker:{brokerKey}",
            "metadata.broker.resolve",
            cancellationToken);
        if (response is null)
        {
            var failure = LastFailure;
            return new MetadataProviderRecordLookup(
                failure?.HttpStatus == 404 && await BrokerSaysRecordMissingAsync(url, cancellationToken)
                    ? MetadataProviderRecordStatus.Missing
                    : MetadataProviderRecordStatus.Unavailable,
                ProviderName,
                providerId,
                Failure: failure);
        }

        var exact = response.Results?.FirstOrDefault(item =>
            string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            var localized = await LocalizeArtworkAsync([exact], cancellationToken);
            return new MetadataProviderRecordLookup(
                MetadataProviderRecordStatus.Found,
                ProviderName,
                providerId,
                localized[0]);
        }

        return new MetadataProviderRecordLookup(
            // Only the gateway saying "provider_record_missing" makes a record
            // missing; a result list that lacks the id is an unanswered question.
            MetadataProviderRecordStatus.Unavailable,
            ProviderName,
            providerId,
            Failure: response.Failure);
    }

    /// <summary>
    /// Asks the gateway again, reading the reason it gave for a 404. The shared
    /// request path does not keep the body, and a proxy or a wrong address also
    /// answers 404, so "missing" needs the gateway's own words (#1091). The
    /// gateway remembers its answers briefly, so this costs it almost nothing.
    /// </summary>
    private async Task<bool> BrokerSaysRecordMissingAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }

            return IsProviderRecordMissingBody(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return false;
        }
    }

    /// <summary>True only for the gateway's <c>{"error":"provider_record_missing"}</c> body.</summary>
    internal static bool IsProviderRecordMissingBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String &&
                   string.Equals(error.GetString(), "provider_record_missing", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildBrokerSearchBaseUrl(string brokerUrl)
    {
        var trimmed = brokerUrl.TrimEnd('/');
        return trimmed.EndsWith("/metadata/broker", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("/api/metadata/broker", StringComparison.OrdinalIgnoreCase)
            ? $"{trimmed}/search"
            : $"{trimmed}/metadata/search";
    }

    private async Task<MetadataSearchResult> ToResultAsync(
        TmdbSearchItem item,
        string mediaType,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var title = item.Title ?? item.Name ?? "Unknown title";
        var releaseDate = mediaType == "tv" ? item.FirstAirDate : item.ReleaseDate;
        var year = TryParseYear(releaseDate);
        var poster = string.IsNullOrWhiteSpace(item.PosterPath)
            ? null
            : $"https://image.tmdb.org/t/p/{ArtworkSizes.Poster}{item.PosterPath}";
        var backdrop = string.IsNullOrWhiteSpace(item.BackdropPath)
            ? null
            : $"https://image.tmdb.org/t/p/{ArtworkSizes.Backdrop}{item.BackdropPath}";

        var externalIds = await GetExternalIdsAsync(item.Id, mediaType, apiKey, cancellationToken);

        var ratings = await BuildRatingsAsync(
            mediaType,
            item.Id,
            item.VoteAverage,
            item.VoteCount,
            externalIds.ImdbId,
            cancellationToken);

        return new MetadataSearchResult(
            Provider: ProviderName,
            ProviderId: item.Id.ToString(CultureInfo.InvariantCulture),
            MediaType: mediaType,
            Title: title,
            OriginalTitle: item.OriginalTitle ?? item.OriginalName,
            Year: year,
            Overview: item.Overview,
            PosterUrl: poster,
            BackdropUrl: backdrop,
            Rating: item.VoteAverage,
            Ratings: ratings,
            Genres: ResolveGenreNames(mediaType, item.GenreIds),
            ImdbId: externalIds.ImdbId,
            ExternalUrl: BuildTmdbUrl(mediaType, item.Id),
            // A search result carries no runtime; the detail lookup does.
            Popularity: item.Popularity,
            VoteCount: item.VoteCount,
            TvDbId: externalIds.TvDbId);
    }

    private async Task<MetadataSearchResult?> GetDetailsByIdAsync(
        int id,
        string mediaType,
        string apiKey,
        string? language,
        string certificationCountry,
        CancellationToken cancellationToken)
    {
        var path = mediaType == "tv" ? "tv" : "movie";
        // A show's certification lives under content_ratings; a movie's under
        // release_dates. Asking for the one that does not apply to this media
        // type costs nothing — TMDb answers null for it, the same as any other
        // append the title does not carry.
        var appends = mediaType == "tv"
            ? "external_ids,credits,keywords,content_ratings,alternative_titles,images"
            : "external_ids,credits,keywords,release_dates,alternative_titles,images";
        var url = $"https://api.themoviedb.org/3/{path}/{id}?api_key={Uri.EscapeDataString(apiKey)}&append_to_response={appends}";
        if (!string.IsNullOrWhiteSpace(language))
        {
            url += $"&language={Uri.EscapeDataString(language)}";
        }

        var detail = await GetJsonWithResilienceAsync<TmdbDetailItem>(
            url,
            $"metadata:tmdb:detail:{mediaType}",
            "metadata.tmdb.detail",
            cancellationToken);
        if (detail is null || string.IsNullOrWhiteSpace(detail.Title ?? detail.Name))
        {
            return null;
        }

        var title = detail.Title ?? detail.Name ?? "Unknown title";
        var releaseDate = mediaType == "tv" ? detail.FirstAirDate : detail.ReleaseDate;
        var poster = string.IsNullOrWhiteSpace(detail.PosterPath)
            ? null
            : $"https://image.tmdb.org/t/p/{ArtworkSizes.Poster}{detail.PosterPath}";
        var backdrop = string.IsNullOrWhiteSpace(detail.BackdropPath)
            ? null
            : $"https://image.tmdb.org/t/p/{ArtworkSizes.Backdrop}{detail.BackdropPath}";

        var ratings = await BuildRatingsAsync(
            mediaType,
            detail.Id,
            detail.VoteAverage,
            detail.VoteCount,
            detail.ExternalIds?.ImdbId,
            cancellationToken);

        return new MetadataSearchResult(
            Provider: ProviderName,
            ProviderId: detail.Id.ToString(CultureInfo.InvariantCulture),
            MediaType: mediaType,
            Title: title,
            OriginalTitle: detail.OriginalTitle ?? detail.OriginalName,
            Year: TryParseYear(releaseDate),
            Overview: detail.Overview,
            PosterUrl: poster,
            BackdropUrl: backdrop,
            Rating: detail.VoteAverage,
            Ratings: ratings,
            Genres: detail.Genres?.Select(genre => genre.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToArray() ?? [],
            ImdbId: detail.ExternalIds?.ImdbId,
            ExternalUrl: BuildTmdbUrl(mediaType, detail.Id),
            Cast: ReadCast(detail.Credits?.Cast),
            Crew: ReadCrew(detail.Credits?.Crew),
            RuntimeMinutes: detail.Runtime ?? detail.EpisodeRunTime?.FirstOrDefault(minutes => minutes > 0),
            Popularity: detail.Popularity,
            VoteCount: detail.VoteCount,
            // A show has a network and a film has a studio; both are "who made
            // it", and TMDb answers them in different fields.
            Studio: detail.ProductionCompanies?.Select(company => company?.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
            Network: detail.Networks?.Select(network => network?.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
            Collection: string.IsNullOrWhiteSpace(detail.BelongsToCollection?.Name) ? null : detail.BelongsToCollection.Name,
            CollectionProviderId: detail.BelongsToCollection?.Id?.ToString(CultureInfo.InvariantCulture),
            // Never set here at all, though the crew list it comes out of has
            // been fetched all along. The catalogue has a Director column and
            // sorts on it, so a direct-TMDb library sorted every title as blank
            // — the same shape as the studio and status fields above.
            Director: detail.Credits?.Crew?
                .FirstOrDefault(member => member.Job == "Director" && !string.IsNullOrWhiteSpace(member.Name))?.Name,
            Tagline: string.IsNullOrWhiteSpace(detail.Tagline) ? null : detail.Tagline,
            Homepage: string.IsNullOrWhiteSpace(detail.Homepage) ? null : detail.Homepage,
            OriginalLanguage: string.IsNullOrWhiteSpace(detail.OriginalLanguage) ? null : detail.OriginalLanguage,
            TvDbId: detail.ExternalIds?.TvDbId,
            Status: string.IsNullOrWhiteSpace(detail.Status) ? null : detail.Status,
            Keywords: detail.Keywords?.Names ?? [],
            Certification: ResolveCertification(mediaType, certificationCountry, detail.ReleaseDates, detail.ContentRatings),
            AlternateTitles: detail.AlternativeTitles?.Names(title, detail.OriginalTitle ?? detail.OriginalName),
            LogoUrl: BuildTmdbArtworkUrl(detail.Images?.Logos?.FirstOrDefault()?.FilePath, ArtworkSizes.Logo),
            Screenshots: BuildScreenshotUrls(detail.Images?.Backdrops, detail.BackdropPath),
            ReleaseFactsByCountry: BuildReleaseFactsByCountry(mediaType, detail.ReleaseDates, detail.ContentRatings));
    }

    /// <summary>
    /// Every country TMDb answered for, unreduced (#862) — the same
    /// <c>detail.ReleaseDates</c>/<c>detail.ContentRatings</c>
    /// <see cref="ResolveCertification"/> already picks one country's
    /// certification out of, kept here in full so a later certification
    /// country change can pick a different one already-stored country
    /// without a re-fetch.
    /// </summary>
    private static IReadOnlyList<MetadataCountryFacts>? BuildReleaseFactsByCountry(
        string mediaType,
        TmdbReleaseDatesResponse? releaseDates,
        TmdbContentRatingsResponse? contentRatings)
    {
        if (mediaType == "tv")
        {
            // TMDb has no per-country premiere/digital/physical dates for a
            // show, only content_ratings' per-country certification.
            var facts = contentRatings?.Results?
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Country))
                .Select(entry => new MetadataCountryFacts(entry.Country!.ToUpperInvariant(), null, null, null, NullIfBlank(entry.Rating)))
                .ToArray();
            return facts is { Length: > 0 } ? facts : null;
        }

        var movieFacts = releaseDates?.Results?
            .Where(country => !string.IsNullOrWhiteSpace(country.Country))
            .Select(country =>
            {
                DateOnly? cinemas = null;
                DateOnly? digital = null;
                DateOnly? physical = null;
                string? certification = null;
                foreach (var entry in country.ReleaseDates ?? [])
                {
                    var date = ParseDateOnly(entry.ReleaseDate);
                    if (date is not null)
                    {
                        if (entry.Type is 2 or 3 && (cinemas is null || date < cinemas)) cinemas = date;
                        else if (entry.Type is 4 && (digital is null || date < digital)) digital = date;
                        else if (entry.Type is 5 && (physical is null || date < physical)) physical = date;
                    }

                    certification ??= NullIfBlank(entry.Certification);
                }

                return new MetadataCountryFacts(country.Country!.ToUpperInvariant(), cinemas, digital, physical, certification);
            })
            .Where(facts => facts.InCinemas is not null || facts.Digital is not null || facts.Physical is not null || facts.Certification is not null)
            .ToArray();
        return movieFacts is { Length: > 0 } ? movieFacts : null;
    }

    /// <summary>
    /// The certification Deluno shows for a title, taken for the saved
    /// ratings-region country (metadata.certificationCountry). TMDb reports a
    /// certification per country, and for a movie, per release type within
    /// that country. A country with nothing on file for this title — most of
    /// them, most of the time — falls back to the US board, which is the
    /// certification Deluno showed before a region setting existed to ask
    /// for.
    /// </summary>
    private static string? ResolveCertification(
        string mediaType,
        string certificationCountry,
        TmdbReleaseDatesResponse? releaseDates,
        TmdbContentRatingsResponse? contentRatings)
    {
        if (mediaType == "tv")
        {
            var ratings = contentRatings?.Results?
                .Select(entry => (entry.Country, entry.Rating))
                .ToArray();
            return FindCertification(ratings, certificationCountry) ?? FindCertification(ratings, "US");
        }

        var certifications = releaseDates?.Results?
            .Select(country => (
                country.Country,
                Rating: country.ReleaseDates?
                    .Select(entry => entry.Certification)
                    .FirstOrDefault(certification => !string.IsNullOrWhiteSpace(certification))))
            .ToArray();
        return FindCertification(certifications, certificationCountry) ?? FindCertification(certifications, "US");
    }

    private static string? FindCertification(
        IReadOnlyList<(string? Country, string? Rating)>? entries,
        string country)
        => entries?
            .Where(entry => string.Equals(entry.Country, country, StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(entry.Rating))
            .Select(entry => entry.Rating)
            .FirstOrDefault();

    /// <summary>
    /// The season/episode catalogue for a series.
    ///
    /// The gateway is asked first — it does the per-season fan-out and caches the
    /// answer, so the app makes one request instead of one per season. TMDb is the
    /// backup when the gateway cannot answer: a catalogue is the difference
    /// between a library that mirrors your disk and one that knows what episodes
    /// exist, so it is worth a direct call rather than nothing.
    /// </summary>
    private async Task<IReadOnlyList<MetadataSeason>> GetTmdbSeriesCatalogueAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || !int.TryParse(providerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return [];
        }

        var config = await GetMetadataConfigurationAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(config.BrokerUrl))
        {
            var fromBroker = await TryBrokerCatalogueAsync(config.BrokerUrl, id, cancellationToken);
            if (fromBroker is { Count: > 0 })
            {
                return fromBroker;
            }

            logger.LogInformation(
                "Metadata gateway returned no catalogue for series {ProviderId}; falling back to a direct provider call.",
                providerId);
        }

        var apiKey = config.TmdbApiKey ?? await GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning(
                "No episode catalogue for series {ProviderId}: the gateway could not serve one and no direct key is available.",
                providerId);
            return [];
        }

        return await GetTmdbCatalogueAsync(id, apiKey, cancellationToken);
    }

    /// <summary>
    /// Every alternate ordering (aired/DVD/absolute/story-arc) TMDb's
    /// episode_groups endpoint publishes for this series (#863).
    /// </summary>
    private async Task<IReadOnlyList<MetadataEpisodeGroup>> GetTmdbSeriesEpisodeGroupsAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || !int.TryParse(providerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return [];
        }

        var config = await GetMetadataConfigurationAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(config.BrokerUrl))
        {
            var fromBroker = await TryBrokerEpisodeGroupsAsync(config.BrokerUrl, id, cancellationToken);
            if (fromBroker is { Count: > 0 })
            {
                return fromBroker;
            }

            logger.LogInformation(
                "Metadata gateway returned no episode groups for series {ProviderId}; falling back to a direct provider call.",
                providerId);
        }

        var apiKey = config.TmdbApiKey ?? await GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        return await GetTmdbEpisodeGroupsAsync(id, apiKey, cancellationToken);
    }

    /// <summary>Release dates for a movie: in cinemas, digital, physical.</summary>
    public async Task<MetadataReleaseDates> GetMovieReleaseDatesAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || !int.TryParse(providerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return MetadataReleaseDates.None;
        }

        var config = await GetMetadataConfigurationAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(config.BrokerUrl))
        {
            var fromBroker = await TryBrokerReleaseDatesAsync(config.BrokerUrl, id, cancellationToken);
            if (fromBroker is not null)
            {
                return fromBroker;
            }
        }

        var apiKey = config.TmdbApiKey ?? await GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return MetadataReleaseDates.None;
        }

        return await GetTmdbReleaseDatesAsync(id, apiKey, cancellationToken);
    }

    /// <summary>
    /// Fetches a movie collection as one provider operation. Broker mode keeps
    /// the provider call behind the same metadata boundary as title search;
    /// hybrid mode falls back to TMDb when the broker is unavailable.
    /// </summary>
    public async Task<MetadataCollection?> GetMovieCollectionAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || !int.TryParse(providerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        var config = await GetMetadataConfigurationAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(config.BrokerUrl))
        {
            var fromBroker = await TryBrokerCollectionAsync(config.BrokerUrl, id, cancellationToken);
            if (fromBroker is not null)
            {
                return fromBroker;
            }

            if (config.ProviderMode == "broker")
            {
                return null;
            }
        }

        var apiKey = config.TmdbApiKey ?? await GetApiKeyAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : await GetTmdbCollectionAsync(id, apiKey, cancellationToken);
    }

    /// <summary>
    /// Discover's TMDb recommendations grid (#785). Same broker-then-direct
    /// shape every other lookup in this provider follows, but with no OMDb
    /// fallback and no DB search cache — a popular list is not a title lookup
    /// a person is waiting on, and it goes stale in hours rather than days.
    /// </summary>
    public async Task<IReadOnlyList<MetadataSearchResult>> GetPopularAsync(
        string mediaType,
        int page,
        CancellationToken cancellationToken)
    {
        var normalizedMediaType = NormalizeMediaType(mediaType);
        var config = await GetMetadataConfigurationAsync(cancellationToken);
        var pageNumber = Math.Clamp(page, 1, 500);

        if (config.ProviderMode is "broker" or "hybrid" && !string.IsNullOrWhiteSpace(config.BrokerUrl))
        {
            var fromBroker = await TryBrokerPopularAsync(config.BrokerUrl, normalizedMediaType, pageNumber, cancellationToken);
            if (fromBroker is { Count: > 0 })
            {
                return await LocalizeArtworkAsync(fromBroker, cancellationToken);
            }

            if (config.ProviderMode == "broker")
            {
                return [];
            }
        }

        var apiKey = config.TmdbApiKey ?? await GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        var direct = await GetTmdbPopularAsync(normalizedMediaType, pageNumber, apiKey, config.Language, cancellationToken);
        return await LocalizeArtworkAsync(direct, cancellationToken);
    }

    private async Task<IReadOnlyList<MetadataSearchResult>?> TryBrokerPopularAsync(
        string brokerUrl,
        string mediaType,
        int page,
        CancellationToken cancellationToken)
    {
        var url = $"{BuildGatewayBaseUrl(brokerUrl)}/{mediaType}/popular?page={page.ToString(CultureInfo.InvariantCulture)}";
        var response = await GetJsonWithResilienceAsync<TmdbSearchResponse>(
            url,
            $"metadata:broker:popular:{BuildHostKey(brokerUrl)}",
            "metadata.broker.popular",
            cancellationToken);

        return response?.Results is { Count: > 0 } results
            ? results.Select(item => ToDiscoverResult(item, mediaType)).ToArray()
            : null;
    }

    private async Task<IReadOnlyList<MetadataSearchResult>> GetTmdbPopularAsync(
        string mediaType,
        int page,
        string apiKey,
        string? language,
        CancellationToken cancellationToken)
    {
        var languageParam = string.IsNullOrWhiteSpace(language) ? string.Empty : $"&language={Uri.EscapeDataString(language)}";
        var url = $"https://api.themoviedb.org/3/{(mediaType == "tv" ? "tv" : "movie")}/popular"
            + $"?api_key={Uri.EscapeDataString(apiKey)}&page={page.ToString(CultureInfo.InvariantCulture)}{languageParam}";
        var response = await GetJsonWithResilienceAsync<TmdbSearchResponse>(
            url,
            "metadata:tmdb:popular",
            "metadata.tmdb.popular",
            cancellationToken);

        return response?.Results is { Count: > 0 } results
            ? results.Select(item => ToDiscoverResult(item, mediaType)).ToArray()
            : [];
    }

    /// <summary>
    /// A popular-list item to a search result, without the per-title detail
    /// fetch <see cref="ToResultAsync"/> does for a real search hit — Discover
    /// shows a grid of many titles at once, and one detail call per card would
    /// turn opening the page into dozens of requests for facts the card does
    /// not display (cast, crew, full ratings breakdown).
    /// </summary>
    private MetadataSearchResult ToDiscoverResult(TmdbSearchItem item, string mediaType)
        => new(
            Provider: ProviderName,
            ProviderId: item.Id.ToString(CultureInfo.InvariantCulture),
            MediaType: mediaType,
            Title: NullIfBlank(item.Title ?? item.Name) ?? "Untitled",
            OriginalTitle: NullIfBlank(item.OriginalTitle ?? item.OriginalName),
            Year: TryParseYear(item.ReleaseDate ?? item.FirstAirDate),
            Overview: NullIfBlank(item.Overview),
            PosterUrl: BuildTmdbArtworkUrl(item.PosterPath, ArtworkSizes.Poster),
            BackdropUrl: BuildTmdbArtworkUrl(item.BackdropPath, ArtworkSizes.Backdrop),
            Rating: item.VoteAverage,
            Ratings: [],
            Genres: [],
            ImdbId: null,
            ExternalUrl: BuildTmdbUrl(mediaType, item.Id));

    private string BuildGatewayBaseUrl(string brokerUrl)
    {
        var trimmed = brokerUrl.TrimEnd('/');
        return trimmed.EndsWith("/metadata/broker", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("/api/metadata/broker", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/metadata";
    }

    private async Task<IReadOnlyList<MetadataSeason>?> TryBrokerCatalogueAsync(
        string brokerUrl,
        int id,
        CancellationToken cancellationToken)
    {
        var url = $"{BuildGatewayBaseUrl(brokerUrl)}/tv/{id.ToString(CultureInfo.InvariantCulture)}/catalogue";
        var response = await GetJsonWithResilienceAsync<BrokerCatalogueResponse>(
            url,
            $"metadata:broker:catalogue:{BuildHostKey(brokerUrl)}",
            "metadata.broker.catalogue",
            cancellationToken);

        if (response?.Seasons is not { Count: > 0 })
        {
            return null;
        }

        return response.Seasons
            .Select(season => new MetadataSeason(
                season.SeasonNumber,
                NullIfBlank(season.Name),
                season.Episodes?.Count ?? 0,
                ParseDateOnly(season.AirDate),
                (season.Episodes ?? [])
                    .Select(episode => new MetadataEpisode(
                        season.SeasonNumber,
                        episode.EpisodeNumber,
                        NullIfBlank(episode.Title),
                        NullIfBlank(episode.Overview),
                        ParseAirDate(episode.AirDate)))
                    .ToArray()))
            .ToArray();
    }

    private async Task<IReadOnlyList<MetadataEpisodeGroup>?> TryBrokerEpisodeGroupsAsync(
        string brokerUrl,
        int id,
        CancellationToken cancellationToken)
    {
        var url = $"{BuildGatewayBaseUrl(brokerUrl)}/tv/{id.ToString(CultureInfo.InvariantCulture)}/episode-groups";
        var response = await GetJsonWithResilienceAsync<BrokerEpisodeGroupsResponse>(
            url,
            $"metadata:broker:episode-groups:{BuildHostKey(brokerUrl)}",
            "metadata.broker.episode-groups",
            cancellationToken);

        if (response?.Groups is not { Count: > 0 })
        {
            return null;
        }

        return response.Groups
            .Where(group => !string.IsNullOrWhiteSpace(group.ProviderGroupId) && !string.IsNullOrWhiteSpace(group.Name))
            .Select(group => new MetadataEpisodeGroup(
                group.Type,
                group.ProviderGroupId!.Trim(),
                group.Name!.Trim(),
                (group.Episodes ?? [])
                    .Select(episode => new MetadataEpisodeGroupEpisode(
                        episode.OriginalSeasonNumber,
                        episode.OriginalEpisodeNumber,
                        episode.GroupSeasonNumber,
                        episode.GroupEpisodeNumber))
                    .ToArray()))
            .ToArray();
    }

    private async Task<MetadataReleaseDates?> TryBrokerReleaseDatesAsync(
        string brokerUrl,
        int id,
        CancellationToken cancellationToken)
    {
        var url = $"{BuildGatewayBaseUrl(brokerUrl)}/movie/{id.ToString(CultureInfo.InvariantCulture)}/release-dates";
        var response = await GetJsonWithResilienceAsync<BrokerReleaseDatesResponse>(
            url,
            $"metadata:broker:release-dates:{BuildHostKey(brokerUrl)}",
            "metadata.broker.release-dates",
            cancellationToken);

        if (response is null)
        {
            return null;
        }

        return new MetadataReleaseDates(
            ParseDateOnly(response.InCinemas),
            ParseDateOnly(response.Digital),
            ParseDateOnly(response.Physical));
    }

    private async Task<MetadataCollection?> TryBrokerCollectionAsync(
        string brokerUrl,
        int id,
        CancellationToken cancellationToken)
    {
        var url = $"{BuildGatewayBaseUrl(brokerUrl)}/movie/{id.ToString(CultureInfo.InvariantCulture)}/collection";
        var response = await GetJsonWithResilienceAsync<BrokerCollectionResponse>(
            url,
            $"metadata:broker:collection:{BuildHostKey(brokerUrl)}",
            "metadata.broker.collection",
            cancellationToken);

        if (response is null || string.IsNullOrWhiteSpace(response.Name))
        {
            return null;
        }

        return new MetadataCollection(
            Provider: ProviderName,
            ProviderId: response.ProviderId?.Trim() ?? id.ToString(CultureInfo.InvariantCulture),
            Name: response.Name.Trim(),
            Overview: NullIfBlank(response.Overview),
            PosterUrl: NullIfBlank(response.PosterUrl),
            BackdropUrl: NullIfBlank(response.BackdropUrl),
            Movies: (response.Movies ?? [])
                .Where(movie => !string.IsNullOrWhiteSpace(movie.ProviderId) && !string.IsNullOrWhiteSpace(movie.Title))
                .Select(movie => new MetadataCollectionMovie(
                    movie.ProviderId!.Trim(),
                    movie.Title!.Trim(),
                    movie.Year,
                    NullIfBlank(movie.Overview),
                    NullIfBlank(movie.PosterUrl),
                    NullIfBlank(movie.BackdropUrl),
                    NullIfBlank(movie.ExternalUrl),
                    NullIfBlank(movie.ImdbId)))
                .ToArray());
    }

    private async Task<MetadataCollection?> GetTmdbCollectionAsync(
        int id,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var response = await GetJsonWithResilienceAsync<TmdbCollectionResponse>(
            $"https://api.themoviedb.org/3/collection/{id.ToString(CultureInfo.InvariantCulture)}?api_key={Uri.EscapeDataString(apiKey)}",
            "metadata:tmdb:collection",
            "metadata.tmdb.collection",
            cancellationToken);

        if (response is null || string.IsNullOrWhiteSpace(response.Name))
        {
            return null;
        }

        return new MetadataCollection(
            Provider: ProviderName,
            ProviderId: response.Id.ToString(CultureInfo.InvariantCulture),
            Name: response.Name.Trim(),
            Overview: NullIfBlank(response.Overview),
            PosterUrl: BuildTmdbArtworkUrl(response.PosterPath, ArtworkSizes.Poster),
            BackdropUrl: BuildTmdbArtworkUrl(response.BackdropPath, ArtworkSizes.Backdrop),
            Movies: (response.Parts ?? [])
                .Where(movie => movie.Id > 0 && !string.IsNullOrWhiteSpace(movie.Title))
                .Select(movie => new MetadataCollectionMovie(
                    movie.Id.ToString(CultureInfo.InvariantCulture),
                    movie.Title!.Trim(),
                    TryParseYear(movie.ReleaseDate),
                    NullIfBlank(movie.Overview),
                    BuildTmdbArtworkUrl(movie.PosterPath, ArtworkSizes.Poster),
                    BuildTmdbArtworkUrl(movie.BackdropPath, ArtworkSizes.Backdrop),
                    BuildTmdbUrl("movie", movie.Id)))
                .ToArray());
    }

    private static string? BuildTmdbArtworkUrl(string? path, string size)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : $"https://image.tmdb.org/t/p/{size}{path}";

    /// <summary>
    /// A gallery beyond the single hero backdrop: every other backdrop TMDb
    /// has, skipping whichever path was already chosen as the primary one so
    /// the gallery does not repeat it (#864).
    /// </summary>
    private static IReadOnlyList<string>? BuildScreenshotUrls(
        IReadOnlyList<TmdbImage>? backdrops,
        string? primaryBackdropPath)
    {
        if (backdrops is null || backdrops.Count == 0)
        {
            return null;
        }

        var urls = backdrops
            .Where(image => !string.IsNullOrWhiteSpace(image.FilePath)
                && !string.Equals(image.FilePath, primaryBackdropPath, StringComparison.Ordinal))
            .Select(image => BuildTmdbArtworkUrl(image.FilePath, ArtworkSizes.Backdrop))
            .Where(url => url is not null)
            .Take(10)
            .Select(url => url!)
            .ToArray();
        return urls.Length == 0 ? null : urls;
    }

    /// <summary>
    /// Every episode's real broadcast instant TVmaze has for this show,
    /// keyed by season and episode number. TVmaze's own <c>airstamp</c> is
    /// already the network's local air time converted to an offset
    /// timestamp, which is exactly what an episode without one collapses to
    /// midnight UTC for lack of (#860). Chosen over TheTVDB because it needs
    /// no API key and answers a lookup-by-IMDb-id directly; keyless also
    /// means no settings plumbing to gate it behind.
    /// </summary>
    private async Task<IReadOnlyDictionary<(int Season, int Episode), DateTimeOffset>> GetTvMazeAirTimesAsync(
        string? imdbId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return EmptyAirTimes;
        }

        var show = await GetJsonWithResilienceAsync<TvMazeShow>(
            $"https://api.tvmaze.com/lookup/shows?imdb={Uri.EscapeDataString(imdbId)}",
            $"metadata:tvmaze:lookup:{imdbId}",
            "metadata.tvmaze.lookup",
            cancellationToken);
        if (show?.Id is not { } showId)
        {
            return EmptyAirTimes;
        }

        var episodes = await GetJsonWithResilienceAsync<IReadOnlyList<TvMazeEpisode>>(
            $"https://api.tvmaze.com/shows/{showId.ToString(CultureInfo.InvariantCulture)}/episodes",
            $"metadata:tvmaze:episodes:{showId}",
            "metadata.tvmaze.episodes",
            cancellationToken);
        if (episodes is not { Count: > 0 })
        {
            return EmptyAirTimes;
        }

        var airTimes = new Dictionary<(int Season, int Episode), DateTimeOffset>();
        foreach (var episode in episodes)
        {
            if (episode.Season is not { } season || episode.Number is not { } number
                || string.IsNullOrWhiteSpace(episode.Airstamp))
            {
                continue;
            }

            if (DateTimeOffset.TryParse(episode.Airstamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var airstamp))
            {
                airTimes[(season, number)] = airstamp;
            }
        }

        return airTimes;
    }

    private static readonly IReadOnlyDictionary<(int Season, int Episode), DateTimeOffset> EmptyAirTimes =
        new Dictionary<(int Season, int Episode), DateTimeOffset>();

    private sealed record TvMazeShow([property: JsonPropertyName("id")] int? Id);

    private sealed record TvMazeEpisode(
        [property: JsonPropertyName("season")] int? Season,
        [property: JsonPropertyName("number")] int? Number,
        [property: JsonPropertyName("airstamp")] string? Airstamp);

    private async Task<IReadOnlyList<MetadataSeason>> GetTmdbCatalogueAsync(
        int id,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var detail = await GetJsonWithResilienceAsync<TmdbSeriesSeasons>(
            $"https://api.themoviedb.org/3/tv/{id}?api_key={Uri.EscapeDataString(apiKey)}",
            "metadata:tmdb:catalogue",
            "metadata.tmdb.catalogue",
            cancellationToken);

        if (detail?.Seasons is not { Count: > 0 })
        {
            return [];
        }

        // TMDb has no broadcast time at all (#860); TVmaze does, keyed off
        // the same IMDb id every other cross-reference in this file already
        // resolves. Best-effort: a show TVmaze does not carry (or is down)
        // leaves every episode on TMDb's midnight-UTC date, unchanged from
        // before this existed.
        var externalIds = await GetExternalIdsAsync(id, "tv", apiKey, cancellationToken);
        var realBroadcastTimes = await GetTvMazeAirTimesAsync(externalIds.ImdbId, cancellationToken);

        var seasons = new List<MetadataSeason>();
        foreach (var season in detail.Seasons.Where(item => item.SeasonNumber >= 0).OrderBy(item => item.SeasonNumber).Take(50))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var seasonDetail = await GetJsonWithResilienceAsync<TmdbSeasonDetail>(
                $"https://api.themoviedb.org/3/tv/{id}/season/{season.SeasonNumber.ToString(CultureInfo.InvariantCulture)}?api_key={Uri.EscapeDataString(apiKey)}",
                "metadata:tmdb:catalogue:season",
                "metadata.tmdb.catalogue.season",
                cancellationToken);

            // One unavailable season must not lose the rest of the catalogue.
            if (seasonDetail?.Episodes is null)
            {
                continue;
            }

            var episodes = seasonDetail.Episodes
                .Where(episode => episode.EpisodeNumber > 0)
                .Select(episode => new MetadataEpisode(
                    season.SeasonNumber,
                    episode.EpisodeNumber,
                    NullIfBlank(episode.Name),
                    NullIfBlank(episode.Overview),
                    ParseAirDate(
                        episode.AirDate,
                        realBroadcastTimes.TryGetValue((season.SeasonNumber, episode.EpisodeNumber), out var broadcastTime)
                            ? broadcastTime
                            : null)))
                .ToArray();

            seasons.Add(new MetadataSeason(
                season.SeasonNumber,
                NullIfBlank(season.Name),
                episodes.Length,
                ParseDateOnly(season.AirDate),
                episodes));
        }

        return seasons;
    }

    /// <summary>
    /// TMDb's episode_groups endpoint lists every alternate ordering by name
    /// and type but not its episodes; each one's episodes need a second call
    /// to <c>/tv/episode_group/{id}</c> (#863). Only fetched for the types
    /// Deluno actually resolves episodes by (<see cref="TmdbEpisodeGroupTypes.Absolute"/>,
    /// <see cref="TmdbEpisodeGroupTypes.Dvd"/>) - every other type is still
    /// returned, with no episodes, so a series catalogue can at least name
    /// what else TMDb offers. When TMDb lists more than one group of the same
    /// resolvable type (more than one "DVD order", say) the one with the most
    /// episodes is kept, on the theory that a fuller group is more likely to
    /// be the maintained one.
    /// </summary>
    private async Task<IReadOnlyList<MetadataEpisodeGroup>> GetTmdbEpisodeGroupsAsync(
        int id,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var summary = await GetJsonWithResilienceAsync<TmdbEpisodeGroupsResponse>(
            $"https://api.themoviedb.org/3/tv/{id}/episode_groups?api_key={Uri.EscapeDataString(apiKey)}",
            "metadata:tmdb:episode-groups",
            "metadata.tmdb.episode-groups",
            cancellationToken);

        if (summary?.Results is not { Count: > 0 })
        {
            return [];
        }

        var resolvableTypes = new[] { TmdbEpisodeGroupTypes.Absolute, TmdbEpisodeGroupTypes.Dvd };
        var groups = new List<MetadataEpisodeGroup>();

        foreach (var grouping in summary.Results
                     .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Id) && !string.IsNullOrWhiteSpace(candidate.Name))
                     .GroupBy(candidate => candidate.Type))
        {
            if (!resolvableTypes.Contains(grouping.Key))
            {
                // Not one Deluno resolves episodes by yet - still surface its
                // name (the first one, if TMDb lists several) so the title's
                // available orderings can say what else TMDb offers.
                var first = grouping.First();
                groups.Add(new MetadataEpisodeGroup(grouping.Key, first.Id!.Trim(), first.Name!.Trim(), []));
                continue;
            }

            var chosen = grouping.OrderByDescending(candidate => candidate.EpisodeCount).First();
            cancellationToken.ThrowIfCancellationRequested();

            var detail = await GetJsonWithResilienceAsync<TmdbEpisodeGroupDetail>(
                $"https://api.themoviedb.org/3/tv/episode_group/{Uri.EscapeDataString(chosen.Id!)}?api_key={Uri.EscapeDataString(apiKey)}",
                "metadata:tmdb:episode-group:detail",
                "metadata.tmdb.episode-group.detail",
                cancellationToken);

            if (detail?.Groups is not { Count: > 0 })
            {
                groups.Add(new MetadataEpisodeGroup(grouping.Key, chosen.Id!.Trim(), chosen.Name!.Trim(), []));
                continue;
            }

            var episodes = new List<MetadataEpisodeGroupEpisode>();
            if (grouping.Key == TmdbEpisodeGroupTypes.Absolute)
            {
                // Absolute order has no season concept: one running count
                // across every subgroup TMDb split the episodes into.
                var position = 0;
                foreach (var episode in detail.Groups.SelectMany(subgroup => subgroup.Episodes ?? []))
                {
                    if (episode.SeasonNumber is null || episode.EpisodeNumber is null)
                    {
                        continue;
                    }

                    position++;
                    episodes.Add(new MetadataEpisodeGroupEpisode(
                        episode.SeasonNumber.Value,
                        episode.EpisodeNumber.Value,
                        GroupSeasonNumber: 1,
                        GroupEpisodeNumber: position));
                }
            }
            else
            {
                // DVD order keeps its own season structure: each subgroup is
                // one renumbered season, in the order TMDb lists them.
                var seasonNumber = 0;
                foreach (var subgroup in detail.Groups)
                {
                    seasonNumber++;
                    var episodeNumber = 0;
                    foreach (var episode in subgroup.Episodes ?? [])
                    {
                        if (episode.SeasonNumber is null || episode.EpisodeNumber is null)
                        {
                            continue;
                        }

                        episodeNumber++;
                        episodes.Add(new MetadataEpisodeGroupEpisode(
                            episode.SeasonNumber.Value,
                            episode.EpisodeNumber.Value,
                            seasonNumber,
                            episodeNumber));
                    }
                }
            }

            groups.Add(new MetadataEpisodeGroup(grouping.Key, chosen.Id!.Trim(), chosen.Name!.Trim(), episodes));
        }

        return groups;
    }

    /// <summary>
    /// TMDb reports release dates per country and per type, so this takes the
    /// earliest of each across every region rather than guessing one. Types:
    /// 2 limited, 3 theatrical, 4 digital, 5 physical. A premiere (1) is a
    /// festival screening, not something anyone can obtain, so it is ignored.
    /// </summary>
    private async Task<MetadataReleaseDates> GetTmdbReleaseDatesAsync(
        int id,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var response = await GetJsonWithResilienceAsync<TmdbReleaseDatesResponse>(
            $"https://api.themoviedb.org/3/movie/{id}/release_dates?api_key={Uri.EscapeDataString(apiKey)}",
            "metadata:tmdb:release-dates",
            "metadata.tmdb.release-dates",
            cancellationToken);

        if (response?.Results is not { Count: > 0 })
        {
            return MetadataReleaseDates.None;
        }

        DateOnly? cinemas = null;
        DateOnly? digital = null;
        DateOnly? physical = null;

        foreach (var entry in response.Results.SelectMany(country => country.ReleaseDates ?? []))
        {
            var date = ParseDateOnly(entry.ReleaseDate);
            if (date is null)
            {
                continue;
            }

            if (entry.Type is 2 or 3)
            {
                if (cinemas is null || date < cinemas) cinemas = date;
            }
            else if (entry.Type is 4)
            {
                if (digital is null || date < digital) digital = date;
            }
            else if (entry.Type is 5)
            {
                if (physical is null || date < physical) physical = date;
            }
        }

        return new MetadataReleaseDates(cinemas, digital, physical);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateOnly? ParseDateOnly(string? value)
        => value is not null && DateOnly.TryParse(
            value.Length >= 10 ? value[..10] : value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// TMDb gives only a calendar date, so this used to collapse every
    /// episode to exactly midnight UTC. <paramref name="realBroadcastTime"/>
    /// is the network's actual broadcast instant, when a source that has one
    /// (TVmaze) could match this episode; it wins over the midnight guess.
    /// Falls back to midnight only when no such source had this episode
    /// (#860).
    /// </summary>
    private static DateTimeOffset? ParseAirDate(string? value, DateTimeOffset? realBroadcastTime = null)
        => realBroadcastTime
           ?? (ParseDateOnly(value) is { } date
               ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
               : null);

    private sealed record BrokerCatalogueResponse(
        [property: JsonPropertyName("seasons")] IReadOnlyList<BrokerSeason>? Seasons);

    private sealed record BrokerSeason(
        [property: JsonPropertyName("seasonNumber")] int SeasonNumber,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("airDate")] string? AirDate,
        [property: JsonPropertyName("episodes")] IReadOnlyList<BrokerEpisode>? Episodes);

    private sealed record BrokerEpisode(
        [property: JsonPropertyName("episodeNumber")] int EpisodeNumber,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("airDate")] string? AirDate);

    private sealed record BrokerEpisodeGroupsResponse(
        [property: JsonPropertyName("groups")] IReadOnlyList<BrokerEpisodeGroup>? Groups);

    private sealed record BrokerEpisodeGroup(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("providerGroupId")] string? ProviderGroupId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("episodes")] IReadOnlyList<BrokerEpisodeGroupEpisode>? Episodes);

    private sealed record BrokerEpisodeGroupEpisode(
        [property: JsonPropertyName("originalSeasonNumber")] int OriginalSeasonNumber,
        [property: JsonPropertyName("originalEpisodeNumber")] int OriginalEpisodeNumber,
        [property: JsonPropertyName("groupSeasonNumber")] int GroupSeasonNumber,
        [property: JsonPropertyName("groupEpisodeNumber")] int GroupEpisodeNumber);

    private sealed record BrokerReleaseDatesResponse(
        [property: JsonPropertyName("inCinemas")] string? InCinemas,
        [property: JsonPropertyName("digital")] string? Digital,
        [property: JsonPropertyName("physical")] string? Physical);

    private sealed record BrokerCollectionResponse(
        [property: JsonPropertyName("providerId")] string? ProviderId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("posterUrl")] string? PosterUrl,
        [property: JsonPropertyName("backdropUrl")] string? BackdropUrl,
        [property: JsonPropertyName("movies")] IReadOnlyList<BrokerCollectionMovie>? Movies);

    private sealed record BrokerCollectionMovie(
        [property: JsonPropertyName("providerId")] string? ProviderId,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("year")] int? Year,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("posterUrl")] string? PosterUrl,
        [property: JsonPropertyName("backdropUrl")] string? BackdropUrl,
        [property: JsonPropertyName("externalUrl")] string? ExternalUrl,
        [property: JsonPropertyName("imdbId")] string? ImdbId);

    private sealed record TmdbCollectionResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
        [property: JsonPropertyName("parts")] IReadOnlyList<TmdbCollectionPart>? Parts);

    private sealed record TmdbCollectionPart(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath);

    private sealed record TmdbSeriesSeasons(
        [property: JsonPropertyName("seasons")] IReadOnlyList<TmdbSeasonSummary>? Seasons);

    private sealed record TmdbSeasonSummary(
        [property: JsonPropertyName("season_number")] int SeasonNumber,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("air_date")] string? AirDate,
        [property: JsonPropertyName("episode_count")] int EpisodeCount);

    private sealed record TmdbSeasonDetail(
        [property: JsonPropertyName("episodes")] IReadOnlyList<TmdbSeasonEpisode>? Episodes);

    private sealed record TmdbSeasonEpisode(
        [property: JsonPropertyName("episode_number")] int EpisodeNumber,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("air_date")] string? AirDate);

    private sealed record TmdbEpisodeGroupsResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<TmdbEpisodeGroupSummary>? Results);

    private sealed record TmdbEpisodeGroupSummary(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("episode_count")] int EpisodeCount);

    private sealed record TmdbEpisodeGroupDetail(
        [property: JsonPropertyName("groups")] IReadOnlyList<TmdbEpisodeGroupSubgroup>? Groups);

    private sealed record TmdbEpisodeGroupSubgroup(
        [property: JsonPropertyName("order")] int Order,
        [property: JsonPropertyName("episodes")] IReadOnlyList<TmdbEpisodeGroupEpisode>? Episodes);

    private sealed record TmdbEpisodeGroupEpisode(
        [property: JsonPropertyName("season_number")] int? SeasonNumber,
        [property: JsonPropertyName("episode_number")] int? EpisodeNumber);

    private sealed record TmdbReleaseDatesResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<TmdbCountryReleaseDates>? Results);

    private sealed record TmdbCountryReleaseDates(
        [property: JsonPropertyName("iso_3166_1")] string? Country,
        [property: JsonPropertyName("release_dates")] IReadOnlyList<TmdbReleaseDateEntry>? ReleaseDates);

    private sealed record TmdbReleaseDateEntry(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("certification")] string? Certification = null);

    private sealed record TmdbContentRatingsResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<TmdbContentRatingEntry>? Results);

    private sealed record TmdbContentRatingEntry(
        [property: JsonPropertyName("iso_3166_1")] string? Country,
        [property: JsonPropertyName("rating")] string? Rating);

    private async Task<TmdbExternalIds> GetExternalIdsAsync(
        int id,
        string mediaType,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var path = mediaType == "tv" ? "tv" : "movie";
        var url = $"https://api.themoviedb.org/3/{path}/{id}/external_ids?api_key={Uri.EscapeDataString(apiKey)}";
        return await GetJsonWithResilienceAsync<TmdbExternalIds>(
            url,
            $"metadata:tmdb:external-ids:{mediaType}",
            "metadata.tmdb.external-ids",
            cancellationToken) ?? new TmdbExternalIds(null);
    }

    private static IReadOnlyList<string> ResolveGenreNames(string mediaType, IReadOnlyList<int>? ids)
    {
        if (ids is not { Count: > 0 })
        {
            return [];
        }

        var map = mediaType == "tv" ? TvGenres : MovieGenres;
        return ids
            .Select(id => map.TryGetValue(id, out var name) ? name : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
    }

    /// <summary>
    /// The name to put in front of a metadata failure, taken from the
    /// resilience key ("metadata:tmdb:...", "metadata:omdb:...",
    /// "metadata:broker:...").
    ///
    /// <para>Derived rather than passed, because there are sixteen call sites
    /// and a name each of them has to remember is a name fifteen of them will
    /// eventually get wrong.</para>
    /// </summary>
    private static string MetadataServiceName(string key)
    {
        var parts = key.Split(':', StringSplitOptions.RemoveEmptyEntries);
        var source = parts.Length > 1 ? parts[1] : parts.FirstOrDefault() ?? "metadata";
        return source.ToLowerInvariant() switch
        {
            "tmdb" => "TMDb",
            "omdb" => "OMDb",
            "broker" => "the metadata service",
            _ => source
        };
    }

    /// <summary>
    /// When set, calls made inside the block do not touch the process-wide
    /// "last failure": TheTVDB is an optional first choice with TMDb behind it,
    /// so its failing is reported to the work that asked (the failure scope)
    /// and does not make the whole metadata service look down.
    /// </summary>
    private static readonly AsyncLocal<bool> QuietCalls = new();

    private void NoteServiceFailure(IntegrationFailure failure)
    {
        Interlocked.Exchange(ref _lastFailure, failure);
        if (!CountsAsServiceFailure(failure))
        {
            return;
        }

        ReportToScope(failure);
        if (!QuietCalls.Value)
        {
            _health.RecordFailure(failure);
        }
    }

    /// <summary>
    /// The wait a service asked for: Retry-After first, then the common
    /// rate-limit headers. Returns null when the response asked for nothing.
    /// </summary>
    private static TimeSpan? ReadRequestedPause(HttpResponseMessage response, DateTimeOffset now)
    {
        if (IntegrationResiliencePolicy.TryGetRetryAfter(response) is { } retryAfter && retryAfter > TimeSpan.Zero)
        {
            return retryAfter;
        }

        if (TryReadHeaderNumber(response, "X-RateLimit-Retry-After") is { } retryAfterSeconds && retryAfterSeconds > 0)
        {
            return TimeSpan.FromSeconds(retryAfterSeconds);
        }

        if (TryReadHeaderNumber(response, "X-RateLimit-Reset") is { } reset && reset > 0)
        {
            // Either seconds from now or an epoch second; an epoch is far larger than any wait.
            var seconds = reset > 100_000_000 ? reset - now.ToUnixTimeSeconds() : reset;
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }

        return null;
    }

    private static double? TryReadHeaderNumber(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
           && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private async Task<T?> GetJsonWithResilienceAsync<T>(
        string url,
        string key,
        string operation,
        CancellationToken cancellationToken)
    {
        var serviceName = MetadataServiceName(key);
        var host = ThrottleHost(url);

        // A pause the service itself asked for (a 429's Retry-After). Honoured
        // before anything else: a short one is waited out, a long one defers the
        // call instead of tying up a job lane for a minute (#1090).
        var pausedFor = _health.PausedFor(host);
        if (pausedFor > TimeSpan.Zero)
        {
            if (pausedFor > _health.MaxPauseWait)
            {
                var pausedFailure = IntegrationFailureFactory.FromLegacy(
                    "metadata",
                    key,
                    serviceName,
                    operation,
                    "rate-limited",
                    "The metadata service asked Deluno to wait before calling it again.",
                    retryAfterUtc: timeProvider.GetUtcNow().Add(pausedFor));
                ReportToScope(pausedFailure);
                Interlocked.Exchange(ref _lastFailure, pausedFailure);
                return default;
            }

            await Task.Delay(pausedFor, timeProvider, cancellationToken);
        }

        // Paced before the request. A freshly imported library asks this
        // roughly once per title; at 20,000 titles and no pacing that is a
        // sustained burst, which is how a run during this work collected 394
        // rate-limit responses out of about 20,000 requests. The gateway's own
        // limit is far lower than TMDb's (30 a minute per address), so calls to
        // it are paced to that instead (#1090).
        var isGateway = key.StartsWith("metadata:broker:", StringComparison.OrdinalIgnoreCase);
        if (await outboundRequestThrottle.TryAcquireAsync(
                host,
                isGateway ? OutboundRate.MetadataGatewayDefault : OutboundRate.MetadataProviderDefault,
                MaxMetadataThrottleWait,
                cancellationToken) is null)
        {
            logger.LogInformation(
                "Deferred {Operation}: the metadata provider is still inside its request budget after {Wait}.",
                operation,
                MaxMetadataThrottleWait);

            // Deluno's own pacing, not the service failing: it is reported to the
            // work that asked, never to the status the person reads.
            var deferredFailure = IntegrationFailureFactory.FromLegacy(
                    "metadata",
                    key,
                    serviceName,
                    operation,
                    "rate-limited",
                    "The metadata request was deferred because the provider budget was exhausted.",
                    retryAfterUtc: timeProvider.GetUtcNow().Add(MaxMetadataThrottleWait));
            ReportToScope(deferredFailure);
            Interlocked.Exchange(ref _lastFailure, deferredFailure);

            return default;
        }

        IntegrationFailure? boundaryFailure = null;
        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                key,
                operation,
                FailureThreshold: 2,
                // Without these the policy has no name to build a failure with,
                // so it falls back to the operation string and the summary
                // reads "metadata.broker.resolve metadata.broker.resolve
                // failed: ..." - the same defect #369 fixed for indexers and
                // download clients, still true of every metadata call (#338).
                ServiceType: "metadata",
                ServiceId: key,
                ServiceName: serviceName),
            async token =>
            {
                try
                {
                    // About ten seconds a call. The client's own timeout is a
                    // backstop above this; without one a dead gateway held a
                    // call for 100 s, three times over.
                    using var timeout = new CancellationTokenSource(_health.CallTimeout, timeProvider);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
                    using var response = await httpClient.GetAsync(url, linked.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        var requestedPause = ReadRequestedPause(response, timeProvider.GetUtcNow());
                        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        {
                            // TMDb and the gateway say exactly how long to wait;
                            // keep that for every call that follows (not only the
                            // retry of this one) and pass it through the typed
                            // exception so the resilience policy honours it
                            // instead of guessing (#865, #1090).
                            var wait = requestedPause ?? TimeSpan.FromSeconds(10);
                            var until = _health.PauseFor(host, wait);
                            if (wait > _health.MaxPauseWait)
                            {
                                // Too long to sit out here, and not the service
                                // being broken: defer, and do not count it towards
                                // opening the circuit.
                                boundaryFailure = IntegrationFailureFactory.FromLegacy(
                                    "metadata",
                                    key,
                                    serviceName,
                                    operation,
                                    "rate-limited",
                                    "The metadata service asked Deluno to wait before calling it again.",
                                    retryAfterUtc: until);
                                return default;
                            }

                            throw new IntegrationRateLimitedException("The service returned HTTP 429.", wait);
                        }

                        if (IntegrationResiliencePolicy.IsTransientHttpStatusCode(response.StatusCode))
                        {
                            if (requestedPause is { } busyFor)
                            {
                                _health.PauseFor(host, busyFor);
                            }

                            // "The service returned HTTP 503" - not the name of
                            // the Deluno function that asked. The operation is
                            // already a field on the failure.
                            throw new HttpRequestException(
                                $"The service returned HTTP {(int)response.StatusCode}.",
                                null,
                                response.StatusCode);
                        }

                        boundaryFailure = IntegrationFailureFactory.FromHttpStatus(
                            "metadata",
                            key,
                            serviceName,
                            operation,
                            response.StatusCode);
                        return default;
                    }

                    if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
                        && remaining.FirstOrDefault() == "0"
                        && ReadRequestedPause(response, timeProvider.GetUtcNow()) is { } exhaustedFor)
                    {
                        // The budget is spent: wait for the window instead of
                        // spending the next request on a 429.
                        _health.PauseFor(host, exhaustedFor);
                    }

                    return await response.Content.ReadFromJsonAsync<T>(CacheJsonOptions, linked.Token);
                }
                catch (Exception exception) when (exception is not HttpRequestException and not OperationCanceledException and not IOException)
                {
                    boundaryFailure = IntegrationFailureFactory.FromException(
                        "metadata",
                        key,
                        serviceName,
                        operation,
                        exception);
                    return default;
                }
            },
            value => value is null
                ? IntegrationResilienceOutcome.NonRetryableFailure
                : IntegrationResilienceOutcome.Success,
            cancellationToken);

        if (result.Value is null)
        {
            var failure = result.Failure
                ?? boundaryFailure
                ?? IntegrationFailureFactory.FromLegacy(
                    "metadata",
                    key,
                    serviceName,
                    operation,
                    "unknown",
                    "The metadata provider returned no usable response.");
            NoteServiceFailure(failure);
        }
        else
        {
            Interlocked.Exchange(ref _lastFailure, null);
        }

        if (result.Value is not null && !QuietCalls.Value)
        {
            _health.RecordSuccess();
        }

        return result.Value;
    }

    private static string BuildHostKey(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}";
        }

        return url.Split('?', 2)[0].Trim().ToLowerInvariant();
    }

    /// <summary>
    /// The host a request is paced against. Relative URLs go through the
    /// configured base address, so they share one budget with the absolute ones.
    /// </summary>
    private string ThrottleHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            ? absolute.Host
            : httpClient.BaseAddress?.Host ?? "metadata-provider";

    private static string NormalizeMediaType(string? mediaType)
    {
        var normalized = mediaType?.Trim().ToLowerInvariant();
        return normalized is "tv" or "shows" or "series" ? "tv" : "movies";
    }

    private async Task<IReadOnlyList<MetadataRatingItem>> BuildRatingsAsync(
        string mediaType,
        int providerId,
        double? voteAverage,
        int? voteCount,
        string? imdbId,
        CancellationToken cancellationToken)
    {
        var ratings = new List<MetadataRatingItem>();
        if (voteAverage is null)
        {
            return await AddOmdbRatingsAsync(ratings, imdbId, cancellationToken);
        }

        ratings.Add(
            new MetadataRatingItem(
                Source: "tmdb",
                Label: "TMDb",
                Score: Math.Round(voteAverage.Value, 1),
                MaxScore: 10,
                VoteCount: voteCount,
                Url: BuildTmdbUrl(mediaType, providerId),
                Kind: "community"));

        return await AddOmdbRatingsAsync(ratings, imdbId, cancellationToken);
    }

    private async Task<IReadOnlyList<MetadataRatingItem>> AddOmdbRatingsAsync(
        List<MetadataRatingItem> ratings,
        string? imdbId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return ratings;
        }

        var apiKey = await GetOmdbApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ratings;
        }

        var url =
            $"https://www.omdbapi.com/?apikey={Uri.EscapeDataString(apiKey)}&i={Uri.EscapeDataString(imdbId)}&plot=short&r=json";

        var item = await GetJsonWithResilienceAsync<OmdbTitleResponse>(
            url,
            "metadata:omdb:ratings",
            "metadata.omdb.ratings",
            cancellationToken);
        if (item is null || string.Equals(item.Response, "False", StringComparison.OrdinalIgnoreCase))
        {
            return ratings;
        }

        AddRatingIfPresent(
            ratings,
            "imdb",
            "IMDb",
            ParseFraction(item.ImdbRating, 10),
            10,
            ParseVotes(item.ImdbVotes),
            BuildImdbUrl(imdbId),
            "community");

        foreach (var rating in item.Ratings ?? [])
        {
            var source = NormalizeOmdbSource(rating.Source);
            if (source is null)
            {
                continue;
            }

            var parsed = ParseOmdbRating(rating.Value);
            AddRatingIfPresent(
                ratings,
                source.Value.Source,
                source.Value.Label,
                parsed.Score,
                parsed.MaxScore,
                null,
                null,
                source.Value.Kind);
        }

        if (int.TryParse(item.Metascore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var metascore))
        {
            AddRatingIfPresent(ratings, "metacritic", "Metacritic", metascore, 100, null, null, "critic");
        }

        return ratings
            .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    /// <summary>How many of the billed cast a detail page is worth showing.</summary>
    private const int MaxCast = 30;

    private const int MaxCrew = 20;

    /// <summary>
    /// The crew jobs a viewer recognises, in the order a title card lists them.
    ///
    /// <para>TMDb's crew array is exhaustive — every runner and assistant — and
    /// ordered by nothing useful, so it cannot be taken as it comes.</para>
    /// </summary>
    private static readonly string[] CrewJobs =
    [
        "Director", "Screenplay", "Writer", "Story", "Novel", "Characters",
        "Producer", "Executive Producer", "Original Music Composer", "Music",
        "Director of Photography", "Editor", "Production Design", "Art Direction",
        "Costume Design", "Casting", "Visual Effects Supervisor"
    ];

    private static string? PersonId(int id) => id > 0 ? id.ToString() : null;

    private static string? Portrait(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : $"https://image.tmdb.org/t/p/{ArtworkSizes.Portrait}{path}";

    /// <summary>
    /// The billed cast, not the first ten of it. TMDb bills them in order, so
    /// the cut can simply be made past the point where a name still means
    /// something to a viewer.
    /// </summary>
    private static IReadOnlyList<MetadataCastMember> ReadCast(IReadOnlyList<TmdbCastMember>? cast) =>
        [.. (cast ?? [])
            .Where(member => !string.IsNullOrWhiteSpace(member.Name))
            .Take(MaxCast)
            .Select(member => new MetadataCastMember(member.Name!, member.Character, Portrait(member.ProfilePath), PersonId(member.Id)))];

    /// <summary>
    /// One row per person, in <see cref="CrewJobs"/> order, with every job that
    /// person did joined onto it — Villeneuve directs and produces, and two
    /// identical portraits in a row reads as a bug rather than a fuller credit.
    /// </summary>
    private static IReadOnlyList<MetadataCrewMember> ReadCrew(IReadOnlyList<TmdbCrewMember>? crew)
    {
        var byPerson = new Dictionary<string, (string Name, List<string> Jobs, string? Portrait, string? PersonId)>();
        var order = new List<string>();

        foreach (var job in CrewJobs)
        {
            foreach (var member in crew ?? [])
            {
                if (member.Job != job || string.IsNullOrWhiteSpace(member.Name)) continue;

                var key = member.Id > 0 ? member.Id.ToString() : member.Name!;
                if (byPerson.TryGetValue(key, out var existing))
                {
                    if (!existing.Jobs.Contains(job)) existing.Jobs.Add(job);
                    continue;
                }

                byPerson[key] = (member.Name!.Trim(), [job], Portrait(member.ProfilePath), PersonId(member.Id));
                order.Add(key);
            }
        }

        return [.. order
            .Take(MaxCrew)
            .Select(key => byPerson[key])
            .Select(person => new MetadataCrewMember(person.Name, string.Join(", ", person.Jobs), person.Portrait, person.PersonId))];
    }

    private static string BuildTmdbUrl(string mediaType, int providerId)
        => $"https://www.themoviedb.org/{(mediaType == "tv" ? "tv" : "movie")}/{providerId.ToString(CultureInfo.InvariantCulture)}";

    private static string BuildImdbUrl(string imdbId)
        => $"https://www.imdb.com/title/{imdbId}/";

    private static void AddRatingIfPresent(
        List<MetadataRatingItem> ratings,
        string source,
        string label,
        double? score,
        double? maxScore,
        int? voteCount,
        string? url,
        string kind)
    {
        if (score is null && voteCount is null)
        {
            return;
        }

        ratings.Add(new MetadataRatingItem(source, label, score, maxScore, voteCount, url, kind));
    }

    private static (string Source, string Label, string Kind)? NormalizeOmdbSource(string? source)
    {
        return source?.Trim().ToLowerInvariant() switch
        {
            "internet movie database" => ("imdb", "IMDb", "community"),
            "rotten tomatoes" => ("rotten_tomatoes", "Rotten Tomatoes", "critic"),
            "metacritic" => ("metacritic", "Metacritic", "critic"),
            _ => null
        };
    }

    private static (double? Score, double? MaxScore) ParseOmdbRating(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var trimmed = value.Trim();
        if (trimmed.EndsWith('%') &&
            double.TryParse(trimmed.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return (percent, 100);
        }

        var parts = trimmed.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var score) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxScore))
        {
            return (score, maxScore);
        }

        return (null, null);
    }

    private static double? ParseFraction(string? value, double maxScore)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Min(parsed, maxScore)
            : null;
    }

    private static int? ParseVotes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = value.Replace(",", string.Empty, StringComparison.Ordinal);
        return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? TryParseYear(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.Year
            : null;

    private static IReadOnlyList<string> SplitGenres(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string? NormalizeOmdbPoster(string? poster)
    {
        if (string.IsNullOrWhiteSpace(poster) ||
            string.Equals(poster.Trim(), "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return poster.Trim();
    }

    private static string ResolveArtworkExtension(Uri uri)
    {
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 6)
        {
            return ".jpg";
        }

        return extension.ToLowerInvariant();
    }

    private static string ResolveContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [System.Text.RegularExpressions.GeneratedRegex("/api/metadata/artwork/([0-9a-fA-F]{64})")]
    private static partial System.Text.RegularExpressions.Regex CachedSearchArtworkKey();

    private sealed record ArtworkRow(string RemoteUrl, string MediaType);

    private sealed record ArtworkCacheEntry(
        string CacheKey,
        string? LocalPath,
        string FetchedText,
        DateTimeOffset FetchedUtc);

    private sealed record MetadataProviderConfiguration(
        string ProviderMode,
        string? BrokerUrl,
        string? TmdbApiKey,
        string? OmdbApiKey,
        string? Language = null,
        string CertificationCountry = "US",
        bool TvdbDirectKey = false);

    private sealed record MetadataBrokerSearchResponse(
        string Provider,
        string Mode,
        int ResultCount,
        IReadOnlyList<MetadataSearchResult> Results,
        IntegrationFailure? Failure = null);

    private sealed record TmdbSearchResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<TmdbSearchItem>? Results);

    private sealed record TmdbFindResponse(
        [property: JsonPropertyName("movie_results")] IReadOnlyList<TmdbSearchItem>? MovieResults,
        [property: JsonPropertyName("tv_results")] IReadOnlyList<TmdbSearchItem>? TvResults);

    private sealed record TmdbSearchItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("original_title")] string? OriginalTitle,
        [property: JsonPropertyName("original_name")] string? OriginalName,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("vote_average")] double? VoteAverage,
        [property: JsonPropertyName("vote_count")] int? VoteCount,
        [property: JsonPropertyName("popularity")] double? Popularity,
        [property: JsonPropertyName("genre_ids")] IReadOnlyList<int>? GenreIds);

    private sealed record TmdbExternalIds(
        [property: JsonPropertyName("imdb_id")] string? ImdbId,
        // TMDb sends this as a JSON number; a plain string property refused the
        // whole record, so a direct-mode TV lookup came back empty.
        [property: JsonPropertyName("tvdb_id"), JsonConverter(typeof(NumberOrStringConverter))] string? TvDbId = null);

    private sealed record TmdbDetailItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("original_title")] string? OriginalTitle,
        [property: JsonPropertyName("original_name")] string? OriginalName,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("vote_average")] double? VoteAverage,
        [property: JsonPropertyName("vote_count")] int? VoteCount,
        [property: JsonPropertyName("popularity")] double? Popularity,
        // A movie has one runtime; a show has a runtime per episode, and TMDB
        // returns a list of them because it varies. The first is the useful one.
        [property: JsonPropertyName("runtime")] int? Runtime,
        [property: JsonPropertyName("episode_run_time")] IReadOnlyList<int>? EpisodeRunTime,
        [property: JsonPropertyName("genres")] IReadOnlyList<TmdbGenre>? Genres,
        [property: JsonPropertyName("external_ids")] TmdbExternalIds? ExternalIds,
        [property: JsonPropertyName("credits")] TmdbCredits? Credits,
        // Everything below arrives on an ordinary detail call and was thrown
        // away. The metadata gateway has read all of it for a while; the direct
        // provider never did, so a library configured to talk to TMDb itself had
        // no status, no network and no studio at all — and the filters over
        // those columns returned nothing, which looks exactly like a fair
        // answer.
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("original_language")] string? OriginalLanguage,
        [property: JsonPropertyName("tagline")] string? Tagline,
        [property: JsonPropertyName("homepage")] string? Homepage,
        [property: JsonPropertyName("networks")] IReadOnlyList<TmdbNamed>? Networks,
        [property: JsonPropertyName("production_companies")] IReadOnlyList<TmdbNamed>? ProductionCompanies,
        [property: JsonPropertyName("belongs_to_collection")] TmdbNamed? BelongsToCollection,
        [property: JsonPropertyName("keywords")] TmdbKeywords? Keywords,
        // Only one of these is ever populated per media type — release_dates
        // for a movie, content_ratings for a show — the other appended value
        // simply comes back null.
        [property: JsonPropertyName("release_dates")] TmdbReleaseDatesResponse? ReleaseDates = null,
        [property: JsonPropertyName("content_ratings")] TmdbContentRatingsResponse? ContentRatings = null,
        [property: JsonPropertyName("alternative_titles")] TmdbAlternativeTitles? AlternativeTitles = null,
        [property: JsonPropertyName("images")] TmdbImages? Images = null);

    /// <summary>
    /// TMDb answers <c>alternative_titles.titles</c> for a film and
    /// <c>alternative_titles.results</c> for a show — the same shape split
    /// across two names, like <see cref="TmdbKeywords"/>.
    /// </summary>
    private sealed record TmdbAlternativeTitles(
        [property: JsonPropertyName("titles")] IReadOnlyList<TmdbAlternativeTitle>? Movie,
        [property: JsonPropertyName("results")] IReadOnlyList<TmdbAlternativeTitle>? Series)
    {
        /// <summary>
        /// Every distinct alternate spelling TMDb reported for this title,
        /// beyond the primary and original titles a release could already be
        /// matched against — a title can carry the same alternate spelling
        /// for several countries, and a release under that spelling is one
        /// release, not several.
        /// </summary>
        public IReadOnlyList<string> Names(string? primaryTitle, string? originalTitle)
        {
            var exclude = new[] { primaryTitle, originalTitle }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. (Movie ?? Series ?? [])
                .Select(entry => entry.Title?.Trim())
                .Where(title => !string.IsNullOrWhiteSpace(title) && !exclude.Contains(title!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(25)
                .Select(title => title!)];
        }
    }

    private sealed record TmdbAlternativeTitle(
        [property: JsonPropertyName("iso_3166_1")] string? Country,
        [property: JsonPropertyName("title")] string? Title);

    /// <summary>Everything the <c>images</c> append gives beyond the primary poster/backdrop (#864).</summary>
    private sealed record TmdbImages(
        [property: JsonPropertyName("backdrops")] IReadOnlyList<TmdbImage>? Backdrops,
        [property: JsonPropertyName("logos")] IReadOnlyList<TmdbImage>? Logos);

    private sealed record TmdbImage(
        [property: JsonPropertyName("file_path")] string? FilePath);

    /// <summary>
    /// TMDb answers <c>keywords.keywords</c> for a film and
    /// <c>keywords.results</c> for a show — the same data under two names.
    /// Reading only one silently returns an empty list for half a library.
    /// </summary>
    private sealed record TmdbKeywords(
        [property: JsonPropertyName("keywords")] IReadOnlyList<TmdbNamed>? Movie,
        [property: JsonPropertyName("results")] IReadOnlyList<TmdbNamed>? Series)
    {
        public IReadOnlyList<string> Names =>
            [.. (Movie ?? Series ?? [])
                .Select(keyword => keyword.Name?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(25)
                .Select(name => name!)];
    }

    /// <summary>Anything TMDb returns as an object with a name.</summary>
    private sealed record TmdbNamed(
        [property: JsonPropertyName("id")] int? Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record TmdbCredits(
        [property: JsonPropertyName("cast")] IReadOnlyList<TmdbCastMember>? Cast,
        [property: JsonPropertyName("crew")] IReadOnlyList<TmdbCrewMember>? Crew);

    private sealed record TmdbCastMember(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("character")] string? Character,
        [property: JsonPropertyName("profile_path")] string? ProfilePath);

    private sealed record TmdbCrewMember(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("job")] string? Job,
        [property: JsonPropertyName("profile_path")] string? ProfilePath);

    private sealed record TmdbGenre(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record OmdbTitleResponse(
        [property: JsonPropertyName("imdbRating")] string? ImdbRating,
        [property: JsonPropertyName("imdbVotes")] string? ImdbVotes,
        [property: JsonPropertyName("Metascore")] string? Metascore,
        [property: JsonPropertyName("Ratings")] IReadOnlyList<OmdbRating>? Ratings,
        [property: JsonPropertyName("Response")] string? Response);

    private sealed record OmdbSearchResponse(
        [property: JsonPropertyName("Search")] IReadOnlyList<OmdbSearchItem>? Search,
        [property: JsonPropertyName("Response")] string? Response);

    private sealed record OmdbSearchItem(
        [property: JsonPropertyName("Title")] string? Title,
        [property: JsonPropertyName("Year")] string? Year,
        [property: JsonPropertyName("imdbID")] string? ImdbId,
        [property: JsonPropertyName("Poster")] string? Poster);

    private sealed record OmdbDetailResponse(
        [property: JsonPropertyName("Plot")] string? Plot,
        [property: JsonPropertyName("Genre")] string? Genre,
        [property: JsonPropertyName("imdbRating")] string? ImdbRating,
        [property: JsonPropertyName("imdbVotes")] string? ImdbVotes,
        [property: JsonPropertyName("Metascore")] string? Metascore,
        [property: JsonPropertyName("Ratings")] IReadOnlyList<OmdbRating>? Ratings);

    private sealed record OmdbRating(
        [property: JsonPropertyName("Source")] string? Source,
        [property: JsonPropertyName("Value")] string? Value);

    public sealed record MetadataArtworkAsset(
        string FilePath,
        string ContentType);

    private static readonly IReadOnlyDictionary<int, string> MovieGenres = new Dictionary<int, string>
    {
        [12] = "Adventure",
        [14] = "Fantasy",
        [16] = "Animation",
        [18] = "Drama",
        [27] = "Horror",
        [28] = "Action",
        [35] = "Comedy",
        [36] = "History",
        [37] = "Western",
        [53] = "Thriller",
        [80] = "Crime",
        [99] = "Documentary",
        [878] = "Science Fiction",
        [9648] = "Mystery",
        [10402] = "Music",
        [10749] = "Romance",
        [10751] = "Family",
        [10752] = "War",
        [10770] = "TV Movie"
    };

    private static readonly IReadOnlyDictionary<int, string> TvGenres = new Dictionary<int, string>
    {
        [16] = "Animation",
        [18] = "Drama",
        [35] = "Comedy",
        [37] = "Western",
        [80] = "Crime",
        [99] = "Documentary",
        [9648] = "Mystery",
        [10751] = "Family",
        [10759] = "Action & Adventure",
        [10762] = "Kids",
        [10763] = "News",
        [10764] = "Reality",
        [10765] = "Sci-Fi & Fantasy",
        [10766] = "Soap",
        [10767] = "Talk",
        [10768] = "War & Politics"
    };
}
