namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// Turns the trait ids a plan gates on into the words the person recognises:
/// a custom format by the name they gave it, a built-in trait by its plain
/// label. An id is never shown; a trait whose name cannot be found reads as a
/// plain description of what is missing.
/// </summary>
internal sealed class PreferenceTraitWords
{
    public const string MissingCustomFormat = "a custom format that no longer exists";
    public const string MissingSetting = "a setting this profile uses that Deluno no longer has";

    private readonly ReleasePreferencePlan plan;

    public PreferenceTraitWords(ReleasePreferencePlan plan) => this.plan = plan;

    /// <summary>
    /// A noun phrase for the trait, and whether the trait is the "does not
    /// match" half of a custom format (so a rule on it reads the other way round).
    /// </summary>
    public (string Name, bool Negated) Describe(string traitId)
    {
        var id = (traitId ?? string.Empty).Trim().ToLowerInvariant();
        if (CustomFormatPreferenceTraits.IsDynamic(id))
        {
            var negated = id.EndsWith(".not-matched", StringComparison.Ordinal);
            var name = CustomFormatName(id);
            return (name is null ? MissingCustomFormat : $"your custom format \"{name}\"", negated);
        }

        if (PreferenceTraitRegistry.Current.TryResolve(id, out var trait) && !string.IsNullOrWhiteSpace(trait.DisplayName))
        {
            var prefix = trait.Dimension.StartsWith("audio.language", StringComparison.OrdinalIgnoreCase) ? "the language " : string.Empty;
            return ($"{prefix}{trait.DisplayName.Trim()}", false);
        }

        return (MissingSetting, false);
    }

    private string? CustomFormatName(string traitId)
    {
        foreach (var source in plan.Sources ?? [])
        {
            if (!string.Equals(source.SourceKind?.Trim(), CustomFormatPreferenceTraits.SourceKind, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(source.RuleName))
            {
                continue;
            }

            var ruleId = string.IsNullOrWhiteSpace(source.RuleId) ? source.SourceId : source.RuleId;
            var mapped = (source.MappedTraitIds ?? []).Any(item => string.Equals(item?.Trim(), traitId, StringComparison.OrdinalIgnoreCase));
            var derived = !string.IsNullOrWhiteSpace(ruleId)
                && (string.Equals(CustomFormatPreferenceTraits.Matched(ruleId), traitId, StringComparison.Ordinal)
                    || string.Equals(CustomFormatPreferenceTraits.NotMatched(ruleId), traitId, StringComparison.Ordinal));
            if (mapped || derived)
            {
                return source.RuleName.Trim();
            }
        }

        return null;
    }

    private string Phrase(string traitId, out bool wantMatch, bool required)
    {
        var (name, negated) = Describe(traitId);
        // A required "not-matched" means the person wants it NOT to match.
        wantMatch = required ? !negated : negated;
        return name;
    }

    private static string Sentence(string name, bool wantMatch)
        => wantMatch
            ? $"It does not match {name}, which this profile requires."
            : $"It matches {name}, which this profile never takes.";

    /// <summary>A required trait that is proven absent.</summary>
    public string RequiredAbsent(string traitId)
    {
        var name = Phrase(traitId, out var wantMatch, required: true);
        return Sentence(name, wantMatch);
    }

    /// <summary>A required trait whose evidence is unknown or conflicting.</summary>
    public string RequiredUnknown(string traitId)
    {
        var name = Phrase(traitId, out var wantMatch, required: true);
        return $"We cannot tell whether it matches {name}, which this profile {(wantMatch ? "requires" : "never takes")}.";
    }

    /// <summary>A forbidden trait that is proven present.</summary>
    public string ForbiddenPresent(string traitId)
    {
        var name = Phrase(traitId, out var wantMatch, required: false);
        return Sentence(name, wantMatch);
    }

    /// <summary>A forbidden trait whose evidence is unknown.</summary>
    public string ForbiddenUnknown(string traitId)
    {
        var name = Phrase(traitId, out var wantMatch, required: false);
        return $"We cannot tell whether it matches {name}, which this profile {(wantMatch ? "requires" : "never takes")}.";
    }

    public string GroupList(IEnumerable<string> traitIds)
        => string.Join(", ", traitIds
            .Select(id =>
            {
                var (name, negated) = Describe(id);
                return negated ? $"not {name}" : name;
            })
            .Distinct(StringComparer.Ordinal));

    public string GroupMissing(IEnumerable<string> traitIds)
        => $"It matches none of {GroupList(traitIds)}, and this profile needs at least one.";

    public string GroupConflicting(IEnumerable<string> traitIds)
        => $"The evidence conflicts on whether it matches any of {GroupList(traitIds)}, and this profile needs at least one.";

    public string GroupIncomplete(IEnumerable<string> traitIds)
        => $"We cannot yet tell whether it matches any of {GroupList(traitIds)}, and this profile needs at least one.";
}
