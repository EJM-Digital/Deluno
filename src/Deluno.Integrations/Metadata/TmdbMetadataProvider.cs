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
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MetadataProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var apiKey = await GetApiKeyAsync(cancellationToken);
        var status = string.IsNullOrWhiteSpace(apiKey)
            ? new MetadataProviderStatus(
                ProviderName,
                false,
                "unconfigured",
                "TMDb API key is not configured. Metadata search will return no provider results until TMDb is configured.")
            : new MetadataProviderStatus(
                ProviderName,
                true,
                "live",
                "TMDb metadata search is configured.");

        return status;
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

        var apiKey = await GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        var mediaType = NormalizeMediaType(request.MediaType);
        var cacheKey = BuildSearchCacheKey(mediaType, query, request.Year, request.ProviderId);
        var cached = await TryReadSearchCacheAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

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
        => $"{ProviderName}:search:{mediaType}:{query.Trim().ToLowerInvariant()}:{year?.ToString(CultureInfo.InvariantCulture) ?? "any"}:{providerId?.Trim() ?? "none"}";

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken)
        => await platformRepository.GetMetadataProviderSecretAsync(ProviderName, cancellationToken)
           ?? configuration["Deluno:Metadata:TMDbApiKey"]
           ?? configuration["TMDB_API_KEY"]
           ?? Environment.GetEnvironmentVariable("TMDB_API_KEY");

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
            Genres: ResolveGenreNames(mediaType, item.GenreIds),
            ImdbId: externalIds.ImdbId,
                    ExternalUrl: $"https://www.themoviedb.org/{(mediaType == "tv" ? "tv" : "movie")}/{item.Id}");
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
                Genres: detail.Genres?.Select(genre => genre.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToArray() ?? [],
                ImdbId: detail.ExternalIds?.ImdbId,
                ExternalUrl: $"https://www.themoviedb.org/{(mediaType == "tv" ? "tv" : "movie")}/{detail.Id}");
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

    private static int? TryParseYear(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.Year
            : null;

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
        [property: JsonPropertyName("genres")] IReadOnlyList<TmdbGenre>? Genres,
        [property: JsonPropertyName("external_ids")] TmdbExternalIds? ExternalIds);

    private sealed record TmdbGenre(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name);

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
