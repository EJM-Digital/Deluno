using Deluno.Quality.ReleasePreferences;

namespace Deluno.Quality.Playback;

/// <summary>
/// Reads the owner-supplied capability assertions used by both playback
/// validation and plan compilation. A specific capability can prove a related
/// broader capability, but an explicitly absent companion makes that specific
/// assertion contradictory rather than silently playable.
/// </summary>
internal static class PlaybackCapabilityFacts
{
    private static readonly PreferenceRelationshipKind[] CapabilityRelationships =
    [
        PreferenceRelationshipKind.Implies,
        PreferenceRelationshipKind.Subsumes,
        PreferenceRelationshipKind.CoreOf,
        PreferenceRelationshipKind.CarriedBy
    ];

    public static Dictionary<string, PreferenceFactState> ReadDirectStates(
        PlaybackDeviceProfile profile,
        string mediaType,
        ICollection<string>? warnings = null)
    {
        var states = new Dictionary<string, PreferenceFactState>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in profile.Capabilities ?? [])
        {
            if (!PreferenceTraitRegistry.Current.TryResolve(capability.TraitId, out var definition))
            {
                if (!string.IsNullOrWhiteSpace(capability.TraitId))
                {
                    warnings?.Add($"Unknown device capability '{capability.TraitId}'.");
                }

                continue;
            }

            if (!definition.NormalizedMediaTypes.Contains("both", StringComparer.Ordinal)
                && !definition.NormalizedMediaTypes.Contains(mediaType, StringComparer.Ordinal))
            {
                warnings?.Add($"Device capability '{definition.Id}' is not applicable to {mediaType}.");
                continue;
            }

            var traitId = definition.NormalizedId;
            var state = PlaybackCapabilityStates.Parse(capability.State);
            states[traitId] = states.TryGetValue(traitId, out var existing)
                ? Merge(existing, state)
                : state;
        }

        return states;
    }

    public static bool IsExplicitlyBlocked(
        PlaybackDeviceProfile profile,
        string mediaType,
        string traitId)
    {
        var canonical = PreferenceTraitRegistry.Current.Canonicalize(traitId);
        if (canonical is null)
        {
            return false;
        }

        var states = ReadDirectStates(profile, mediaType);
        if (states.TryGetValue(canonical, out var direct)
            && direct is PreferenceFactState.Absent or PreferenceFactState.Conflicting)
        {
            return true;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { canonical };
        var pending = new Queue<string>([canonical]);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var relationship in PreferenceTraitRegistry.Current.Relationships
                         .Where(relationship => CapabilityRelationships.Contains(relationship.Kind)
                             && string.Equals(relationship.FromTraitId, current, StringComparison.OrdinalIgnoreCase)))
            {
                var related = relationship.ToTraitId.Trim().ToLowerInvariant();
                if (states.TryGetValue(related, out var relatedState)
                    && relatedState is PreferenceFactState.Absent or PreferenceFactState.Conflicting)
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

    public static bool IsCapabilityRelationship(PreferenceRelationshipKind kind)
        => CapabilityRelationships.Contains(kind);

    private static PreferenceFactState Merge(
        PreferenceFactState existing,
        PreferenceFactState incoming)
    {
        if (existing == incoming)
        {
            return existing;
        }

        if (existing == PreferenceFactState.Conflicting || incoming == PreferenceFactState.Conflicting)
        {
            return PreferenceFactState.Conflicting;
        }

        // Unknown is an absence of proof, not a contrary assertion. A
        // present/absent assertion can therefore resolve it, while present
        // and absent together are a genuine conflict.
        if (existing == PreferenceFactState.Unknown)
        {
            return incoming;
        }

        if (incoming == PreferenceFactState.Unknown)
        {
            return existing;
        }

        return PreferenceFactState.Conflicting;
    }
}
