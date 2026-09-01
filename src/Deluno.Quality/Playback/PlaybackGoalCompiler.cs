using Deluno.Quality.ReleasePreferences;

namespace Deluno.Quality.Playback;

/// <summary>
/// Compiles device-aware playback goals into the same release-preference plan
/// consumed by acquisition. Compatibility is represented as explicit
/// AND-of-OR-of-AND groups: each group must pass, each alternative is a whole
/// device path, and every trait in that path must be proven together.
/// </summary>
public static class PlaybackGoalCompiler
{
    public static PlaybackGoalCompilation Compile(
        PlaybackGoalItem goal,
        PlaybackDeviceGroup? group,
        IReadOnlyList<PlaybackDeviceProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(goal);
        profiles ??= [];

        var warnings = new List<string>();
        var unknownCapabilities = new List<string>();
        var normalizedMediaType = PreferenceTraitRegistry.NormalizeMediaType(goal.MediaType);
        var selectedDevices = SelectDevices(goal, group, profiles, warnings);

        var required = ResolveTraits(
            goal.RequiredTraitIds,
            normalizedMediaType,
            "required",
            warnings);
        var forbidden = ResolveTraits(
            goal.EffectiveForbiddenTraitIds,
            normalizedMediaType,
            "forbidden",
            warnings);
        var requiredAny = ResolveGroups(
            goal.RequiredAnyTraitGroups,
            normalizedMediaType,
            "required-any",
            warnings);
        var compatibilityGroups = new List<PreferenceCompatibilityGroup>();

        if (goal.MustPlay)
        {
            foreach (var device in selectedDevices)
            {
                var supported = ResolveSupportedTraits(device, normalizedMediaType, unknownCapabilities, warnings);
                if (supported.Count == 0)
                {
                    warnings.Add($"Device '{device.Name}' has no proven playback capability; this goal cannot be activated automatically for it.");
                    continue;
                }

                var alternatives = BuildCompatibilityAlternatives(supported, device.Name, warnings);
                if (alternatives.Count > 0)
                {
                    compatibilityGroups.Add(new PreferenceCompatibilityGroup(
                        $"device/{device.Id}",
                        alternatives));
                }
            }

            if (selectedDevices.Count == 0)
            {
                warnings.Add("The playback goal has no enabled device to use as a compatibility gate.");
            }
        }
        else if (selectedDevices.Count > 0)
        {
            warnings.Add("Device capabilities are recorded for explanation only because MustPlay is disabled; they are not hard gates.");
        }

        var preferred = ResolveTraits(
            goal.PreferredTraitIds,
            normalizedMediaType,
            "preferred",
            warnings);
        var stopWhen = ResolveTrait(goal.StopWhenTraitId, normalizedMediaType, "stop-when", warnings);
        var preferenceFamily = BuildPreferenceFamily(preferred, stopWhen, warnings);
        IReadOnlyList<PreferenceFamily> families = preferenceFamily is null ? [] : [preferenceFamily];
        var compiledCompatibilityGroups = CollapseCompatibilityGroups(group, compatibilityGroups);
        var declaredTraits = required
            .Concat(requiredAny.SelectMany(group => group))
            .Concat(forbidden)
            .Concat(preferred)
            .Concat(compiledCompatibilityGroups.SelectMany(group => group.Alternatives.SelectMany(alternative => alternative)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relationships = PreferenceTraitRegistry.Current.Relationships
            .Where(relationship => declaredTraits.Contains(relationship.FromTraitId)
                && declaredTraits.Contains(relationship.ToTraitId))
            .ToArray();
        var dimensionOrder = families.Select(family => family.Id).ToArray();
        var version = $"goal/{goal.UpdatedUtc.UtcTicks}/registry/{PreferenceTraitRegistry.Current.Version}";
        var plan = new ReleasePreferencePlan(
            $"playback/{goal.Id}",
            version,
            normalizedMediaType,
            families,
            required,
            ForbiddenTraitIds: forbidden,
            Relationships: relationships,
            DimensionOrder: dimensionOrder,
            CompatibilityScope: group is null
                ? "no-device-group"
                : $"{PlaybackGoalModes.Normalize(group.Mode)}:{group.Id}",
            Scenario: goal.Name,
            Provenance: $"playback-goal/{goal.Id}; registry={PreferenceTraitRegistry.Current.Version}",
            RequiredAnyTraitGroups: requiredAny,
            CompatibilityGroups: compiledCompatibilityGroups);

        ReleasePreferencePlanValidator.ThrowIfInvalid(plan);
        var requiresReview = warnings.Count > 0 || unknownCapabilities.Count > 0;
        return new PlaybackGoalCompilation(
            goal,
            group,
            selectedDevices,
            plan,
            plan.PlanHash,
            unknownCapabilities.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            requiresReview);
    }

    private static IReadOnlyList<PlaybackDeviceProfile> SelectDevices(
        PlaybackGoalItem goal,
        PlaybackDeviceGroup? group,
        IReadOnlyList<PlaybackDeviceProfile> profiles,
        ICollection<string> warnings)
    {
        if (group is null)
        {
            warnings.Add($"Device group '{goal.DeviceGroupId}' was not found.");
            return [];
        }

        var byId = profiles
            .Where(profile => profile.IsEnabled && !string.IsNullOrWhiteSpace(profile.Id))
            .ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);
        var ids = group.DeviceProfileIds ?? [];
        var selectedIds = ids.ToArray();
        if (PlaybackGoalModes.Normalize(group.Mode) == PlaybackGoalModes.PrimaryDevice
            && group.PrimaryDeviceProfileId is null
            && ids.Count > 1)
        {
            warnings.Add("Primary-device mode has no explicit primary profile; the first listed profile will be the primary preference and the remaining profiles remain the fallback set.");
        }

        var selected = new List<PlaybackDeviceProfile>();
        foreach (var id in selectedIds.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (byId.TryGetValue(id.Trim(), out var profile))
            {
                selected.Add(profile);
            }
            else
            {
                warnings.Add($"Device profile '{id}' is missing or disabled.");
            }
        }

        var distinctSelected = selected
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(grouping => grouping.First())
            .ToArray();

        if (PlaybackGoalModes.Normalize(group.Mode) != PlaybackGoalModes.PrimaryDevice)
        {
            return distinctSelected;
        }

        var primaryId = group.PrimaryDeviceProfileId?.Trim();
        if (primaryId is not null
            && !distinctSelected.Any(profile => string.Equals(profile.Id, primaryId, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add($"Primary device profile '{group.PrimaryDeviceProfileId}' is not enabled in this group; the first enabled profile will be used as primary.");
            primaryId = null;
        }

        primaryId ??= distinctSelected.FirstOrDefault()?.Id;
        return distinctSelected
            .OrderBy(profile => string.Equals(profile.Id, primaryId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<PreferenceCompatibilityGroup> CollapseCompatibilityGroups(
        PlaybackDeviceGroup? group,
        IReadOnlyList<PreferenceCompatibilityGroup> perDeviceGroups)
    {
        if (group is null || perDeviceGroups.Count == 0)
        {
            return [];
        }

        var mode = PlaybackGoalModes.Normalize(group.Mode);
        if (mode == PlaybackGoalModes.EveryDevice)
        {
            return perDeviceGroups;
        }

        // A primary-device goal is an ordered choice for the owner, not a
        // second hard-gate language: if the primary cannot play a release,
        // the explicitly selected fallback devices may still satisfy it. The
        // immutable plan records that all selected paths are alternatives so
        // the evaluator never combines one device's video with another's
        // audio. The group id and compatibility scope retain the mode for the
        // explanation surface.
        var primaryId = group.PrimaryDeviceProfileId?.Trim();
        if (primaryId is null)
        {
            primaryId = perDeviceGroups
                .Select(item => item.Id.StartsWith("device/", StringComparison.OrdinalIgnoreCase) ? item.Id["device/".Length..] : item.Id)
                .FirstOrDefault();
        }

        var rankedAlternatives = perDeviceGroups
            .SelectMany(item => (item.Alternatives ?? [])
                .Select(alternative => new
                {
                    Alternative = alternative,
                    Rank = string.Equals(
                        item.Id.StartsWith("device/", StringComparison.OrdinalIgnoreCase) ? item.Id["device/".Length..] : item.Id,
                        primaryId,
                        StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1
                }))
            .ToArray();
        return rankedAlternatives.Length == 0
            ? []
            : [new PreferenceCompatibilityGroup(
                mode == PlaybackGoalModes.PrimaryDevice
                    ? $"primary-with-fallback/{group.Id}"
                    : $"fallback/{group.Id}",
                rankedAlternatives.Select(item => item.Alternative).ToArray(),
                mode == PlaybackGoalModes.PrimaryDevice
                    ? rankedAlternatives.Select(item => item.Rank).ToArray()
                    : null)];
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildCompatibilityAlternatives(
        IReadOnlyDictionary<string, HashSet<string>> supported,
        string deviceName,
        ICollection<string> warnings)
    {
        const int maximumAlternatives = 256;
        var alternatives = new List<IReadOnlyList<string>> { Array.Empty<string>() };
        foreach (var dimension in supported.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var traits = dimension.Value
                .Where(trait => !string.IsNullOrWhiteSpace(trait))
                .OrderBy(trait => trait, StringComparer.Ordinal)
                .ToArray();
            if (traits.Length == 0)
            {
                continue;
            }

            if (alternatives.Count > maximumAlternatives / traits.Length)
            {
                warnings.Add($"Device '{deviceName}' has more than {maximumAlternatives} capability combinations; its compatibility gate remains review-only until the profile is narrowed.");
                return [];
            }

            alternatives = alternatives
                .SelectMany(prefix => traits.Select(trait => prefix.Concat([trait]).ToArray()))
                .Cast<IReadOnlyList<string>>()
                .ToList();
        }

        return alternatives
            .Where(alternative => alternative.Count > 0)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, HashSet<string>> ResolveSupportedTraits(
        PlaybackDeviceProfile profile,
        string mediaType,
        ICollection<string> unknownCapabilities,
        ICollection<string> warnings)
    {
        var directStates = PlaybackCapabilityFacts.ReadDirectStates(profile, mediaType, warnings);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A specific capability also proves the less-specific capability that
        // the registry explicitly relates to it (for example DV fallback →
        // HDR10). If that companion is explicitly absent or conflicting, the
        // specific assertion is not a safe playback path and remains review-
        // only instead of silently admitting an impossible release.
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var state in directStates.Where(item => item.Value == PreferenceFactState.Present))
            {
                if (blocked.Contains(state.Key)
                    || !HasBlockedRelatedTrait(state.Key, directStates, blocked))
                {
                    continue;
                }

                if (blocked.Add(state.Key))
                {
                    unknownCapabilities.Add($"{profile.Name}: {state.Key} (related capability is not proven compatible)");
                    warnings.Add($"Device '{profile.Name}' has a present '{state.Key}' capability but its related capability path is explicitly absent or conflicting; the path needs review.");
                    changed = true;
                }
            }
        }

        var states = new Dictionary<string, PreferenceFactState>(directStates, StringComparer.OrdinalIgnoreCase);
        foreach (var state in directStates.Where(item => item.Value == PreferenceFactState.Present && !blocked.Contains(item.Key)))
        {
            var pending = new Queue<string>([state.Key]);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { state.Key };
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                foreach (var relationship in PreferenceTraitRegistry.Current.Relationships
                             .Where(relationship => PlaybackCapabilityFacts.IsCapabilityRelationship(relationship.Kind)
                                 && string.Equals(relationship.FromTraitId, current, StringComparison.OrdinalIgnoreCase)))
                {
                    var related = relationship.ToTraitId.Trim().ToLowerInvariant();
                    if (blocked.Contains(related)
                        || directStates.TryGetValue(related, out var directState)
                            && directState is PreferenceFactState.Absent or PreferenceFactState.Conflicting)
                    {
                        continue;
                    }

                    states[related] = PreferenceFactState.Present;
                    if (visited.Add(related))
                    {
                        pending.Enqueue(related);
                    }
                }
            }
        }

        foreach (var state in states.Where(item => item.Value is PreferenceFactState.Unknown or PreferenceFactState.Conflicting))
        {
            unknownCapabilities.Add($"{profile.Name}: {state.Key}");
        }

        var supported = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states.Where(item => item.Value == PreferenceFactState.Present && !blocked.Contains(item.Key)))
        {
            if (!PreferenceTraitRegistry.Current.TryResolve(state.Key, out var definition))
            {
                continue;
            }

            if (!supported.TryGetValue(definition.Dimension, out var traits))
            {
                traits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                supported[definition.Dimension] = traits;
            }

            traits.Add(definition.NormalizedId);
        }

        return supported;
    }

    private static bool HasBlockedRelatedTrait(
        string traitId,
        IReadOnlyDictionary<string, PreferenceFactState> directStates,
        IReadOnlySet<string> blocked)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { traitId };
        var pending = new Queue<string>([traitId]);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var relationship in PreferenceTraitRegistry.Current.Relationships
                         .Where(relationship => PlaybackCapabilityFacts.IsCapabilityRelationship(relationship.Kind)
                             && string.Equals(relationship.FromTraitId, current, StringComparison.OrdinalIgnoreCase)))
            {
                var related = relationship.ToTraitId.Trim().ToLowerInvariant();
                if (blocked.Contains(related)
                    || directStates.TryGetValue(related, out var state)
                        && state is PreferenceFactState.Absent or PreferenceFactState.Conflicting)
                {
                    return true;
                }

                if (visited.Add(related))
                {
                    pending.Enqueue(related);
                }
            }
        }

        return false;
    }

    private static PreferenceFamily? BuildPreferenceFamily(
        IReadOnlyList<string> preferred,
        string? stopWhen,
        ICollection<string> warnings)
    {
        if (preferred.Count == 0)
        {
            if (stopWhen is not null)
            {
                warnings.Add("Stop-when was supplied without a preferred trait; it was not activated.");
            }

            return null;
        }

        var levels = preferred
            .Select((trait, index) => new PreferenceFamilyLevel($"level-{index + 1}", index, [trait]))
            .ToArray();
        var stopLevel = stopWhen is null
            ? null
            : levels.FirstOrDefault(level => level.TraitIds.Contains(stopWhen, StringComparer.OrdinalIgnoreCase));
        if (stopWhen is not null && stopLevel is null)
        {
            warnings.Add($"Stop-when trait '{stopWhen}' is not in the preferred list; the preference remains tie-break only.");
        }

        var upgradeDriving = stopLevel is not null;
        return new PreferenceFamily(
            "playback.preference",
            "Playback preference",
            1,
            upgradeDriving ? PreferenceIntent.Ranked : PreferenceIntent.TieBreak,
            levels,
            stopLevel?.Id,
            upgradeDriving,
            Transient: false);
    }

    private static List<string> ResolveTraits(
        IReadOnlyList<string>? source,
        string mediaType,
        string label,
        ICollection<string> warnings)
        => (source ?? [])
            .Select(value => ResolveTrait(value, mediaType, label, warnings))
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<IReadOnlyList<string>> ResolveGroups(
        IReadOnlyList<IReadOnlyList<string>>? source,
        string mediaType,
        string label,
        ICollection<string> warnings)
        => (source ?? [])
            .Select(group => (IReadOnlyList<string>)ResolveTraits(group, mediaType, label, warnings))
            .Where(group => group.Count > 0)
            .ToList();

    private static string? ResolveTrait(
        string? value,
        string mediaType,
        string label,
        ICollection<string> warnings)
    {
        if (!PreferenceTraitRegistry.Current.TryResolve(value, out var definition))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                warnings.Add($"Unknown {label} trait '{value}'. It remains review-only and was not compiled into the runtime plan.");
            }

            return null;
        }

        var allowed = definition.NormalizedMediaTypes;
        if (!allowed.Contains("both", StringComparer.Ordinal)
            && !allowed.Contains(mediaType, StringComparer.Ordinal))
        {
            warnings.Add($"Trait '{definition.Id}' is not applicable to {mediaType}.");
            return null;
        }

        return definition.Id.Trim().ToLowerInvariant();
    }
}
