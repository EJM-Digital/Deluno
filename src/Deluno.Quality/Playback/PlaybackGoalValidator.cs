using Deluno.Quality.ReleasePreferences;

namespace Deluno.Quality.Playback;

/// <summary>
/// Validates contradictions that can be identified before a playback goal is
/// saved. Unknown capability evidence is deliberately not an error: it is
/// surfaced by <see cref="PlaybackGoalCompiler"/> as review-only evidence.
/// </summary>
public static class PlaybackGoalValidator
{
    public static IReadOnlyList<string> Validate(
        PlaybackGoalItem goal,
        PlaybackDeviceGroup? group,
        IReadOnlyList<PlaybackDeviceProfile>? profiles)
    {
        ArgumentNullException.ThrowIfNull(goal);

        var errors = new List<string>();
        var required = Resolve(goal.RequiredTraitIds);
        var forbidden = Resolve(goal.EffectiveForbiddenTraitIds);
        var preferred = Resolve(goal.PreferredTraitIds);
        var stopWhen = ResolveOne(goal.StopWhenTraitId);

        AddDuplicateErrors("required", goal.RequiredTraitIds, errors);
        AddDuplicateErrors("forbidden", goal.EffectiveForbiddenTraitIds, errors);
        AddDuplicateErrors("preferred", goal.PreferredTraitIds, errors);

        var requiredForbidden = required.Intersect(forbidden, StringComparer.OrdinalIgnoreCase).ToArray();
        if (requiredForbidden.Length > 0)
        {
            errors.Add($"A playback goal cannot both require and forbid: {string.Join(", ", requiredForbidden)}.");
        }

        var preferredForbidden = preferred.Intersect(forbidden, StringComparer.OrdinalIgnoreCase).ToArray();
        if (preferredForbidden.Length > 0)
        {
            errors.Add($"A playback goal cannot prefer a trait it forbids: {string.Join(", ", preferredForbidden)}.");
        }

        if (stopWhen is not null && !preferred.Contains(stopWhen, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"The stop-when trait '{stopWhen}' must be one of the preferred traits.");
        }

        if (stopWhen is not null && forbidden.Contains(stopWhen, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"The stop-when trait '{stopWhen}' cannot be forbidden.");
        }

        var requiredAny = (goal.RequiredAnyTraitGroups ?? [])
            .Select(Resolve)
            .ToArray();
        for (var index = 0; index < requiredAny.Length; index++)
        {
            var alternatives = requiredAny[index];
            if (alternatives.Count == 0)
            {
                errors.Add($"Required-any group {index + 1} must contain at least one trait.");
                continue;
            }

            var forbiddenAlternatives = alternatives
                .Where(trait => forbidden.Contains(trait, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (forbiddenAlternatives.Length == alternatives.Count)
            {
                errors.Add($"Required-any group {index + 1} has no viable alternative because every trait is forbidden.");
            }
        }

        AddIncompatibleRelationshipErrors(required, "required", errors);
        for (var index = 0; index < requiredAny.Length; index++)
        {
            AddIncompatibleRelationshipErrors(
                requiredAny[index],
                $"required-any group {index + 1}",
                errors);
        }

        if (goal.MustPlay)
        {
            AddDeviceCapabilityErrors(
                goal,
                group,
                profiles ?? [],
                PreferenceTraitRegistry.NormalizeMediaType(goal.MediaType),
                required.ToHashSet(StringComparer.OrdinalIgnoreCase),
                requiredAny,
                errors);
        }

        return errors
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddDeviceCapabilityErrors(
        PlaybackGoalItem goal,
        PlaybackDeviceGroup? group,
        IReadOnlyList<PlaybackDeviceProfile> profiles,
        string mediaType,
        IReadOnlySet<string> required,
        IReadOnlyList<IReadOnlyList<string>> requiredAny,
        ICollection<string> errors)
    {
        if (group is null)
        {
            return;
        }

        var byId = profiles
            .Where(profile => profile.IsEnabled && !string.IsNullOrWhiteSpace(profile.Id))
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.First(), StringComparer.OrdinalIgnoreCase);
        var selected = (group.DeviceProfileIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => byId.TryGetValue(id.Trim(), out var profile) ? profile : null)
            .Where(profile => profile is not null)
            .Select(profile => profile!)
            .ToArray();

        if (selected.Length == 0)
        {
            errors.Add("A Must play goal needs at least one enabled device in its selected group.");
            return;
        }

        var mode = PlaybackGoalModes.Normalize(group.Mode);
        foreach (var trait in required)
        {
            var absentOn = selected.Where(profile => IsExplicitlyBlocked(profile, mediaType, trait)).ToArray();
            var impossible = mode == PlaybackGoalModes.EveryDevice
                ? absentOn.Length > 0
                : absentOn.Length == selected.Length;
            if (impossible)
            {
                errors.Add(mode == PlaybackGoalModes.EveryDevice
                    ? $"Required trait '{trait}' is explicitly absent on: {string.Join(", ", absentOn.Select(profile => profile.Name))}."
                    : $"Required trait '{trait}' is explicitly absent on every enabled device in the fallback set.");
            }
        }

        foreach (var (alternatives, index) in requiredAny.Select((value, index) => (value, index)))
        {
            var noDeviceCanSatisfy = selected.All(profile =>
                alternatives.All(trait => IsExplicitlyBlocked(profile, mediaType, trait)));
            if (noDeviceCanSatisfy)
            {
                errors.Add($"Required-any group {index + 1} is explicitly absent on every enabled device in the selected group.");
            }
            else if (mode == PlaybackGoalModes.EveryDevice)
            {
                var blocked = selected
                    .Where(profile => alternatives.All(trait => IsExplicitlyBlocked(profile, mediaType, trait)))
                    .Select(profile => profile.Name)
                    .ToArray();
                if (blocked.Length > 0)
                {
                    errors.Add($"Required-any group {index + 1} has no explicit viable alternative on: {string.Join(", ", blocked)}.");
                }
            }
        }
    }

    private static bool IsExplicitlyBlocked(
        PlaybackDeviceProfile profile,
        string mediaType,
        string traitId)
        => PlaybackCapabilityFacts.IsExplicitlyBlocked(profile, mediaType, traitId);

    private static void AddIncompatibleRelationshipErrors(
        IReadOnlyList<string> traits,
        string label,
        ICollection<string> errors)
    {
        var set = traits.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in PreferenceTraitRegistry.Current.Relationships
                     .Where(relationship => relationship.Kind == PreferenceRelationshipKind.Incompatible
                         && set.Contains(relationship.FromTraitId)
                         && set.Contains(relationship.ToTraitId)))
        {
            errors.Add($"The {label} contains incompatible traits '{relationship.FromTraitId}' and '{relationship.ToTraitId}'.");
        }
    }

    private static IReadOnlyList<string> Resolve(IEnumerable<string>? values)
        => (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => PreferenceTraitRegistry.Current.TryResolve(value, out var definition)
                ? definition.Id.Trim().ToLowerInvariant()
                : value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? ResolveOne(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : Resolve([value]).FirstOrDefault();

    private static void AddDuplicateErrors(
        string label,
        IEnumerable<string>? values,
        ICollection<string> errors)
    {
        var duplicates = (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => PreferenceTraitRegistry.Current.TryResolve(value, out var definition)
                ? definition.Id.Trim().ToLowerInvariant()
                : value.Trim().ToLowerInvariant())
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            errors.Add($"The {label} list contains duplicate trait(s): {string.Join(", ", duplicates)}.");
        }
    }
}
