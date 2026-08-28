using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Integrations.Metadata;

public sealed class TmdbMetadataProvider(
    HttpClient httpClient,
    IConfiguration configuration,
    IPlatformSettingsRepository platformRepository,
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IOptions<StoragePathOptions> storageOptions,
    TimeProvider timeProvider,
    IIntegrationResiliencePolicy resiliencePolicy,
    IOutboundRequestThrottle outboundRequestThrottle,
    ILogger<TmdbMetadataProvider> logger)
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

    public async Task<MetadataProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var config = await GetMetadataConfigurationAsync(cancellationToken);
        var directConfigured = !string.IsNullOrWhiteSpace(config.TmdbApiKey);
        var brokerConfigured = !string.IsNullOrWhiteSpace(config.BrokerUrl);
        var sources = BuildSourceStatuses(config, directConfigured, brokerConfigured);

        return config.ProviderMode switch
        {
            "broker" => new MetadataProviderStatus(
                BrokerProviderName,
                brokerConfigured,
                brokerConfigured ? "broker" : "unconfigured",
                brokerConfigured
                    ? "Deluno's managed metadata service is ready for title matching."
                    : "Title matching has not been configured for this Deluno installation yet.",
                sources),
            "hybrid" => new MetadataProviderStatus(
                BrokerProviderName,
                brokerConfigured || directConfigured,
                brokerConfigured ? "hybrid" : directConfigured ? "direct-fallback" : "unconfigured",
                brokerConfigured
                    ? "Deluno's managed metadata service is ready for title matching."
                    : directConfigured
                        ? "Deluno's metadata service is ready using this installation's configured fallback."
                        : "Title matching has not been configured for this Deluno installation yet.",
                sources),
            _ => new MetadataProviderStatus(
                ProviderName,
                directConfigured,
                directConfigured ? "direct" : "unconfigured",
                directConfigured
                    ? string.IsNullOrWhiteSpace(config.OmdbApiKey)
                        ? "Deluno's title-matching service is ready."
                        : "Deluno's title-matching and ratings service is ready."
                    : "Title matching has not been configured for this Deluno installation yet.",
                sources)
        };
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
                false));
    }

    public async Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
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
        var cacheKey = BuildSearchCacheKey(config.ProviderMode, mediaType, query, request.Year, request.ProviderId);
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

        var live = await SearchDirectAsync(request, config.TmdbApiKey, cacheKey, mediaType, query, cancellationToken);
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

    public async Task<IReadOnlyList<MetadataSearchResult>> SearchDirectAsync(
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

        var mediaType = NormalizeMediaType(request.MediaType);
        var cacheKey = BuildSearchCacheKey($"{ProviderName}:direct", mediaType, query, request.Year, request.ProviderId);
        var cached = await TryReadSearchCacheAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return await LocalizeArtworkAsync(cached, cancellationToken);
        }

        return await SearchDirectAsync(request, apiKey, cacheKey, mediaType, query, cancellationToken);
    }

    private async Task<IReadOnlyList<MetadataSearchResult>> SearchDirectAsync(
        MetadataLookupRequest request,
        string apiKey,
        string cacheKey,
        string mediaType,
        string query,
        CancellationToken cancellationToken)
    {

        if (!string.IsNullOrWhiteSpace(request.ProviderId) &&
            int.TryParse(request.ProviderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var providerId))
        {
            var exact = await GetDetailsByIdAsync(providerId, mediaType, apiKey, cancellationToken);
            if (exact is not null)
            {
                var result = await LocalizeArtworkAsync(new[] { exact }, cancellationToken);
                await WriteSearchCacheAsync(cacheKey, mediaType, query, result, cancellationToken);
                return result;
            }
        }

        var endpoint = mediaType == "tv" ? "search/tv" : "search/movie";
        var url =
            $"https://api.themoviedb.org/3/{endpoint}?api_key={Uri.EscapeDataString(apiKey)}&query={Uri.EscapeDataString(query)}&include_adult=false";
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

        var localized = new List<MetadataSearchResult>(results.Count);
        foreach (var result in results)
        {
            var poster = await CacheArtworkUrlAsync(result.MediaType, result.PosterUrl, cancellationToken);
            var backdrop = await CacheArtworkUrlAsync(result.MediaType, result.BackdropUrl, cancellationToken);
            localized.Add(result with
            {
                PosterUrl = poster ?? result.PosterUrl,
                BackdropUrl = backdrop ?? result.BackdropUrl
            });
        }

        return localized;
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
        var cachedPath = await ReadArtworkLocalPathAsync(cacheKey, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
        {
            return $"/api/metadata/artwork/{cacheKey}";
        }

        var extension = ResolveArtworkExtension(remoteUri);
        Directory.CreateDirectory(_artworkRootPath);
        var destinationPath = Path.Combine(_artworkRootPath, $"{cacheKey}{extension}");

        // Artwork comes from the same provider and counts against the same
        // budget. A backfill fetching a poster per title is the larger half of
        // the traffic, not an afterthought.
        if (await outboundRequestThrottle.TryAcquireAsync(
                remoteUri.Host,
                OutboundRate.MetadataProviderDefault,
                MaxMetadataThrottleWait,
                cancellationToken) is null)
        {
            return remoteUrl;
        }

        try
        {
            using var response = await httpClient.GetAsync(remoteUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                await UpsertArtworkCacheAsync(cacheKey, mediaType, remoteUri.ToString(), null, cancellationToken);
                return remoteUrl;
            }

            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await stream.CopyToAsync(output, cancellationToken);
            }

            await UpsertArtworkCacheAsync(cacheKey, mediaType, remoteUri.ToString(), destinationPath, cancellationToken);
            return $"/api/metadata/artwork/{cacheKey}";
        }
        catch
        {
            await UpsertArtworkCacheAsync(cacheKey, mediaType, remoteUri.ToString(), null, cancellationToken);
            return remoteUrl;
        }
    }

    public async Task<MetadataArtworkAsset?> GetCachedArtworkAsync(string cacheKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return null;
        }

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
        AddParameter(command, "@cacheKey", cacheKey.Trim());

        var localPath = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            return null;
        }

        return new MetadataArtworkAsset(
            localPath,
            ResolveContentType(localPath));
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

    public async Task InvalidateCacheAsync(string? mediaType, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Cache,
            cancellationToken);

        using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            command.CommandText = "DELETE FROM search_result_cache;";
        }
        else
        {
            command.CommandText = "DELETE FROM search_result_cache WHERE media_type = @mediaType;";
            AddParameter(command, "@mediaType", NormalizeMediaType(mediaType));
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
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
        => BuildSearchCacheKey(ProviderName, mediaType, query, year, providerId);

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
    private const string SearchCacheShape = "v5";

    private static string BuildSearchCacheKey(string source, string mediaType, string query, int? year, string? providerId)
        => $"{source}:search:{SearchCacheShape}:{mediaType}:{query.Trim().ToLowerInvariant()}:{year?.ToString(CultureInfo.InvariantCulture) ?? "any"}:{providerId?.Trim() ?? "none"}";

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
            requiresDirectFallback ? await GetOmdbApiKeyAsync(cancellationToken) : null);
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
                "Future TV-specific enrichment",
                false,
                "planned",
                "Reserved for future TV metadata fallback and episode-specific enrichment."),
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
            VoteCount: item.VoteCount);
    }

    private async Task<MetadataSearchResult?> GetDetailsByIdAsync(
        int id,
        string mediaType,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var path = mediaType == "tv" ? "tv" : "movie";
        var url = $"https://api.themoviedb.org/3/{path}/{id}?api_key={Uri.EscapeDataString(apiKey)}&append_to_response=external_ids,credits,keywords";
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
            Cast: detail.Credits?.Cast?
                .Where(member => !string.IsNullOrWhiteSpace(member.Name))
                .Take(10)
                .Select(member => new MetadataCastMember(
                    member.Name!,
                    member.Character,
                    string.IsNullOrWhiteSpace(member.ProfilePath) ? null : $"https://image.tmdb.org/t/p/{ArtworkSizes.Portrait}{member.ProfilePath}"))
                .ToArray() ?? [],
            RuntimeMinutes: detail.Runtime ?? detail.EpisodeRunTime?.FirstOrDefault(minutes => minutes > 0),
            Popularity: detail.Popularity,
            VoteCount: detail.VoteCount,
            // A show has a network and a film has a studio; both are "who made
            // it", and TMDb answers them in different fields.
            Studio: detail.ProductionCompanies?.Select(company => company?.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
            Network: detail.Networks?.Select(network => network?.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
            Collection: string.IsNullOrWhiteSpace(detail.BelongsToCollection?.Name) ? null : detail.BelongsToCollection.Name,
            Tagline: string.IsNullOrWhiteSpace(detail.Tagline) ? null : detail.Tagline,
            Homepage: string.IsNullOrWhiteSpace(detail.Homepage) ? null : detail.Homepage,
            OriginalLanguage: string.IsNullOrWhiteSpace(detail.OriginalLanguage) ? null : detail.OriginalLanguage,
            Status: string.IsNullOrWhiteSpace(detail.Status) ? null : detail.Status,
            Keywords: detail.Keywords?.Names ?? []);
    }

    /// <summary>
    /// The season/episode catalogue for a series.
    ///
    /// The gateway is asked first — it does the per-season fan-out and caches the
    /// answer, so the app makes one request instead of one per season. TMDb is the
    /// backup when the gateway cannot answer: a catalogue is the difference
    /// between a library that mirrors your disk and one that knows what episodes
    /// exist, so it is worth a direct call rather than nothing.
    /// </summary>
    public async Task<IReadOnlyList<MetadataSeason>> GetSeriesCatalogueAsync(
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
                    ParseAirDate(episode.AirDate)))
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

    private static DateTimeOffset? ParseAirDate(string? value)
        => ParseDateOnly(value) is { } date
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;

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

    private sealed record BrokerReleaseDatesResponse(
        [property: JsonPropertyName("inCinemas")] string? InCinemas,
        [property: JsonPropertyName("digital")] string? Digital,
        [property: JsonPropertyName("physical")] string? Physical);

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

    private sealed record TmdbReleaseDatesResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<TmdbCountryReleaseDates>? Results);

    private sealed record TmdbCountryReleaseDates(
        [property: JsonPropertyName("iso_3166_1")] string? Country,
        [property: JsonPropertyName("release_dates")] IReadOnlyList<TmdbReleaseDateEntry>? ReleaseDates);

    private sealed record TmdbReleaseDateEntry(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("release_date")] string? ReleaseDate);

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

    private async Task<T?> GetJsonWithResilienceAsync<T>(
        string url,
        string key,
        string operation,
        CancellationToken cancellationToken)
    {
        // Paced before the request. A freshly imported library asks this
        // roughly once per title; at 20,000 titles and no pacing that is a
        // sustained burst, which is how a run during this work collected 394
        // rate-limit responses out of about 20,000 requests.
        if (await outboundRequestThrottle.TryAcquireAsync(
                ThrottleHost(url),
                OutboundRate.MetadataProviderDefault,
                MaxMetadataThrottleWait,
                cancellationToken) is null)
        {
            logger.LogInformation(
                "Deferred {Operation}: the metadata provider is still inside its request budget after {Wait}.",
                operation,
                MaxMetadataThrottleWait);

            return default;
        }

        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(key, operation, FailureThreshold: 2),
            async token =>
            {
                try
                {
                    using var response = await httpClient.GetAsync(url, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (IntegrationResiliencePolicy.IsTransientHttpStatusCode(response.StatusCode))
                        {
                            throw new HttpRequestException(
                                $"{operation} returned transient HTTP {(int)response.StatusCode}.",
                                null,
                                response.StatusCode);
                        }

                        return default;
                    }

                    return await response.Content.ReadFromJsonAsync<T>(CacheJsonOptions, token);
                }
                catch (Exception exception) when (exception is not HttpRequestException and not TaskCanceledException and not IOException)
                {
                    return default;
                }
            },
            value => value is null
                ? IntegrationResilienceOutcome.NonRetryableFailure
                : IntegrationResilienceOutcome.Success,
            cancellationToken);

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

    private sealed record MetadataProviderConfiguration(
        string ProviderMode,
        string? BrokerUrl,
        string? TmdbApiKey,
        string? OmdbApiKey);

    private sealed record MetadataBrokerSearchResponse(
        string Provider,
        string Mode,
        int ResultCount,
        IReadOnlyList<MetadataSearchResult> Results);

    private sealed record TmdbSearchResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<TmdbSearchItem>? Results);

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
        [property: JsonPropertyName("imdb_id")] string? ImdbId);

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
        [property: JsonPropertyName("keywords")] TmdbKeywords? Keywords);

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
        [property: JsonPropertyName("name")] string? Name);

    private sealed record TmdbCredits(
        [property: JsonPropertyName("cast")] IReadOnlyList<TmdbCastMember>? Cast);

    private sealed record TmdbCastMember(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("character")] string? Character,
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
