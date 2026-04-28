using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Data;
using Microsoft.Extensions.Configuration;

namespace Deluno.Integrations.Metadata;

public sealed class TmdbMetadataProvider(
    HttpClient httpClient,
    IConfiguration configuration,
    IPlatformSettingsRepository platformRepository,
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IMetadataProvider
{
    private const string ProviderName = "tmdb";
    private const string BrokerProviderName = "deluno";
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);

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
                    ? "Deluno metadata broker is configured. Users do not need local provider API keys for lookup."
                    : "Deluno broker mode is selected, but no broker URL is configured yet.",
                sources),
            "hybrid" => new MetadataProviderStatus(
                BrokerProviderName,
                brokerConfigured || directConfigured,
                brokerConfigured ? "hybrid" : directConfigured ? "direct-fallback" : "unconfigured",
                brokerConfigured
                    ? "Deluno will try the metadata broker first and fall back to local TMDb when a direct key exists."
                    : directConfigured
                        ? "Hybrid mode is selected. Broker is not configured, so Deluno is using local TMDb fallback."
                        : "Hybrid mode needs either a broker URL or a local TMDb fallback key.",
                sources),
            _ => new MetadataProviderStatus(
                ProviderName,
                directConfigured,
                directConfigured ? "direct" : "unconfigured",
                directConfigured
                    ? string.IsNullOrWhiteSpace(config.OmdbApiKey)
                        ? "Direct TMDb metadata search is configured. Add OMDb to enrich IMDb, Rotten Tomatoes, and Metacritic ratings."
                        : "Direct TMDb search and OMDb ratings enrichment are configured."
                    : "Direct metadata mode needs a TMDb API key before provider lookup can run.",
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
                    ? "Direct TMDb lookup is configured. OMDb enrichment is not configured."
                    : "Direct TMDb lookup and OMDb ratings enrichment are configured."
                : "Direct TMDb lookup needs a TMDb API key.",
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
            return cached;
        }

        if (config.ProviderMode is "broker" or "hybrid" && !string.IsNullOrWhiteSpace(config.BrokerUrl))
        {
            var brokerResults = await TryBrokerSearchAsync(config.BrokerUrl, mediaType, request, query, cancellationToken);
            if (brokerResults is { Count: > 0 })
            {
                await WriteSearchCacheAsync(cacheKey, mediaType, query, brokerResults, cancellationToken);
                return brokerResults;
            }
        }

        if (string.IsNullOrWhiteSpace(config.TmdbApiKey) || config.ProviderMode == "broker")
        {
            return [];
        }

        return await SearchDirectAsync(request, config.TmdbApiKey, cacheKey, mediaType, query, cancellationToken);
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
            return cached;
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
                var result = new[] { exact };
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

        try
        {
            var response = await httpClient.GetFromJsonAsync<TmdbSearchResponse>(url, cancellationToken);
            var items = response?.Results?
                .Where(item => !string.IsNullOrWhiteSpace(item.Title ?? item.Name))
                .Take(12)
                .ToArray() ?? [];

            var results = new List<MetadataSearchResult>(items.Length);
            foreach (var item in items)
            {
                results.Add(await ToResultAsync(item, mediaType, apiKey, cancellationToken));
            }

            await WriteSearchCacheAsync(cacheKey, mediaType, query, results, cancellationToken);
            return results;
        }
        catch
        {
            return [];
        }
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

    private static string BuildSearchCacheKey(string source, string mediaType, string query, int? year, string? providerId)
        => $"{source}:search:{mediaType}:{query.Trim().ToLowerInvariant()}:{year?.ToString(CultureInfo.InvariantCulture) ?? "any"}:{providerId?.Trim() ?? "none"}";

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
        return new MetadataProviderConfiguration(
            settings.MetadataProviderMode,
            ResolveBrokerUrl(settings.MetadataBrokerUrl),
            await GetApiKeyAsync(cancellationToken),
            await GetOmdbApiKeyAsync(cancellationToken));
    }

    private string? ResolveBrokerUrl(string? settingsValue)
    {
        var value = string.IsNullOrWhiteSpace(settingsValue) ? null : settingsValue.Trim();
        value ??= configuration["Deluno:Metadata:BrokerUrl"]
                  ?? configuration["DELUNO_METADATA_BROKER_URL"]
                  ?? Environment.GetEnvironmentVariable("DELUNO_METADATA_BROKER_URL");
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
        => await platformRepository.GetMetadataProviderSecretAsync(ProviderName, cancellationToken)
           ?? configuration["Deluno:Metadata:TMDbApiKey"]
           ?? configuration["TMDB_API_KEY"]
           ?? Environment.GetEnvironmentVariable("TMDB_API_KEY");

    private async Task<string?> GetOmdbApiKeyAsync(CancellationToken cancellationToken)
        => await platformRepository.GetMetadataProviderSecretAsync("omdb", cancellationToken)
           ?? configuration["Deluno:Metadata:OMDbApiKey"]
           ?? configuration["OMDB_API_KEY"]
           ?? Environment.GetEnvironmentVariable("OMDB_API_KEY");

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

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var brokerResponse = await JsonSerializer.DeserializeAsync<MetadataBrokerSearchResponse>(
                stream,
                CacheJsonOptions,
                cancellationToken);
            return brokerResponse?.Results?.Take(12).ToArray();
        }
        catch
        {
            return null;
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
            : $"https://image.tmdb.org/t/p/w500{item.PosterPath}";
        var backdrop = string.IsNullOrWhiteSpace(item.BackdropPath)
            ? null
            : $"https://image.tmdb.org/t/p/w1280{item.BackdropPath}";

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
            ExternalUrl: BuildTmdbUrl(mediaType, item.Id));
    }

    private async Task<MetadataSearchResult?> GetDetailsByIdAsync(
        int id,
        string mediaType,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var path = mediaType == "tv" ? "tv" : "movie";
        var url = $"https://api.themoviedb.org/3/{path}/{id}?api_key={Uri.EscapeDataString(apiKey)}&append_to_response=external_ids";
        try
        {
            var detail = await httpClient.GetFromJsonAsync<TmdbDetailItem>(url, cancellationToken);
            if (detail is null || string.IsNullOrWhiteSpace(detail.Title ?? detail.Name))
            {
                return null;
            }

            var title = detail.Title ?? detail.Name ?? "Unknown title";
            var releaseDate = mediaType == "tv" ? detail.FirstAirDate : detail.ReleaseDate;
            var poster = string.IsNullOrWhiteSpace(detail.PosterPath)
                ? null
                : $"https://image.tmdb.org/t/p/w500{detail.PosterPath}";
            var backdrop = string.IsNullOrWhiteSpace(detail.BackdropPath)
                ? null
                : $"https://image.tmdb.org/t/p/w1280{detail.BackdropPath}";

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
                ExternalUrl: BuildTmdbUrl(mediaType, detail.Id));
        }
        catch
        {
            return null;
        }
    }

    private async Task<TmdbExternalIds> GetExternalIdsAsync(
        int id,
        string mediaType,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var path = mediaType == "tv" ? "tv" : "movie";
        var url = $"https://api.themoviedb.org/3/{path}/{id}/external_ids?api_key={Uri.EscapeDataString(apiKey)}";
        try
        {
            return await httpClient.GetFromJsonAsync<TmdbExternalIds>(url, cancellationToken) ?? new TmdbExternalIds(null);
        }
        catch
        {
            return new TmdbExternalIds(null);
        }
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

        try
        {
            var item = await httpClient.GetFromJsonAsync<OmdbTitleResponse>(url, cancellationToken);
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
        }
        catch
        {
            return ratings;
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
        [property: JsonPropertyName("genres")] IReadOnlyList<TmdbGenre>? Genres,
        [property: JsonPropertyName("external_ids")] TmdbExternalIds? ExternalIds);

    private sealed record TmdbGenre(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record OmdbTitleResponse(
        [property: JsonPropertyName("imdbRating")] string? ImdbRating,
        [property: JsonPropertyName("imdbVotes")] string? ImdbVotes,
        [property: JsonPropertyName("Metascore")] string? Metascore,
        [property: JsonPropertyName("Ratings")] IReadOnlyList<OmdbRating>? Ratings,
        [property: JsonPropertyName("Response")] string? Response);

    private sealed record OmdbRating(
        [property: JsonPropertyName("Source")] string? Source,
        [property: JsonPropertyName("Value")] string? Value);

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
