using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Deluno.Integrations.Metadata;

/// <summary>
/// TheTVDB v4 called straight from this install with a key the owner supplied
/// (direct mode), for people not using Deluno's managed broker. It maps
/// TheTVDB's answers into the same records the gateway serves, so nothing
/// above it knows which route an answer took. The gateway's own mapping lives
/// in services/metadata-gateway/src/tvdb.js; the two are kept in step by the
/// contract in docs/metadata-broker-contract.md.
/// </summary>
internal sealed partial class TvdbDirectClient(HttpClient httpClient, string apiKey, string? pin)
{
    private const string Api = "https://api4.thetvdb.com/v4";
    private const int MaxPages = 20;

    // The bearer token lasts a month and is not per-user, so one is shared by
    // every client in the process rather than logging in for each request.
    private static readonly SemaphoreSlim LoginGate = new(1, 1);
    private static (string Key, string Token, DateTimeOffset Expires)? _token;

    public async Task<IReadOnlyList<TvdbSeriesRecord>> SearchAsync(string query, int? year, CancellationToken cancellationToken)
    {
        var url = $"{Api}/search?type=series&limit=8&query={Uri.EscapeDataString(query)}";
        if (year is > 0)
        {
            url += $"&year={year.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        var payload = await GetAsync(url, cancellationToken);
        return payload is null
            ? []
            : ReadArray(payload.Value, "data").Select(item => MapSeries(item, null)).OfType<TvdbSeriesRecord>().Take(8).ToArray();
    }

    public async Task<TvdbSeriesRecord?> GetSeriesAsync(string tvdbId, CancellationToken cancellationToken)
    {
        var payload = await GetAsync($"{Api}/series/{Uri.EscapeDataString(tvdbId)}/extended?short=false", cancellationToken);
        if (payload is null || !payload.Value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        JsonElement? translation = null;
        var translated = await GetAsync($"{Api}/series/{Uri.EscapeDataString(tvdbId)}/translations/eng", cancellationToken);
        if (translated is not null && translated.Value.TryGetProperty("data", out var translationData) &&
            translationData.ValueKind == JsonValueKind.Object)
        {
            translation = translationData;
        }

        return MapSeries(data, translation);
    }

    /// <summary>One language's name and overview; <paramref name="language"/> is TheTVDB's three-letter code. Null when TheTVDB has none.</summary>
    public async Task<TvdbTranslation?> GetTranslationAsync(string tvdbId, string language, CancellationToken cancellationToken)
    {
        var payload = await GetAsync($"{Api}/series/{Uri.EscapeDataString(tvdbId)}/translations/{Uri.EscapeDataString(language)}", cancellationToken);
        if (payload is null || !payload.Value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var title = Text(data, "name");
        var overview = Text(data, "overview");
        return title is null && overview is null ? null : new TvdbTranslation(title, overview);
    }

    public async Task<string?> FindByImdbIdAsync(string imdbId, CancellationToken cancellationToken)
    {
        var payload = await GetAsync($"{Api}/search/remoteid/{Uri.EscapeDataString(imdbId)}", cancellationToken);
        if (payload is null)
        {
            return null;
        }

        foreach (var entry in ReadArray(payload.Value, "data"))
        {
            if (entry.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Object &&
                series.TryGetProperty("id", out var id))
            {
                return id.ToString();
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<TvdbSeason>> GetCatalogueAsync(string tvdbId, CancellationToken cancellationToken)
    {
        var episodes = await FetchEpisodesAsync(tvdbId, "official", "eng", cancellationToken);
        if (episodes is null)
        {
            episodes = await FetchEpisodesAsync(tvdbId, "official", null, cancellationToken);
        }

        if (episodes is null)
        {
            return [];
        }

        return episodes
            .Where(IsEpisode)
            .GroupBy(episode => episode.GetProperty("seasonNumber").GetInt32())
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var list = group
                    .Select(episode => new TvdbEpisode(
                        episode.GetProperty("number").GetInt32(),
                        Text(episode, "name"),
                        Text(episode, "overview"),
                        DateText(episode, "aired"),
                        PositiveInt(episode, "absoluteNumber")))
                    .OrderBy(episode => episode.EpisodeNumber)
                    .ToArray();
                return new TvdbSeason(
                    group.Key,
                    null,
                    list.Select(episode => episode.AirDate).Where(date => date is not null).Order().FirstOrDefault(),
                    list);
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<MetadataEpisodeGroup>> GetEpisodeGroupsAsync(string tvdbId, CancellationToken cancellationToken)
    {
        var aired = (await FetchEpisodesAsync(tvdbId, "official", null, cancellationToken))?.Where(IsEpisode).ToArray();
        if (aired is null || aired.Length == 0)
        {
            return [];
        }

        var airedById = aired
            .Where(episode => episode.TryGetProperty("id", out _))
            .GroupBy(episode => episode.GetProperty("id").GetInt64())
            .ToDictionary(group => group.Key, group => group.First());

        var groups = new List<MetadataEpisodeGroup>();
        foreach (var (seasonType, code, name) in new[]
                 {
                     ("dvd", TmdbEpisodeGroupTypes.Dvd, "DVD order"),
                     ("absolute", TmdbEpisodeGroupTypes.Absolute, "Absolute order")
                 })
        {
            var episodes = (await FetchEpisodesAsync(tvdbId, seasonType, null, cancellationToken))?.Where(IsEpisode).ToArray();
            if (episodes is null)
            {
                continue;
            }

            var mapped = new List<MetadataEpisodeGroupEpisode>();
            foreach (var episode in episodes)
            {
                if (episode.TryGetProperty("id", out var id) && airedById.TryGetValue(id.GetInt64(), out var original))
                {
                    mapped.Add(new MetadataEpisodeGroupEpisode(
                        original.GetProperty("seasonNumber").GetInt32(),
                        original.GetProperty("number").GetInt32(),
                        episode.GetProperty("seasonNumber").GetInt32(),
                        episode.GetProperty("number").GetInt32()));
                }
            }

            if (mapped.Count > 0)
            {
                groups.Add(new MetadataEpisodeGroup(code, $"tvdb:{tvdbId}:{seasonType}", name, mapped));
            }
        }

        return groups;
    }

    /// <summary>Every episode of one season type, following TheTVDB's pagination. Null when TheTVDB has none (404).</summary>
    private async Task<List<JsonElement>?> FetchEpisodesAsync(
        string tvdbId,
        string seasonType,
        string? language,
        CancellationToken cancellationToken)
    {
        var episodes = new List<JsonElement>();
        for (var page = 0; page < MaxPages; page++)
        {
            var path = language is null
                ? $"{Api}/series/{Uri.EscapeDataString(tvdbId)}/episodes/{seasonType}"
                : $"{Api}/series/{Uri.EscapeDataString(tvdbId)}/episodes/{seasonType}/{language}";
            var payload = await GetAsync($"{path}?page={page}", cancellationToken);
            if (payload is null)
            {
                return page == 0 ? null : episodes;
            }

            if (payload.Value.TryGetProperty("data", out var data) &&
                data.TryGetProperty("episodes", out var batch) &&
                batch.ValueKind == JsonValueKind.Array)
            {
                episodes.AddRange(batch.EnumerateArray().Select(item => item.Clone()));
            }

            if (!payload.Value.TryGetProperty("links", out var links) ||
                !links.TryGetProperty("next", out var next) ||
                next.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                break;
            }
        }

        return episodes;
    }

    private async Task<JsonElement?> GetAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetTokenAsync(attempt > 0, cancellationToken);
            if (token is null)
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                continue; // expired or revoked: log in again once
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).Clone();
        }

        return null;
    }

    private async Task<string?> GetTokenAsync(bool force, CancellationToken cancellationToken)
    {
        var cacheKey = $"{apiKey}|{pin}";
        await LoginGate.WaitAsync(cancellationToken);
        try
        {
            if (!force && _token is { } cached && cached.Key == cacheKey && cached.Expires > DateTimeOffset.UtcNow)
            {
                return cached.Token;
            }

            // A user-supported key needs the subscriber's PIN; a licensed key
            // must leave "pin" out entirely.
            var body = string.IsNullOrWhiteSpace(pin)
                ? (object)new { apikey = apiKey }
                : new { apikey = apiKey, pin };
            using var response = await httpClient.PostAsJsonAsync($"{Api}/login", body, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var token = payload.TryGetProperty("data", out var data) && data.TryGetProperty("token", out var value)
                ? value.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            // Valid for a month; renew a few days early.
            _token = (cacheKey, token, DateTimeOffset.UtcNow.AddDays(25));
            return token;
        }
        finally
        {
            LoginGate.Release();
        }
    }

    private static bool IsEpisode(JsonElement episode)
        => episode.TryGetProperty("number", out var number) && number.ValueKind == JsonValueKind.Number && number.GetInt32() > 0
           && episode.TryGetProperty("seasonNumber", out var season) && season.ValueKind == JsonValueKind.Number && season.GetInt32() >= 0;

    private static IEnumerable<JsonElement> ReadArray(JsonElement element, string name)
        => element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static string? DateText(JsonElement element, string name)
    {
        var value = Text(element, name);
        return value is { Length: >= 10 } && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? value[..10]
            : null;
    }

    private static int? PositiveInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0
            ? number
            : null;

    private static string? ImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"https://artworks.thetvdb.com{(value.StartsWith('/') ? "" : "/")}{value}";
    }

    [GeneratedRegex(@"^(?:tv/)?(\d{1,12})$", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbSeriesIdPattern();

    /// <summary>A search hit or an extended series record, mapped the way the gateway maps it.</summary>
    internal static TvdbSeriesRecord? MapSeries(JsonElement record, JsonElement? translation)
    {
        var tvdbId = Text(record, "tvdb_id") ?? (record.TryGetProperty("id", out var idElement) ? idElement.ToString() : null);
        if (string.IsNullOrWhiteSpace(tvdbId))
        {
            return null;
        }

        string? imdb = null;
        string? tmdb = null;
        var remoteList = record.TryGetProperty("remote_ids", out var snake) ? snake
            : record.TryGetProperty("remoteIds", out var camel) ? camel
            : default;
        if (remoteList.ValueKind == JsonValueKind.Array)
        {
            foreach (var remote in remoteList.EnumerateArray())
            {
                var id = Text(remote, "id");
                var source = (Text(remote, "sourceName") ?? string.Empty).ToLowerInvariant();
                if (id is null)
                {
                    continue;
                }

                if (source.Contains("imdb") && Regex.IsMatch(id, @"^tt\d+$", RegexOptions.IgnoreCase))
                {
                    imdb ??= id;
                }
                else if (source.Contains("themoviedb") && TmdbSeriesIdPattern().Match(id) is { Success: true } match)
                {
                    tmdb ??= match.Groups[1].Value;
                }
            }
        }

        var baseName = Text(record, "name") ?? Text(record, "title");
        var title = (translation is { } t ? Text(t, "name") : null) ?? Text(record, "name_translated") ?? baseName;
        if (title is null)
        {
            return null;
        }

        var yearText = Text(record, "year") ?? Text(record, "firstAired") ?? Text(record, "first_air_time");
        int? year = yearText is { Length: >= 4 } && int.TryParse(yearText[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear)
            ? parsedYear
            : null;

        var aliases = new List<string>();
        if (record.TryGetProperty("aliases", out var aliasList) && aliasList.ValueKind == JsonValueKind.Array)
        {
            aliases.AddRange(aliasList.EnumerateArray()
                .Select(alias => alias.ValueKind == JsonValueKind.String ? alias.GetString() : Text(alias, "name"))
                .Where(alias => !string.IsNullOrWhiteSpace(alias))!);
        }

        if (translation is { } withAliases && withAliases.TryGetProperty("aliases", out var translatedAliases) &&
            translatedAliases.ValueKind == JsonValueKind.Array)
        {
            aliases.AddRange(translatedAliases.EnumerateArray()
                .Select(alias => alias.ValueKind == JsonValueKind.String ? alias.GetString() : null)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))!);
        }

        var genres = new List<string>();
        if (record.TryGetProperty("genres", out var genreList) && genreList.ValueKind == JsonValueKind.Array)
        {
            genres.AddRange(genreList.EnumerateArray()
                .Select(genre => genre.ValueKind == JsonValueKind.String ? genre.GetString() : Text(genre, "name"))
                .Where(genre => !string.IsNullOrWhiteSpace(genre))!);
        }

        var artworks = record.TryGetProperty("artworks", out var art) && art.ValueKind == JsonValueKind.Array
            ? art.EnumerateArray().ToArray()
            : [];
        List<JsonElement> OfType(int type) => artworks
            .Where(item => item.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.Number && kind.GetInt32() == type)
            .Where(item => ImageUrl(Text(item, "image")) is not null)
            .OrderByDescending(item => item.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number ? score.GetDouble() : 0)
            .ToList();
        var backgrounds = OfType(3);

        string? network = Text(record, "network");
        foreach (var name in new[] { "latestNetwork", "originalNetwork" })
        {
            if (network is null && record.TryGetProperty(name, out var company) && company.ValueKind == JsonValueKind.Object)
            {
                network = Text(company, "name");
            }
        }

        string? status = null;
        if (record.TryGetProperty("status", out var statusElement))
        {
            status = statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() : Text(statusElement, "name");
        }

        string? certification = null;
        if (record.TryGetProperty("contentRatings", out var ratings) && ratings.ValueKind == JsonValueKind.Array)
        {
            var list = ratings.EnumerateArray().Where(rating => Text(rating, "name") is not null).ToArray();
            if (list.Length > 0)
            {
                var preferred = list.Where(rating =>
                    string.Equals(Text(rating, "country"), "usa", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Text(rating, "country"), "US", StringComparison.OrdinalIgnoreCase)).ToArray();
                certification = Text(preferred.Length > 0 ? preferred[0] : list[0], "name");
            }
        }

        var poster = ImageUrl(Text(record, "image_url") ?? Text(record, "image") ?? Text(record, "poster")
                              ?? OfType(2).Select(item => Text(item, "image")).FirstOrDefault());
        var overview = (translation is { } o ? Text(o, "overview") : null) ?? Text(record, "overview");

        return new TvdbSeriesRecord(
            tvdbId,
            tmdb,
            imdb,
            title,
            baseName is not null && !string.Equals(baseName, title, StringComparison.Ordinal) ? baseName : null,
            year,
            overview,
            poster,
            ImageUrl(backgrounds.Select(item => Text(item, "image")).FirstOrDefault()),
            ImageUrl(OfType(1).Select(item => Text(item, "image")).FirstOrDefault()),
            ImageUrl(OfType(23).Select(item => Text(item, "image")).FirstOrDefault()),
            backgrounds.Skip(1).Take(12).Select(item => ImageUrl(Text(item, "image"))!).ToArray(),
            genres,
            network,
            status,
            PositiveInt(record, "averageRuntime"),
            Text(record, "originalLanguage") ?? Text(record, "primary_language"),
            certification,
            aliases.Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToArray(),
            $"https://thetvdb.com/dereferrer/series/{tvdbId}");
    }
}
