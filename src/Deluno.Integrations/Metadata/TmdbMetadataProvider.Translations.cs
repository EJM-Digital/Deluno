using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.Metadata;

// Localized title, overview and tagline (#1018).
//
// Radarr keeps every translation TMDb publishes for a film, per language, and
// shows the one chosen as "Movie info language"; Sonarr has no series
// translations. Deluno's chosen language is the metadata language in Settings.
// English (or blank) is today's behaviour and changes nothing.
//
// The chain, per field: TheTVDB (TV only, when the show has a TheTVDB id), then
// TMDb's translations, then what the provider already sent. The title itself is
// never replaced - folders, searches and the blocklist use it - the localized
// name is carried beside it, and every known spelling stays in AlternateTitles
// so release matching still sees all of them.
public sealed partial class TmdbMetadataProvider
{
    private static readonly TimeSpan TranslationsTtl = TimeSpan.FromHours(6);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyList<MetadataTranslation> Value)> TmdbTranslationMemory = new();

    // TheTVDB names languages by three-letter code; the setting is a TMDb-style
    // code (fr, pt-BR). The common ones; anything else skips TheTVDB and TMDb answers.
    private static readonly Dictionary<string, string> TvdbLanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = "ara", ["bg"] = "bul", ["cs"] = "ces", ["da"] = "dan", ["de"] = "deu", ["el"] = "ell",
        ["es"] = "spa", ["fi"] = "fin", ["fr"] = "fra", ["he"] = "heb", ["hi"] = "hin", ["hr"] = "hrv",
        ["hu"] = "hun", ["it"] = "ita", ["ja"] = "jpn", ["ko"] = "kor", ["nl"] = "nld", ["no"] = "nor",
        ["pl"] = "pol", ["pt"] = "por", ["ro"] = "ron", ["ru"] = "rus", ["sk"] = "slk", ["sv"] = "swe",
        ["th"] = "tha", ["tr"] = "tur", ["uk"] = "ukr", ["vi"] = "vie", ["zh"] = "zho"
    };

    /// <summary>True when the saved language means "as the provider sends it" (blank or English).</summary>
    internal static bool IsDefaultLanguage(string? language)
        => string.IsNullOrWhiteSpace(language) ||
           string.Equals(language.Trim().Split('-', '_')[0], "en", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lays the saved language's translation over one detailed result. Anything
    /// that goes wrong leaves the result exactly as it was: a translation is
    /// never a reason for metadata to fail.
    /// </summary>
    private async Task<MetadataSearchResult> ApplyLanguageAsync(MetadataSearchResult result, CancellationToken cancellationToken)
    {
        var translationWanted = false;
        try
        {
            var settings = await platformRepository.GetAsync(cancellationToken);
            var language = NormalizeLanguage(settings.MetadataLanguage);
            if (IsDefaultLanguage(language) || language is null ||
                !string.Equals(result.Provider, ProviderName, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(result.ProviderId))
            {
                return result;
            }

            translationWanted = true;
            var isTv = string.Equals(NormalizeMediaType(result.MediaType), "tv", StringComparison.Ordinal);
            TvdbTranslation? tvdb = null;
            if (isTv && !string.IsNullOrWhiteSpace(result.TvDbId))
            {
                tvdb = await TvdbTranslationAsync(result.TvDbId, language, cancellationToken);
            }

            var tmdbList = await TmdbTranslationsAsync(result, isTv, cancellationToken);
            // A translation that could not be fetched is not the same as one that
            // does not exist: the English text must not go on top of wording an
            // earlier refresh already translated (#1091).
            return tmdbList is null && tvdb is null
                ? WithholdUntranslatedText(result)
                : ApplyTranslation(result, language, tvdb, tmdbList);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(exception, "Translations for {ProviderId} were unavailable; keeping what is stored.", result.ProviderId);
            return translationWanted ? WithholdUntranslatedText(result) : result;
        }
    }

    /// <summary>
    /// The result without its untranslated wording, for when a translation was
    /// wanted but could not be fetched. The write keeps the text already stored
    /// where a field arrives empty, so the stored translation survives.
    /// </summary>
    internal static MetadataSearchResult WithholdUntranslatedText(MetadataSearchResult result)
        => result with { Overview = null, Tagline = null };

    /// <summary>
    /// The fallback chain, kept pure so it can be tested: TheTVDB, then TMDb,
    /// then the original text. Field by field - a show TheTVDB names but does
    /// not describe still gets TMDb's translated overview.
    /// </summary>
    internal static MetadataSearchResult ApplyTranslation(
        MetadataSearchResult result,
        string language,
        TvdbTranslation? tvdb,
        IReadOnlyList<MetadataTranslation>? tmdb)
    {
        var picked = PickTranslation(tmdb, language);
        var title = Prefer(tvdb?.Title, picked?.Title);
        var overview = Prefer(tvdb?.Overview, picked?.Overview);
        var tagline = Prefer(picked?.Tagline, null);
        if (title is null && overview is null && tagline is null)
        {
            return result;
        }

        // TheTVDB answers first, so it is the source when it gave the title.
        var source = !string.IsNullOrWhiteSpace(tvdb?.Title) || (title is null && !string.IsNullOrWhiteSpace(tvdb?.Overview))
            ? "tvdb"
            : "tmdb";
        var known = (result.AlternateTitles ?? []).ToList();
        if (title is not null &&
            !string.Equals(title, result.Title, StringComparison.OrdinalIgnoreCase) &&
            !known.Contains(title, StringComparer.OrdinalIgnoreCase))
        {
            known.Add(title);
        }

        return result with
        {
            LocalizedTitle = title,
            LocalizedLanguage = language,
            LocalizedSource = source,
            Overview = overview ?? result.Overview,
            Tagline = tagline ?? result.Tagline,
            AlternateTitles = known.Count > 0 ? known : result.AlternateTitles
        };
    }

    /// <summary>The entry for exactly this language and region, else the same language in any region.</summary>
    internal static MetadataTranslation? PickTranslation(IReadOnlyList<MetadataTranslation>? translations, string language)
    {
        if (translations is not { Count: > 0 })
        {
            return null;
        }

        var parts = language.Trim().Split('-', '_');
        var baseCode = parts[0];
        var region = parts.Length > 1 ? parts[1] : null;
        var sameLanguage = translations
            .Where(entry => string.Equals(entry.Language, baseCode, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sameLanguage.Length == 0)
        {
            return null;
        }

        return (region is null ? null : sameLanguage.FirstOrDefault(entry => string.Equals(entry.Region, region, StringComparison.OrdinalIgnoreCase)))
               ?? sameLanguage[0];
    }

    private async Task<TvdbTranslation?> TvdbTranslationAsync(string tvdbId, string language, CancellationToken cancellationToken)
    {
        var baseCode = language.Trim().Split('-', '_')[0];
        if (!TvdbLanguageCodes.TryGetValue(baseCode, out var code))
        {
            return null;
        }

        return await TvdbAsync<TvdbTranslation>(
            $"translation:{tvdbId}:{code}",
            TranslationsTtl,
            baseUrl => $"{baseUrl}/tvdb/tv/{Uri.EscapeDataString(tvdbId)}/translations/{code}",
            (broker, url, ct) => BrokerTvdbGetAsync<TvdbTranslation>(broker, url, "metadata.broker.tvdb.translation", ct),
            (client, ct) => client.GetTranslationAsync(tvdbId, code, ct),
            cancellationToken);
    }

    /// <summary>Every translation TMDb has for a title: the broker first, then this install's own key (never in broker-only mode).</summary>
    private async Task<IReadOnlyList<MetadataTranslation>?> TmdbTranslationsAsync(
        MetadataSearchResult result,
        bool isTv,
        CancellationToken cancellationToken)
    {
        var kind = isTv ? "tv" : "movie";
        var key = $"{kind}:{result.ProviderId}";
        if (TmdbTranslationMemory.TryGetValue(key, out var hit) && hit.Expires > timeProvider.GetUtcNow())
        {
            return hit.Value;
        }

        var config = await GetMetadataConfigurationAsync(cancellationToken);
        IReadOnlyList<MetadataTranslation>? translations = null;
        var attempted = false;
        if (config.ProviderMode is "broker" or "hybrid" && !string.IsNullOrWhiteSpace(config.BrokerUrl))
        {
            attempted = true;
            var url = $"{BuildGatewayBaseUrl(config.BrokerUrl)}/{kind}/{Uri.EscapeDataString(result.ProviderId)}/translations";
            var response = await GetJsonWithResilienceAsync<JsonElement?>(
                url,
                $"metadata:broker:translations:{BuildHostKey(config.BrokerUrl)}",
                "metadata.broker.translations",
                cancellationToken);
            translations = ReadTranslations(response, direct: false);
        }

        if (translations is null && config.ProviderMode is "direct" or "hybrid")
        {
            var apiKey = config.TmdbApiKey ?? await GetApiKeyAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                attempted = true;
                var response = await GetJsonWithResilienceAsync<JsonElement?>(
                    $"https://api.themoviedb.org/3/{kind}/{Uri.EscapeDataString(result.ProviderId)}/translations?api_key={Uri.EscapeDataString(apiKey)}",
                    "metadata:tmdb:translations",
                    "metadata.tmdb.translations",
                    cancellationToken);
                translations = ReadTranslations(response, direct: true);
                if (translations is null && response is null && LastFailure?.HttpStatus == 404)
                {
                    // TMDb answering "not found" means it has no translations for
                    // this title, which is an answer, not a failed lookup.
                    translations = [];
                }
            }
        }

        if (translations is null)
        {
            // Null says the lookup failed; an empty list says TMDb has none.
            return attempted ? null : [];
        }

        TmdbTranslationMemory[key] = (timeProvider.GetUtcNow().Add(TranslationsTtl), translations);
        return translations;
    }

    /// <summary>Reads the gateway's compact list, or TMDb's own (<c>iso_639_1</c>, <c>data.name</c>) when <paramref name="direct"/>.</summary>
    internal static IReadOnlyList<MetadataTranslation>? ReadTranslations(JsonElement? payload, bool direct)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("translations", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var entries = new List<MetadataTranslation>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? language, region, title, overview, tagline;
            if (direct)
            {
                language = Str(item, "iso_639_1");
                region = Str(item, "iso_3166_1");
                if (!item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                title = Str(data, "title") ?? Str(data, "name");
                overview = Str(data, "overview");
                tagline = Str(data, "tagline");
            }
            else
            {
                language = Str(item, "language");
                region = Str(item, "region");
                title = Str(item, "title");
                overview = Str(item, "overview");
                tagline = Str(item, "tagline");
            }

            if (language is not null && (title is not null || overview is not null || tagline is not null))
            {
                entries.Add(new MetadataTranslation(language, region, title, overview, tagline));
            }
        }

        return entries;
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
