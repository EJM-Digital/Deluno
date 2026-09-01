using Deluno.Quality.Contracts;

namespace Deluno.Quality.Scenarios;

/// <summary>
/// A scenario is a user-facing starting point for one Media Plan. It describes
/// the complete intent that Deluno can carry into the existing policy-set
/// runtime; it is not a second release-ranking model.
/// </summary>
public sealed record MediaPlanScenario(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> MediaTypes,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<MediaPlanScenarioVariant> Variants,
    int Version)
{
    public MediaPlanScenarioVariant? ForMediaType(string mediaType)
        => Variants.FirstOrDefault(variant =>
            string.Equals(variant.MediaType, NormalizeMediaType(mediaType), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeMediaType(string value)
        => string.Equals(value?.Trim(), "tv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "tv shows", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "tvshows", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movies";
}

public sealed record MediaPlanScenarioVariant(
    string MediaType,
    string QualityPresetId,
    string SizeTierId,
    string SizeTierName,
    string SizeDescription,
    int SearchIntervalHours,
    int RetryDelayHours,
    bool UpgradeUntilCutoff,
    string SubtitleIntent,
    string RoutingIntent,
    string SharingIntent,
    string CleanupIntent,
    string NotificationIntent,
    string NamingIntent,
    string Summary);

/// <summary>
/// A scenario's intent is not automatically an active setting. This explicit
/// status keeps the preview truthful while the whole-plan fields are being
/// wired into their owning runtimes.
/// </summary>
public sealed record MediaPlanScenarioBehavior(
    string Id,
    string Area,
    string Intent,
    string ApplicationStatus,
    string Explanation,
    string? ConfigurationSurface = null);

/// <summary>
/// The deterministic result of compiling a scenario. The policy request is
/// deliberately the existing CreatePolicySetRequest so applying a scenario
/// cannot create a parallel plan or ranking engine.
/// </summary>
public sealed record MediaPlanScenarioCompilation(
    string ScenarioId,
    int ScenarioVersion,
    string ScenarioName,
    string MediaType,
    string QualityPresetId,
    MediaPlanScenarioVariant Variant,
    CreatePolicySetRequest PolicySet,
    IReadOnlyList<string> IncludedBehaviors,
    IReadOnlyList<string> Requirements,
    string Summary,
    IReadOnlyList<MediaPlanScenarioBehavior>? Behaviors = null);

public sealed record ApplyMediaPlanScenarioRequest(
    string? MediaType = null,
    string? Name = null,
    bool? IsEnabled = null,
    string? BasePlanHash = null);

/// <summary>
/// The server-side result an owner reviews before applying a newer scenario to
/// an existing Media Plan. The base hash is a concurrency precondition, not a
/// hidden approval token: it proves the preview was based on the same immutable
/// plan content that the owner is about to change.
/// </summary>
public sealed record MediaPlanScenarioUpdatePreview(
    MediaPlanScenarioCompilation Scenario,
    MediaPlanPreview Preview);

/// <summary>
/// Identifies the one generated plan for a scenario *and* media type. A
/// scenario can deliberately have Movie and TV variants, so matching the
/// scenario marker alone could otherwise overwrite the other variant.
/// </summary>
public static class MediaPlanScenarioPlanIdentity
{
    public static bool Matches(PolicySetItem plan, string scenarioId, string mediaType)
    {
        if (!string.Equals(NormalizeMediaType(plan.MediaType), NormalizeMediaType(mediaType), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(plan.AutomationIntent?.ScenarioId, scenarioId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Older generated plans predate the structured scenario id. Retain a
        // narrow, line-based fallback so they remain discoverable without
        // treating a display name or a partial marker as identity.
        return (plan.Notes ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line =>
            {
                const string prefix = "Scenario:";
                if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var marker = line[prefix.Length..].Trim()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                return string.Equals(marker, scenarioId, StringComparison.OrdinalIgnoreCase);
            });
    }

    private static string NormalizeMediaType(string? value)
        => string.Equals(value?.Trim(), "tv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "tv shows", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "tvshows", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movies";
}

public static class MediaPlanScenarioCompiler
{
    public static MediaPlanScenarioCompilation Compile(
        string scenarioId,
        string? mediaType = null,
        string? nameOverride = null,
        bool? isEnabled = null)
    {
        var scenario = MediaPlanScenarioCatalog.Find(scenarioId)
            ?? throw new KeyNotFoundException($"Media Plan scenario '{scenarioId}' was not found.");

        var normalizedMediaType = NormalizeMediaType(mediaType);
        if (string.IsNullOrWhiteSpace(mediaType) && scenario.Variants.Count != 1)
        {
            throw new ArgumentException(
                $"Scenario '{scenario.Id}' applies to both Movies and TV. Choose a mediaType before compiling it.",
                nameof(mediaType));
        }

        var variant = scenario.ForMediaType(normalizedMediaType)
            ?? throw new ArgumentException(
                $"Scenario '{scenario.Id}' does not apply to media type '{normalizedMediaType}'.",
                nameof(mediaType));

        var name = NormalizeName(nameOverride)
            ?? $"{scenario.Name} · {(variant.MediaType == "tv" ? "TV" : "Movies")}";
        var behaviors = DescribeBehaviors(variant);
        var includedBehaviors = new[]
        {
            $"Quality target comes from the '{variant.QualityPresetId}' starter.",
            $"Size tier: {variant.SizeTierName} — {variant.SizeDescription}",
            $"Search every {variant.SearchIntervalHours} hours and retry after {variant.RetryDelayHours} hours.",
            variant.UpgradeUntilCutoff
                ? "Allow improvements until the quality cutoff is met."
                : "Take the first acceptable release and do not schedule quality upgrades.",
            $"Subtitles: {variant.SubtitleIntent}",
            $"Routing: {variant.RoutingIntent}",
            $"Sharing and retention: {variant.SharingIntent}",
            $"Cleanup: {variant.CleanupIntent}",
            $"Notifications: {variant.NotificationIntent}",
            $"Naming: {variant.NamingIntent}"
        };
        var summary = $"{scenario.Name} · {(variant.MediaType == "tv" ? "TV" : "Movies")}: {variant.Summary}";
        var notes = string.Join(
            "\n",
            $"Scenario: {scenario.Id} v{scenario.Version}",
            summary,
            "Generated by Deluno's scenario compiler; refine fields on the Media Plan after creation.");

        var policySet = new CreatePolicySetRequest(
            Name: name,
            MediaType: variant.MediaType,
            QualityProfileId: null,
            DestinationRuleId: null,
            CustomFormatIds: null,
            SearchIntervalOverrideHours: variant.SearchIntervalHours,
            RetryDelayOverrideHours: variant.RetryDelayHours,
            UpgradeUntilCutoff: variant.UpgradeUntilCutoff,
            IsEnabled: isEnabled ?? true,
            Notes: notes,
            AutomationIntent: new MediaPlanAutomationIntent(
                ScenarioId: scenario.Id,
                ScenarioVersion: scenario.Version,
                SizeTierId: variant.SizeTierId,
                SizeTierName: variant.SizeTierName,
                SizeDescription: variant.SizeDescription,
                SubtitleIntent: variant.SubtitleIntent,
                RoutingIntent: variant.RoutingIntent,
                SharingIntent: variant.SharingIntent,
                CleanupIntent: variant.CleanupIntent,
                NotificationIntent: variant.NotificationIntent,
                NamingIntent: variant.NamingIntent));

        return new MediaPlanScenarioCompilation(
            scenario.Id,
            scenario.Version,
            scenario.Name,
            variant.MediaType,
            variant.QualityPresetId,
            variant,
            policySet,
            includedBehaviors,
            scenario.Requirements,
            summary,
            behaviors);
    }

    public static IReadOnlyList<MediaPlanScenarioBehavior> DescribeBehaviors(
        MediaPlanScenarioVariant variant)
        =>
        [
            new(
                "quality",
                "Quality",
                $"Use the '{variant.QualityPresetId}' starter as the quality target.",
                "applied",
                "The generated plan applies the matching Quality Profile when the scenario is applied.",
                "Quality Profiles"),
            new(
                "size",
                "Size",
                $"Use the {variant.SizeTierName.ToLowerInvariant()} size tier: {variant.SizeDescription}",
                "requires-configuration",
                "This is a scenario recommendation. Configure the corresponding Size Rules before relying on it; the current policy contract does not silently turn prose into a size limit.",
                "Size Rules"),
            new(
                "search-cadence",
                "Search cadence",
                $"Search every {variant.SearchIntervalHours} hours and retry after {variant.RetryDelayHours} hours.",
                "applied",
                "These intervals are persisted on the generated Media Plan and are consumed by scheduled searches.",
                "Media Plans"),
            new(
                "upgrade-stop",
                "Upgrade stopping",
                variant.UpgradeUntilCutoff
                    ? "Allow improvements until the quality cutoff is met."
                    : "Take the first acceptable release and do not schedule quality upgrades.",
                "applied",
                "The generated Media Plan persists the upgrade-stop choice.",
                "Media Plans"),
            new(
                "subtitles",
                "Subtitles",
                variant.SubtitleIntent,
                "requires-configuration",
                "The scenario records the subtitle intent for review. Select the provider, languages, and fallback behaviour in Subtitle settings before enabling that outcome.",
                "Subtitle settings"),
            new(
                "routing",
                "Routing",
                variant.RoutingIntent,
                "requires-configuration",
                "The scenario records the source-routing intent for review. Configure and test the relevant indexers and download clients before relying on the route.",
                "Connections"),
            new(
                "sharing-retention",
                "Sharing and retention",
                variant.SharingIntent,
                "informational",
                "Sharing obligations belong to each configured download source. This scenario does not override source-level seeding or retention policy.",
                "Connections"),
            new(
                "cleanup",
                "Cleanup",
                variant.CleanupIntent,
                "informational",
                "The cleanup intent is shown here, but source cleanup remains governed by the download client and import lifecycle.",
                "Queue and imports"),
            new(
                "notifications",
                "Notifications",
                variant.NotificationIntent,
                "requires-configuration",
                "Configure notification destinations and event filters separately; the scenario does not create a hidden notification rule.",
                "Notifications"),
            new(
                "naming",
                "Naming",
                variant.NamingIntent,
                "requires-configuration",
                "Select and preview the naming templates in the library or system naming settings before applying this intent.",
                "Naming settings")
        ];

    private static string NormalizeMediaType(string? value)
        => string.Equals(value?.Trim(), "tv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "tv shows", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "tvshows", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movies";

    private static string? NormalizeName(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class MediaPlanScenarioCatalog
{
    public static readonly IReadOnlyList<MediaPlanScenario> All =
    [
        Both(
            "family-1080p",
            "Family 1080p",
            "A dependable everyday plan for shared viewing, sensible storage, and clear upgrade behaviour.",
            ["No special hardware is required.", "A library destination and at least one healthy source are still required."],
            MoviesVariant(
                "standard-movies", "balanced", "Balanced", "Typical 1080p files without the large remux footprint.",
                12, 6, true, "Use the library subtitle preferences.", "Use healthy available sources.",
                "Inherit the source policy.", "Keep the source until import is verified.", "Notify on attention or failure.",
                "Use the library movie naming template.", "Reliable 1080p movies for shared viewing."),
            TvVariant(
                "hd-tv", "balanced", "Balanced", "Typical 1080p episode files without requiring 4K storage.",
                12, 6, true, "Use the library subtitle preferences.", "Use healthy available sources.",
                "Inherit the source policy.", "Keep the source until import is verified.", "Notify on attention or failure.",
                "Use the library series and episode naming templates.", "Reliable 1080p TV for shared viewing.")),
        Both(
            "premium-4k-hdr",
            "Premium 4K HDR",
            "A high-quality home-theatre plan with HDR-aware goals and deliberate storage expectations.",
            ["A 4K-capable display and playback path are recommended.", "Expect larger files and longer searches."],
            MoviesVariant(
                "4k-movies", "large", "Large", "High-quality 4K files; remux and lossless releases may be large.",
                6, 3, true, "Prefer the library subtitle languages.", "Prefer sources with the required 4K/HDR release.",
                "Inherit the source policy.", "Retain the source until the high-quality import is verified.", "Notify on upgrade and attention.",
                "Use the library movie naming template.", "Premium 4K HDR movies for capable home theatre playback."),
            TvVariant(
                "4k-tv", "large", "Large", "High-quality 4K episodes; storage use is intentionally higher.",
                6, 3, true, "Prefer the library subtitle languages.", "Prefer sources with the required 4K/HDR release.",
                "Inherit the source policy.", "Retain the source until the high-quality import is verified.", "Notify on upgrade and attention.",
                "Use the library series and episode naming templates.", "Premium 4K HDR TV for capable home theatre playback.")),
        Both(
            "low-storage",
            "Low Storage",
            "A compact plan that values predictable disk use and availability over repeated upgrades.",
            ["Quality remains bounded by the selected starter profile.", "Large/remux releases are not the default."],
            MoviesVariant(
                "standard-movies", "compact", "Compact", "Prefer smaller everyday releases and avoid unnecessary remux storage.",
                24, 12, false, "Use only the library subtitle preferences.", "Use the first healthy source that meets the plan.",
                "Inherit the source policy.", "Remove temporary source data after import verification.", "Notify only when attention is needed.",
                "Use the library movie naming template.", "Compact movie storage with minimal replacement churn."),
            TvVariant(
                "standard-tv", "compact", "Compact", "Prefer smaller episode releases and avoid unnecessary replacements.",
                24, 12, false, "Use only the library subtitle preferences.", "Use the first healthy source that meets the plan.",
                "Inherit the source policy.", "Remove temporary source data after import verification.", "Notify only when attention is needed.",
                "Use the library series and episode naming templates.", "Compact TV storage with minimal replacement churn.")),
        Both(
            "usenet-first",
            "Usenet-first",
            "Prefer Usenet when available while retaining safe fallback behaviour for other configured sources.",
            ["Configure a healthy Usenet-capable indexer and download client.", "Fallback sources remain explicit and visible."],
            MoviesVariant(
                "standard-movies", "balanced", "Balanced", "Balanced files with a source route that prefers Usenet.",
                6, 3, true, "Use the library subtitle preferences.", "Usenet first; use other healthy sources as fallback.",
                "Usenet has no seeding obligation; apply each tracker policy independently.", "Keep the source until import is verified.", "Notify on source failure or attention.",
                "Use the library movie naming template.", "Balanced movies with Usenet preferred."),
            TvVariant(
                "hd-tv", "balanced", "Balanced", "Balanced episode files with a source route that prefers Usenet.",
                6, 3, true, "Use the library subtitle preferences.", "Usenet first; use other healthy sources as fallback.",
                "Usenet has no seeding obligation; apply each tracker policy independently.", "Keep the source until import is verified.", "Notify on source failure or attention.",
                "Use the library series and episode naming templates.", "Balanced TV with Usenet preferred.")),
        Both(
            "private-tracker",
            "Private Tracker",
            "Prefer configured private trackers while making the sharing obligation visible and enforceable.",
            ["Configure at least one private tracker.", "Review sharing duration, ratio, and stuck-download handling before enabling automation."],
            MoviesVariant(
                "standard-movies", "balanced", "Balanced", "Balanced files with tracker sharing rules kept in view.",
                6, 3, true, "Use the library subtitle preferences.", "Prefer healthy private trackers, then permitted fallbacks.",
                "Apply strict source sharing and retention rules.", "Retain the client copy until the source obligation is satisfied.", "Notify on sharing or download attention.",
                "Use the library movie naming template.", "Balanced movies with private-tracker obligations respected."),
            TvVariant(
                "hd-tv", "balanced", "Balanced", "Balanced episode files with tracker sharing rules kept in view.",
                6, 3, true, "Use the library subtitle preferences.", "Prefer healthy private trackers, then permitted fallbacks.",
                "Apply strict source sharing and retention rules.", "Retain the client copy until the source obligation is satisfied.", "Notify on sharing or download attention.",
                "Use the library series and episode naming templates.", "Balanced TV with private-tracker obligations respected.")),
        Both(
            "mixed-sources",
            "Mixed Sources",
            "Use the configured source pool together, comparing availability, health, and policy fit in one plan.",
            ["Configure at least two compatible source types for the intended fallback behaviour."],
            MoviesVariant(
                "standard-movies", "balanced", "Balanced", "Balanced releases selected across the configured source pool.",
                6, 3, true, "Use the library subtitle preferences.", "Compare healthy configured sources by plan fit.",
                "Apply each source's own sharing and retention rules.", "Keep source data until import verification and source policy allow cleanup.", "Notify on source failures and attention.",
                "Use the library movie naming template.", "Balanced movies across mixed sources."),
            TvVariant(
                "hd-tv", "balanced", "Balanced", "Balanced episode releases selected across the configured source pool.",
                6, 3, true, "Use the library subtitle preferences.", "Compare healthy configured sources by plan fit.",
                "Apply each source's own sharing and retention rules.", "Keep source data until import verification and source policy allow cleanup.", "Notify on source failures and attention.",
                "Use the library series and episode naming templates.", "Balanced TV across mixed sources.")),
        SingleTv(
            "anime",
            "Anime",
            "A TV plan that makes anime/absolute or scene numbering an explicit series decision instead of guessing from a filename.",
            ["Use a TV library.", "Confirm Standard, Daily, or Anime numbering on each series before importing."],
            TvVariant(
                "standard-tv", "compact", "Compact", "Compact episode files with conservative replacement behaviour.",
                6, 3, true, "Use the library subtitle preferences.", "Compare healthy sources and preserve the chosen numbering context.",
                "Inherit each source's policy.", "Keep the source until every covered episode is verified.", "Notify on numbering mismatch or attention.",
                "Use the library series and episode naming templates.", "Anime-aware TV plan; numbering remains an explicit series choice."))
    ];

    public static MediaPlanScenario? Find(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(scenario => string.Equals(scenario.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    private static MediaPlanScenario Both(
        string id,
        string name,
        string description,
        IReadOnlyList<string> requirements,
        MediaPlanScenarioVariant movies,
        MediaPlanScenarioVariant tv)
        => new(id, name, description, ["movies", "tv"], requirements, [movies, tv], 1);

    private static MediaPlanScenario SingleTv(
        string id,
        string name,
        string description,
        IReadOnlyList<string> requirements,
        MediaPlanScenarioVariant tv)
        => new(id, name, description, ["tv"], requirements, [tv], 1);

    private static MediaPlanScenarioVariant MoviesVariant(
        string qualityPresetId,
        string sizeTierId,
        string sizeTierName,
        string sizeDescription,
        int searchIntervalHours,
        int retryDelayHours,
        bool upgradeUntilCutoff,
        string subtitleIntent,
        string routingIntent,
        string sharingIntent,
        string cleanupIntent,
        string notificationIntent,
        string namingIntent,
        string summary)
        => new("movies", qualityPresetId, sizeTierId, sizeTierName, sizeDescription, searchIntervalHours, retryDelayHours,
            upgradeUntilCutoff, subtitleIntent, routingIntent, sharingIntent, cleanupIntent, notificationIntent, namingIntent, summary);

    private static MediaPlanScenarioVariant TvVariant(
        string qualityPresetId,
        string sizeTierId,
        string sizeTierName,
        string sizeDescription,
        int searchIntervalHours,
        int retryDelayHours,
        bool upgradeUntilCutoff,
        string subtitleIntent,
        string routingIntent,
        string sharingIntent,
        string cleanupIntent,
        string notificationIntent,
        string namingIntent,
        string summary)
        => new("tv", qualityPresetId, sizeTierId, sizeTierName, sizeDescription, searchIntervalHours, retryDelayHours,
            upgradeUntilCutoff, subtitleIntent, routingIntent, sharingIntent, cleanupIntent, notificationIntent, namingIntent, summary);
}
