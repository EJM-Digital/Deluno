using Deluno.Contracts;
using Deluno.Integrations.Search;
using Deluno.Media;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Worker.Jobs;

/// <summary>Everything one quality profile says to a search: its custom formats, its rules, its release-preference plan.</summary>
internal sealed record SearchProfileBundle(
    IReadOnlyList<CustomFormatItem> CustomFormats,
    ProfileSearchRules Rules,
    ReleasePreferencePlan? Plan);

/// <summary>
/// The profile bundle a search judges each title by (#1157).
///
/// <para>A search cycle walks a library, and the library's profile is only the
/// default: a title with a profile of its own is judged by that one, so a film
/// set to a 4K profile in a 1080p library is looked for in 4K and a 1080p
/// release is refused. Resolving is cached by profile, so a library of ten
/// thousand titles that follow the library reads the profile once.</para>
/// </summary>
internal sealed class SearchProfileBundles
{
    private readonly IQualityRepository qualityRepository;
    private readonly IReleasePreferencePlanRepository? planRepository;
    private readonly string? properRepackAcquisitionMode;
    private readonly TitleProfileLookup? titleProfiles;
    private readonly string? defaultProfileId;
    private readonly Dictionary<string, SearchProfileBundle> cache = new(StringComparer.OrdinalIgnoreCase);

    public SearchProfileBundles(
        IQualityRepository qualityRepository,
        IReleasePreferencePlanRepository? planRepository,
        string? properRepackAcquisitionMode,
        IMediaStateRepository? mediaState,
        string? defaultProfileId)
    {
        this.qualityRepository = qualityRepository;
        this.planRepository = planRepository;
        this.properRepackAcquisitionMode = properRepackAcquisitionMode;
        titleProfiles = mediaState is null ? null : new TitleProfileLookup(mediaState, qualityRepository);
        this.defaultProfileId = defaultProfileId;
    }

    /// <summary>The bundle that judges this title: its own profile when it has one, else the library's.</summary>
    public async Task<SearchProfileBundle> ForTitleAsync(MediaKind kind, string titleId, CancellationToken cancellationToken)
    {
        var own = titleProfiles is null ? null : await titleProfiles.ForTitleAsync(kind, titleId, cancellationToken);
        return await ForProfileAsync(own?.Id ?? defaultProfileId, cancellationToken);
    }

    public async Task<SearchProfileBundle> ForProfileAsync(string? profileId, CancellationToken cancellationToken)
    {
        var key = profileId ?? string.Empty;
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var customFormats = await SearchExecutionSupport.ResolveCustomFormatsAsync(qualityRepository, profileId, cancellationToken);
        var rules = await QualityProfileResolver.ResolveSearchRulesAsync(qualityRepository, profileId, cancellationToken);
        var plan = await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
            qualityRepository,
            planRepository,
            profileId,
            cancellationToken,
            customFormats,
            properRepackAcquisitionMode);
        return cache[key] = new SearchProfileBundle(customFormats, rules, plan);
    }
}
