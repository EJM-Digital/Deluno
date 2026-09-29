using System.Collections.Concurrent;
using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.Metadata;

// TheTVDB as the first source for TV, with TMDb as the fallback field by field.
//
// A show's identity stays exactly what it was: the TMDb id is still the
// provider id, so existing shows keep theirs. TheTVDB adds its own id
// (MetadataSearchResult.TvDbId, filled the next time a show's metadata is
// refreshed) and supplies the fields and the episode numbering. Movies never
// come here.
public sealed partial class TmdbMetadataProvider
{
    private const string TvdbSourceName = "tvdb";
    private static readonly TimeSpan TvdbSearchTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TvdbSeriesTtl = TimeSpan.FromHours(1);

    // Scoped provider, so the memory the lookups share is static. It only ever
    // spares TheTVDB (or the broker) a repeat of an identical question; the
    // gateway's own cache is the long-lived one.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Expires, object Value)> TvdbMemory = new();
    private static readonly ConcurrentDictionary<string, string> TmdbToTvdbIds = new();

    public async Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken)
    {
        var results = await SearchTmdbAsync(request, cancellationToken);
        var isIdentity = !string.IsNullOrWhiteSpace(request.IdSource) || !string.IsNullOrWhiteSpace(request.ProviderId);
        if (NormalizeMediaType(request.MediaType) != "tv")
        {
            return isIdentity ? await ApplyLanguageAsync(results, cancellationToken) : results;
        }

        try
        {
            if (isIdentity)
            {
                // An identity search returns the one title; TheTVDB only adds to it.
                var overlaid = new List<MetadataSearchResult>(results.Count);
                foreach (var result in results)
                {
                    overlaid.Add(await ApplyLanguageAsync(await OverlayTvdbAsync(result, cancellationToken), cancellationToken));
                }

                return overlaid;
            }

            return await MergeTvdbSearchAsync(request, results, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // TheTVDB is the preferred source, not a required one.
            logger.LogInformation(exception, "TheTVDB could not add to a TV search; showing TMDb's results.");
            return results;
        }
    }

    public async Task<MetadataProviderRecordLookup> ResolveProviderRecordAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken)
    {
        var lookup = await ResolveTmdbProviderRecordAsync(request, cancellationToken);
        if (lookup.Status != MetadataProviderRecordStatus.Found || lookup.Result is null)
        {
            return lookup;
        }

        if (NormalizeMediaType(request.MediaType) != "tv")
        {
            return lookup with { Result = await ApplyLanguageAsync(lookup.Result, cancellationToken) };
        }

        try
        {
            var overlaid = await OverlayTvdbAsync(lookup.Result, cancellationToken);
            return lookup with { Result = await ApplyLanguageAsync(overlaid, cancellationToken) };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(exception, "TheTVDB could not add to series {ProviderId}; keeping TMDb's record.", lookup.ProviderId);
            return lookup with { Result = await ApplyLanguageAsync(lookup.Result, cancellationToken) };
        }
    }

    private async Task<IReadOnlyList<MetadataSearchResult>> ApplyLanguageAsync(
        IReadOnlyList<MetadataSearchResult> results,
        CancellationToken cancellationToken)
    {
        var localized = new List<MetadataSearchResult>(results.Count);
        foreach (var result in results)
        {
            localized.Add(await ApplyLanguageAsync(result, cancellationToken));
        }

        return localized;
    }

    public Task<IReadOnlyList<MetadataSeason>> GetSeriesCatalogueAsync(
        string providerId,
        CancellationToken cancellationToken)
        => GetSeriesCatalogueAsync(providerId, MetadataCatalogueSource.Preferred, cancellationToken);

    public async Task<IReadOnlyList<MetadataSeason>> GetSeriesCatalogueAsync(
        string providerId,
        MetadataCatalogueSource source,
        CancellationToken cancellationToken)
    {
        if (source == MetadataCatalogueSource.Preferred &&
            int.TryParse(providerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            try
            {
                var tvdb = await GetTvdbCatalogueAsync(tmdbId, cancellationToken);
                if (tvdb.Count > 0)
                {
                    return tvdb;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(exception, "TheTVDB had no usable episode list for series {ProviderId}; using TMDb's.", providerId);
            }
        }

        return await GetTmdbSeriesCatalogueAsync(providerId, cancellationToken);
    }

    public Task<IReadOnlyList<MetadataEpisodeGroup>> GetSeriesEpisodeGroupsAsync(
        string providerId,
        CancellationToken cancellationToken)
        => GetSeriesEpisodeGroupsAsync(providerId, MetadataCatalogueSource.Preferred, cancellationToken);

    public async Task<IReadOnlyList<MetadataEpisodeGroup>> GetSeriesEpisodeGroupsAsync(
        string providerId,
        MetadataCatalogueSource source,
        CancellationToken cancellationToken)
    {
        if (source == MetadataCatalogueSource.Preferred &&
            int.TryParse(providerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            try
            {
                var tvdbId = await ResolveTvdbIdForTmdbAsync(tmdbId, cancellationToken);
                if (tvdbId is not null)
                {
                    var groups = await TvdbEpisodeGroupsAsync(tvdbId, cancellationToken);
                    if (groups is { Count: > 0 })
                    {
                        return groups;
                    }
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(exception, "TheTVDB had no episode orders for series {ProviderId}; using TMDb's.", providerId);
            }
        }

        return await GetTmdbSeriesEpisodeGroupsAsync(providerId, cancellationToken);
    }

    /// <summary>
    /// TheTVDB's hits first, each tied to a TMDb identity through TheTVDB's own
    /// remote ids so the title Deluno stores keeps the id every existing show
    /// already has. Results only TMDb found follow, untouched.
    /// </summary>
    private async Task<IReadOnlyList<MetadataSearchResult>> MergeTvdbSearchAsync(
        MetadataLookupRequest request,
        IReadOnlyList<MetadataSearchResult> tmdbResults,
        CancellationToken cancellationToken)
    {
        var query = request.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return tmdbResults;
        }

        var year = request.Year;
        if (year is null && MetadataQueryYear.TrySplit(query, timeProvider.GetUtcNow().Year, out var title, out var typedYear))
        {
            var byYear = await TvdbSearchAsync(title, typedYear, cancellationToken);
            if (byYear is { Count: > 0 })
            {
                query = title;
                year = typedYear;
            }
        }

        var hits = await TvdbSearchAsync(query, year, cancellationToken);
        if (hits is not { Count: > 0 })
        {
            return tmdbResults;
        }

        var byTmdbId = tmdbResults
            .GroupBy(result => result.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var merged = new List<MetadataSearchResult>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extraTmdbLookups = 0;

        foreach (var hit in hits)
        {
            if (string.IsNullOrWhiteSpace(hit.TmdbId) || !used.Add(hit.TmdbId))
            {
                // A show TheTVDB lists without a TMDb id has no identity Deluno
                // can store; TMDb's own results (below) still cover it if TMDb
                // knows the show.
                continue;
            }

            if (byTmdbId.TryGetValue(hit.TmdbId, out var tmdb))
            {
                merged.Add(ApplyTvdb(tmdb, hit));
            }
            else if (extraTmdbLookups < 4)
            {
                extraTmdbLookups++;
                var lookup = await ResolveTmdbProviderRecordAsync(
                    new MetadataLookupRequest(hit.Title, "tv", hit.Year, hit.TmdbId),
                    cancellationToken);
                if (lookup.Status == MetadataProviderRecordStatus.Found && lookup.Result is not null)
                {
                    merged.Add(ApplyTvdb(lookup.Result, hit));
                }
            }
        }

        merged.AddRange(tmdbResults.Where(result => !used.Contains(result.ProviderId)));
        return await LocalizeArtworkAsync(merged.Take(12).ToArray(), cancellationToken);
    }

    /// <summary>One TMDb result, with TheTVDB's record for the same show laid over it.</summary>
    private async Task<MetadataSearchResult> OverlayTvdbAsync(MetadataSearchResult tmdb, CancellationToken cancellationToken)
    {
        var tvdbId = tmdb.TvDbId;
        if (string.IsNullOrWhiteSpace(tvdbId) && !string.IsNullOrWhiteSpace(tmdb.ImdbId))
        {
            tvdbId = await TvdbFindByImdbAsync(tmdb.ImdbId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(tvdbId))
        {
            return tmdb;
        }

        var record = await TvdbSeriesAsync(tvdbId, cancellationToken);
        if (record is null)
        {
            // Nothing from TheTVDB: keep TMDb's record, but do not lose the id
            // TMDb already told us.
            return tmdb with { TvDbId = tvdbId };
        }

        var merged = ApplyTvdb(tmdb, record);
        return (await LocalizeArtworkAsync([merged], cancellationToken))[0];
    }

    /// <summary>
    /// TheTVDB first, field by field: whatever TheTVDB has wins, and a field it
    /// leaves empty keeps TMDb's value. Ratings, cast, crew and keywords are
    /// TMDb's; TheTVDB has no equivalent that matches how Deluno reads them.
    /// </summary>
    internal static MetadataSearchResult ApplyTvdb(MetadataSearchResult tmdb, TvdbSeriesRecord tvdb)
    {
        var title = Prefer(tvdb.Title, tmdb.Title)!;
        var alternates = (tmdb.AlternateTitles ?? [])
            .Concat([tmdb.Title, tmdb.OriginalTitle])
            .Concat(tvdb.Aliases ?? [])
            .Concat([tvdb.OriginalTitle])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Where(name => !string.Equals(name, title, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return tmdb with
        {
            Title = title,
            OriginalTitle = Prefer(tvdb.OriginalTitle, tmdb.OriginalTitle),
            Year = tvdb.Year ?? tmdb.Year,
            Overview = Prefer(tvdb.Overview, tmdb.Overview),
            PosterUrl = Prefer(tvdb.PosterUrl, tmdb.PosterUrl),
            BackdropUrl = Prefer(tvdb.BackdropUrl, tmdb.BackdropUrl),
            BannerUrl = Prefer(tvdb.BannerUrl, tmdb.BannerUrl),
            LogoUrl = Prefer(tvdb.LogoUrl, tmdb.LogoUrl),
            Screenshots = tvdb.Screenshots is { Count: > 0 } ? tvdb.Screenshots : tmdb.Screenshots,
            Genres = tvdb.Genres is { Count: > 0 } ? tvdb.Genres : tmdb.Genres,
            Network = Prefer(tvdb.Network, tmdb.Network),
            Status = NormalizeTvdbStatus(tvdb.Status) ?? tmdb.Status,
            RuntimeMinutes = tvdb.RuntimeMinutes ?? tmdb.RuntimeMinutes,
            // The ratings board is the owner's chosen country and comes from
            // TMDb; TheTVDB's single rating only fills a gap.
            Certification = Prefer(tmdb.Certification, tvdb.Certification),
            ImdbId = Prefer(tmdb.ImdbId, tvdb.ImdbId),
            OriginalLanguage = tmdb.OriginalLanguage,
            TvDbId = tvdb.TvdbId,
            DataSource = "tvdb",
            AlternateTitles = alternates.Length > 0 ? alternates : tmdb.AlternateTitles
        };
    }

    /// <summary>TheTVDB says Continuing/Ended/Upcoming; Deluno's status vocabulary is TMDb's.</summary>
    internal static string? NormalizeTvdbStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "continuing" => "Returning Series",
            "ended" => "Ended",
            "upcoming" => "In Production",
            _ => null
        };

    private static string? Prefer(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first : string.IsNullOrWhiteSpace(second) ? null : second;

    /// <summary>
    /// TheTVDB's aired episode list with absolute numbers, blanks filled from
    /// TMDb's list by season and episode number.
    /// </summary>
    private async Task<IReadOnlyList<MetadataSeason>> GetTvdbCatalogueAsync(int tmdbId, CancellationToken cancellationToken)
    {
        var tvdbId = await ResolveTvdbIdForTmdbAsync(tmdbId, cancellationToken);
        if (tvdbId is null)
        {
            return [];
        }

        var seasons = await TvdbCatalogueAsync(tvdbId, cancellationToken);
        if (seasons is not { Count: > 0 })
        {
            return [];
        }

        IReadOnlyDictionary<(int Season, int Episode), MetadataEpisode>? tmdbEpisodes = null;
        var needsFallback = seasons.Any(season => (season.Episodes ?? []).Any(episode =>
            string.IsNullOrWhiteSpace(episode.Title) || string.IsNullOrWhiteSpace(episode.Overview) || episode.AirDate is null));
        if (needsFallback)
        {
            try
            {
                tmdbEpisodes = (await GetTmdbSeriesCatalogueAsync(tmdbId.ToString(CultureInfo.InvariantCulture), cancellationToken))
                    .SelectMany(season => season.Episodes)
                    .GroupBy(episode => (episode.SeasonNumber, episode.EpisodeNumber))
                    .ToDictionary(group => group.Key, group => group.First());
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(exception, "TMDb could not fill the gaps in TheTVDB's episode list for series {ProviderId}.", tmdbId);
            }
        }

        return seasons
            .Where(season => season.Episodes is { Count: > 0 })
            .OrderBy(season => season.SeasonNumber)
            .Select(season =>
            {
                var episodes = season.Episodes!
                    .Select(episode =>
                    {
                        MetadataEpisode? fallback = null;
                        tmdbEpisodes?.TryGetValue((season.SeasonNumber, episode.EpisodeNumber), out fallback);
                        return new MetadataEpisode(
                            season.SeasonNumber,
                            episode.EpisodeNumber,
                            Prefer(NullIfBlank(episode.Title), fallback?.Title),
                            Prefer(NullIfBlank(episode.Overview), fallback?.Overview),
                            ParseAirDate(episode.AirDate) ?? fallback?.AirDateUtc,
                            episode.AbsoluteNumber,
                            TvdbSourceName);
                    })
                    .ToArray();
                return new MetadataSeason(
                    season.SeasonNumber,
                    NullIfBlank(season.Name),
                    episodes.Length,
                    ParseDateOnly(season.AirDate),
                    episodes);
            })
            .ToArray();
    }

    private async Task<string?> ResolveTvdbIdForTmdbAsync(int tmdbId, CancellationToken cancellationToken)
    {
        var key = tmdbId.ToString(CultureInfo.InvariantCulture);
        if (TmdbToTvdbIds.TryGetValue(key, out var known))
        {
            return known;
        }

        var lookup = await ResolveTmdbProviderRecordAsync(new MetadataLookupRequest(null, "tv", null, key), cancellationToken);
        var result = lookup.Result;
        var tvdbId = result?.TvDbId;
        if (string.IsNullOrWhiteSpace(tvdbId) && !string.IsNullOrWhiteSpace(result?.ImdbId))
        {
            tvdbId = await TvdbFindByImdbAsync(result.ImdbId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(tvdbId))
        {
            return null;
        }

        TmdbToTvdbIds[key] = tvdbId;
        return tvdbId;
    }

    // ---- Access: broker first, this install's own key second (never in broker-only mode) ----

    private Task<IReadOnlyList<TvdbSeriesRecord>?> TvdbSearchAsync(string query, int? year, CancellationToken cancellationToken)
        => TvdbAsync<IReadOnlyList<TvdbSeriesRecord>>(
            $"search:{query.ToLowerInvariant()}|{year}",
            TvdbSearchTtl,
            (baseUrl) => $"{baseUrl}/tvdb/search?query={Uri.EscapeDataString(query)}" +
                         (year is > 0 ? $"&year={year.Value.ToString(CultureInfo.InvariantCulture)}" : string.Empty),
            async (broker, url, ct) => (await BrokerTvdbGetAsync<TvdbSearchResponse>(broker, url, "metadata.broker.tvdb.search", ct))?.Results,
            async (client, ct) => await client.SearchAsync(query, year, ct),
            cancellationToken);

    private async Task<TvdbSeriesRecord?> TvdbSeriesAsync(string tvdbId, CancellationToken cancellationToken)
        => await TvdbAsync<TvdbSeriesRecord>(
            $"series:{tvdbId}",
            TvdbSeriesTtl,
            baseUrl => $"{baseUrl}/tvdb/tv/{Uri.EscapeDataString(tvdbId)}",
            async (broker, url, ct) => (await BrokerTvdbGetAsync<TvdbSeriesResponse>(broker, url, "metadata.broker.tvdb.series", ct))?.Series,
            (client, ct) => client.GetSeriesAsync(tvdbId, ct),
            cancellationToken);

    private async Task<string?> TvdbFindByImdbAsync(string imdbId, CancellationToken cancellationToken)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(imdbId, @"^tt\d{1,12}$"))
        {
            return null;
        }

        return await TvdbAsync<string>(
            $"remote:{imdbId}",
            TimeSpan.FromHours(6),
            baseUrl => $"{baseUrl}/tvdb/remote/{imdbId}",
            async (broker, url, ct) => (await BrokerTvdbGetAsync<TvdbRemoteResponse>(broker, url, "metadata.broker.tvdb.remote", ct))?.TvdbId,
            (client, ct) => client.FindByImdbIdAsync(imdbId, ct),
            cancellationToken);
    }

    private Task<IReadOnlyList<TvdbSeason>?> TvdbCatalogueAsync(string tvdbId, CancellationToken cancellationToken)
        => TvdbAsync<IReadOnlyList<TvdbSeason>>(
            null, // an episode list must be as fresh as the gateway's own six hours
            TimeSpan.Zero,
            baseUrl => $"{baseUrl}/tvdb/tv/{Uri.EscapeDataString(tvdbId)}/catalogue",
            async (broker, url, ct) => (await BrokerTvdbGetAsync<TvdbCatalogueResponse>(broker, url, "metadata.broker.tvdb.catalogue", ct))?.Seasons,
            async (client, ct) => await client.GetCatalogueAsync(tvdbId, ct),
            cancellationToken);

    private async Task<IReadOnlyList<MetadataEpisodeGroup>?> TvdbEpisodeGroupsAsync(string tvdbId, CancellationToken cancellationToken)
        => await TvdbAsync<IReadOnlyList<MetadataEpisodeGroup>>(
            null,
            TimeSpan.Zero,
            baseUrl => $"{baseUrl}/tvdb/tv/{Uri.EscapeDataString(tvdbId)}/episode-groups",
            async (broker, url, ct) =>
            {
                var response = await BrokerTvdbGetAsync<BrokerEpisodeGroupsResponse>(broker, url, "metadata.broker.tvdb.episode-groups", ct);
                return response?.Groups is null
                    ? null
                    : (IReadOnlyList<MetadataEpisodeGroup>?)response.Groups
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
            },
            async (client, ct) => await client.GetEpisodeGroupsAsync(tvdbId, ct),
            cancellationToken);

    /// <summary>
    /// Runs one TheTVDB question. The broker answers when there is one and the
    /// mode allows it; otherwise (or when it cannot) a key on this install
    /// answers in direct and hybrid mode. Null means "TheTVDB could not say" and
    /// is never an error: the caller falls back to TMDb. A TheTVDB failure is
    /// not left in <see cref="LastFailure"/>: a show TMDb could still describe
    /// is not a metadata outage.
    /// </summary>
    private async Task<T?> TvdbAsync<T>(
        string? memoryKey,
        TimeSpan memoryTtl,
        Func<string, string> brokerUrl,
        Func<string, string, CancellationToken, Task<T?>> fromBroker,
        Func<TvdbDirectClient, CancellationToken, Task<T?>> fromDirect,
        CancellationToken cancellationToken)
    {
        if (memoryKey is not null &&
            TvdbMemory.TryGetValue(memoryKey, out var hit) &&
            hit.Expires > timeProvider.GetUtcNow() &&
            hit.Value is T cached)
        {
            return cached;
        }

        var config = await GetMetadataConfigurationAsync(cancellationToken);
        var failureBefore = LastFailure;
        T? value = default;
        try
        {
            if (config.ProviderMode is "broker" or "hybrid" && !string.IsNullOrWhiteSpace(config.BrokerUrl))
            {
                value = await fromBroker(config.BrokerUrl, brokerUrl(BuildGatewayBaseUrl(config.BrokerUrl)), cancellationToken);
            }

            if (value is null && config.ProviderMode is "direct" or "hybrid")
            {
                var (key, pin) = await GetTvdbCredentialsAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(key) &&
                    await outboundRequestThrottle.TryAcquireAsync(
                        "api4.thetvdb.com",
                        OutboundRate.MetadataProviderDefault,
                        MaxMetadataThrottleWait,
                        cancellationToken) is not null)
                {
                    value = await fromDirect(new TvdbDirectClient(httpClient, key, pin), cancellationToken);
                }
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(exception, "TheTVDB request failed; TMDb will be used instead.");
            value = default;
        }
        finally
        {
            Interlocked.Exchange(ref _lastFailure, failureBefore);
        }

        if (value is not null && memoryKey is not null)
        {
            TvdbMemory[memoryKey] = (timeProvider.GetUtcNow().Add(memoryTtl), value);
        }

        return value;
    }

    private Task<T?> BrokerTvdbGetAsync<T>(string brokerUrl, string url, string operation, CancellationToken cancellationToken)
        => GetJsonWithResilienceAsync<T>(
            url,
            $"metadata:broker:tvdb:{BuildHostKey(brokerUrl)}",
            operation,
            cancellationToken);

    private async Task<(string? Key, string? Pin)> GetTvdbCredentialsAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var key = configuration["Deluno:Metadata:TvdbApiKey"]
                  ?? configuration["TVDB_API_KEY"]
                  ?? Environment.GetEnvironmentVariable("TVDB_API_KEY");
        var pin = configuration["Deluno:Metadata:TvdbPin"]
                  ?? configuration["TVDB_PIN"]
                  ?? Environment.GetEnvironmentVariable("TVDB_PIN");
        return (string.IsNullOrWhiteSpace(key) ? null : key.Trim(), string.IsNullOrWhiteSpace(pin) ? null : pin.Trim());
    }
}
