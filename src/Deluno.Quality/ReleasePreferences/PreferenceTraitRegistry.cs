using System.Text.Json.Serialization;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// A selectable, stable media characteristic. The id is the persisted/API
/// identity; display text and detection aliases are deliberately separate so
/// a renamed label cannot change an existing plan.
/// </summary>
public sealed record PreferenceTraitDefinition(
    string Id,
    string Dimension,
    string DisplayName,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? MediaTypes = null,
    string? GuideSource = null,
    string? GuideVersion = null,
    bool Transient = false)
{
    [JsonIgnore]
    public string NormalizedId => Id.Trim().ToLowerInvariant();

    [JsonIgnore]
    public IReadOnlyList<string> NormalizedAliases
        => (Aliases ?? [])
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(alias => alias, StringComparer.Ordinal)
            .ToArray();

    [JsonIgnore]
    public IReadOnlyList<string> NormalizedMediaTypes
        => (MediaTypes ?? ["both"])
            .Select(PreferenceTraitRegistry.NormalizeMediaType)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(mediaType => mediaType, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// The finite vocabulary available to typed release-preference plans.
///
/// <para>Facts observed by a detector may be richer than this list, but a
/// plan may only make decisions about registered traits. That distinction lets
/// Deluno retain unknown evidence without silently turning a new token into a
/// new upgrade rule.</para>
/// </summary>
public sealed class PreferenceTraitRegistry
{
    public const string CurrentVersion = "typed-registry/v1";

    private readonly IReadOnlyDictionary<string, PreferenceTraitDefinition> byId;
    private readonly IReadOnlyDictionary<string, PreferenceTraitDefinition> byAlias;

    public PreferenceTraitRegistry(
        IReadOnlyList<PreferenceTraitDefinition> traits,
        IReadOnlyList<PreferenceRelationship>? relationships = null,
        string version = CurrentVersion)
    {
        Traits = traits ?? throw new ArgumentNullException(nameof(traits));
        Relationships = relationships ?? [];
        Version = string.IsNullOrWhiteSpace(version) ? CurrentVersion : version.Trim();

        byId = traits
            .Where(trait => !string.IsNullOrWhiteSpace(trait.Id))
            .GroupBy(trait => trait.NormalizedId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var aliases = new Dictionary<string, PreferenceTraitDefinition>(StringComparer.Ordinal);
        foreach (var trait in traits.Where(trait => !string.IsNullOrWhiteSpace(trait.Id)))
        {
            aliases[trait.NormalizedId] = trait;
            foreach (var alias in trait.NormalizedAliases)
            {
                if (!aliases.ContainsKey(alias))
                {
                    aliases[alias] = trait;
                }
            }
        }

        byAlias = aliases;
    }

    public string Version { get; }

    public IReadOnlyList<PreferenceTraitDefinition> Traits { get; }

    public IReadOnlyList<PreferenceRelationship> Relationships { get; }

    public static PreferenceTraitRegistry Current { get; } = BuildCurrent();

    public bool TryResolve(string? idOrAlias, out PreferenceTraitDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(idOrAlias) && byAlias.TryGetValue(idOrAlias.Trim().ToLowerInvariant(), out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    public bool IsKnown(string? idOrAlias) => TryResolve(idOrAlias, out _);

    /// <summary>
    /// Returns the stable id for a registered trait or alias. Callers that
    /// persist provenance may retain an unknown value as review evidence, but
    /// a known alias must never become part of a new plan's effective shape.
    /// </summary>
    public string? Canonicalize(string? idOrAlias)
        => TryResolve(idOrAlias, out var definition)
            ? definition.NormalizedId
            : null;

    /// <summary>Canonicalizes a possibly aliased list while retaining unknown values for review.</summary>
    public IReadOnlyList<string> CanonicalizeIds(IEnumerable<string>? values)
        => (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Canonicalize(value) ?? value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Resolves a value emitted by a detector to the canonical id in one
    /// dimension. Detector labels are intentionally allowed to be aliases,
    /// while persisted facts use the registry id so a renamed display label
    /// cannot fork the vocabulary used by plans.
    /// </summary>
    public bool TryResolveObserved(
        string dimension,
        string? value,
        out PreferenceTraitDefinition definition)
    {
        var normalizedDimension = Normalize(dimension);
        var normalizedValue = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalizedDimension)
            || string.IsNullOrWhiteSpace(normalizedValue))
        {
            definition = null!;
            return false;
        }

        foreach (var trait in Traits.Where(trait =>
                     string.Equals(Normalize(trait.Dimension), normalizedDimension, StringComparison.Ordinal)))
        {
            if (trait.NormalizedAliases.Contains(normalizedValue, StringComparer.Ordinal)
                || string.Equals(Slug(trait.DisplayName), Slug(value!), StringComparison.Ordinal)
                || string.Equals(
                    trait.NormalizedId,
                    $"{normalizedDimension}.{Slug(value!)}",
                    StringComparison.Ordinal))
            {
                definition = trait;
                return true;
            }
        }

        definition = null!;
        return false;
    }

    /// <summary>
    /// Validates the registry itself. This is run by tests and can be run by a
    /// build/update pipeline before a guide package is accepted.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var trait in Traits)
        {
            var id = trait.NormalizedId;
            if (string.IsNullOrWhiteSpace(id))
            {
                errors.Add("Every registered preference trait needs a stable id.");
                continue;
            }

            if (!ids.Add(id))
            {
                errors.Add($"Preference trait '{id}' is registered more than once.");
            }

            if (string.IsNullOrWhiteSpace(trait.Dimension))
                errors.Add($"Preference trait '{id}' needs a dimension.");
            if (string.IsNullOrWhiteSpace(trait.DisplayName))
                errors.Add($"Preference trait '{id}' needs a display name.");

            foreach (var mediaType in trait.NormalizedMediaTypes)
            {
                if (mediaType is not ("movies" or "tv" or "both"))
                    errors.Add($"Preference trait '{id}' has unknown media type '{mediaType}'.");
            }

            RegisterName(names, id, id, errors);
            foreach (var alias in trait.NormalizedAliases)
            {
                RegisterName(names, alias, id, errors);
            }
        }

        foreach (var relationship in Relationships)
        {
            var from = Normalize(relationship.FromTraitId);
            var to = Normalize(relationship.ToTraitId);
            if (!ids.Contains(from) || !ids.Contains(to))
            {
                errors.Add($"Registry relationship '{relationship.FromTraitId} -> {relationship.ToTraitId}' refers to an unknown trait.");
            }
        }

        var relationshipPairs = Relationships
            .GroupBy(item => $"{Normalize(item.FromTraitId)}|{Normalize(item.ToTraitId)}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Kind).ToHashSet(), StringComparer.Ordinal);
        foreach (var pair in relationshipPairs)
        {
            if (pair.Value.Contains(PreferenceRelationshipKind.Incompatible)
                && pair.Value.Any(kind => kind is PreferenceRelationshipKind.Implies
                    or PreferenceRelationshipKind.Requires
                    or PreferenceRelationshipKind.Subsumes
                    or PreferenceRelationshipKind.CoreOf
                    or PreferenceRelationshipKind.CarriedBy))
            {
                errors.Add($"Registry relationship pair '{pair.Key}' is both compatible-by-relationship and incompatible.");
            }
        }

        var graph = Relationships
            .Where(item => item.Kind is PreferenceRelationshipKind.Implies
                or PreferenceRelationshipKind.Requires
                or PreferenceRelationshipKind.Subsumes
                or PreferenceRelationshipKind.CoreOf
                or PreferenceRelationshipKind.CarriedBy)
            .GroupBy(item => Normalize(item.FromTraitId), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => Normalize(item.ToTraitId)).ToArray(),
                StringComparer.Ordinal);
        if (HasCycle(graph))
        {
            errors.Add("Registry implication, requirement, and subsumption relationships must be acyclic.");
        }

        return errors;
    }

    /// <summary>
    /// Validates a plan against the stable registry. Plans must persist
    /// canonical ids, while aliases remain useful for import/detection lookup.
    /// </summary>
    public IReadOnlyList<string> ValidatePlan(ReleasePreferencePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var errors = new List<string>();
        foreach (var family in plan.Families ?? [])
        {
            foreach (var traitId in (family.Levels ?? [])
                         .Where(level => level is not null)
                         .SelectMany(level => level.NormalizedTraitIds))
            {
                ValidatePlanTrait(traitId, family, plan.MediaType, errors);
            }
        }

        foreach (var traitId in (plan.RequiredTraitIds ?? []).Concat(plan.ForbiddenTraitIds ?? []))
        {
            ValidatePlanTrait(traitId, family: null, plan.MediaType, errors);
        }

        foreach (var traitId in (plan.RequiredAnyTraitGroups ?? [])
                     .Where(group => group is not null)
                     .SelectMany(group => group))
        {
            ValidatePlanTrait(traitId, family: null, plan.MediaType, errors);
        }

        foreach (var relationship in (plan.Relationships ?? []).Where(relationship => relationship is not null))
        {
            ValidatePlanTrait(relationship.FromTraitId, family: null, plan.MediaType, errors);
            ValidatePlanTrait(relationship.ToTraitId, family: null, plan.MediaType, errors);
        }

        return errors;
    }

    private void ValidatePlanTrait(
        string? traitId,
        PreferenceFamily? family,
        string? mediaType,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(traitId))
        {
            errors.Add("Plan traits must have a non-empty id.");
            return;
        }

        if (!TryResolve(traitId, out var definition))
        {
            errors.Add($"Plan trait '{traitId}' is not in the {Version} registry.");
            return;
        }

        if (!string.Equals(definition.Id.Trim(), traitId.Trim(), StringComparison.Ordinal))
        {
            errors.Add($"Plan trait '{traitId}' must use canonical registry id '{definition.Id}'.");
        }

        var normalizedMediaType = NormalizeMediaType(mediaType);
        if (!definition.NormalizedMediaTypes.Contains("both", StringComparer.Ordinal)
            && !definition.NormalizedMediaTypes.Contains(normalizedMediaType, StringComparer.Ordinal))
        {
            errors.Add($"Trait '{definition.Id}' is not applicable to media type '{normalizedMediaType}'.");
        }

        if (family is not null && family.Transient != definition.Transient)
        {
            errors.Add($"Trait '{definition.Id}' must be placed in a {(definition.Transient ? "transient" : "persistent")} family.");
        }
    }

    public static string NormalizeMediaType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() switch
        {
            "tv" or "series" or "shows" => "tv",
            "movie" or "movies" => "movies",
            _ => "both"
        };

    private static PreferenceTraitRegistry BuildCurrent()
    {
        var traits = new List<PreferenceTraitDefinition>();
        void Add(
            string id,
            string dimension,
            string displayName,
            string[]? aliases = null,
            string[]? mediaTypes = null,
            bool transient = false,
            string? guideSource = "TRaSH Guides",
            string? guideVersion = "bundled/v1")
            => traits.Add(new PreferenceTraitDefinition(
                id,
                dimension,
                displayName,
                aliases,
                mediaTypes ?? ["both"],
                guideSource,
                guideVersion,
                transient));

        foreach (var quality in MediaPolicyCatalog.Current.QualityRanks.Keys)
        {
            var slug = Slug(quality);
            Add($"quality.{slug}", "quality", quality, [quality]);
        }

        foreach (var source in new[] { "webdl", "webrip", "hdtv", "bluray", "remux", "dvd", "cam", "br-disk" })
        {
            Add($"source.{source}", "source", source switch
            {
                "webdl" => "WEB-DL",
                "webrip" => "WEBRip",
                "hdtv" => "HDTV",
                "bluray" => "Blu-ray",
                "remux" => "Remux",
                "br-disk" => "Blu-ray disc",
                _ => source.ToUpperInvariant()
            }, source switch
            {
                "webdl" => ["WEB", "WEB-DL", "WEB DL"],
                "webrip" => ["WEBRip", "WEB Rip"],
                "bluray" => ["Blu-ray", "Bluray", "BluRay", "BDRip", "BRRip"],
                "remux" => ["Remux", "BDRemux"],
                "hdtv" => ["HDTV"],
                // DVD, CAM and BR-Disk are also quality labels in the legacy
                // policy catalogue. Their canonical source ids are resolved
                // by the dimension-qualified id (source.dvd, etc.) rather
                // than adding ambiguous global aliases.
                _ => []
            });
        }

        foreach (var codec in new[] { ("h264", "H.264", "x264"), ("hevc", "HEVC", "x265"), ("av1", "AV1", "av1"), ("vp9", "VP9", "vp9"), ("xvid", "XviD", "xvid"), ("divx", "DivX", "divx"), ("mpeg-2", "MPEG-2", "mpeg2") })
        {
            Add($"video.codec.{codec.Item1}", "video.codec", codec.Item2, [codec.Item3, codec.Item2]);
        }

        foreach (var depth in new[] { ("8", "8-bit"), ("10", "10-bit"), ("12", "12-bit") })
            Add($"video.bit-depth.{depth.Item1}", "video.bit-depth", depth.Item2, [$"{depth.Item1}bit"]);
        foreach (var resolution in new[] { "480p", "576p", "720p", "1080p", "2160p" })
            Add($"video.resolution.{Slug(resolution)}", "video.resolution", resolution, [resolution]);

        foreach (var hdr in new[]
        {
            ("sdr", "SDR"), ("hdr10", "HDR10"), ("hdr10-plus", "HDR10+"),
            ("hlg", "HLG"), ("dolby-vision", "Dolby Vision"),
            ("dolby-vision-fallback", "Dolby Vision with HDR10 fallback"),
            ("no-hdr-fallback", "No HDR fallback")
        })
        {
            Add($"video.dynamic-range.{hdr.Item1}", "video.dynamic-range", hdr.Item2, [hdr.Item2]);
        }

        foreach (var audio in new[]
        {
            ("truehd-atmos", "TrueHD Atmos"), ("dtsx", "DTS:X"),
            ("truehd", "TrueHD"), ("dts-hd-ma", "DTS-HD MA"),
            ("eac3-atmos", "DD+ Atmos"), ("eac3", "DD+"),
            ("dts", "DTS"), ("aac", "AAC"), ("flac", "FLAC"),
            ("pcm", "PCM"), ("mp3", "MP3")
        })
        {
            Add($"audio.format.{audio.Item1}", "audio.format", audio.Item2, audio.Item1 switch
            {
                "dtsx" => [audio.Item2, "DTS-X"],
                "dts-hd-ma" => [audio.Item2, "DTS-HD"],
                "eac3" => [audio.Item2, "E-AC-3", "EAC3", "DDP"],
                "eac3-atmos" => [audio.Item2, "E-AC-3 Atmos", "DDP Atmos"],
                _ => [audio.Item2]
            });
        }

        Add("audio.format.ac3", "audio.format", "AC-3", ["AC-3", "AC3"]);
        Add("audio.format.opus", "audio.format", "Opus", ["Opus"]);
        Add("audio.object.atmos", "audio.object", "Atmos", ["Atmos"]);

        foreach (var channel in new[] { ("1-0", "Mono"), ("2-0", "Stereo"), ("5-1", "5.1"), ("7-1", "7.1"), ("9-1", "9.1") })
            Add($"audio.channels.{channel.Item1}", "audio.channels", channel.Item2, [channel.Item2, channel.Item2.Replace("-", ".", StringComparison.Ordinal)]);

        foreach (var language in new[] { ("original", "Original language"), ("multi", "Multilingual"), ("dubbed", "Dubbed"), ("en", "English"), ("ja", "Japanese"), ("de", "German"), ("fr", "French"), ("es", "Spanish") })
            Add($"audio.language.{language.Item1}", "audio.language", language.Item2, [language.Item2]);

        foreach (var subtitle in new[] { ("en", "English subtitles"), ("ja", "Japanese subtitles"), ("forced", "Forced subtitles"), ("sdh", "SDH subtitles"), ("hi", "Hearing-impaired subtitles") })
            Add($"subtitle.{subtitle.Item1}", "subtitle", subtitle.Item2, [subtitle.Item2]);

        foreach (var edition in new[] { ("imax", "IMAX"), ("extended", "Extended cut"), ("directors-cut", "Director's cut"), ("uncut", "Uncut") })
            Add($"edition.{edition.Item1}", "edition", edition.Item2, [edition.Item2]);

        foreach (var service in new[] { ("netflix", "Netflix"), ("amazon", "Amazon Prime"), ("apple", "Apple TV+"), ("disney", "Disney+"), ("max", "Max"), ("hulu", "Hulu") })
            Add($"service.{service.Item1}", "streaming.service", service.Item2, [service.Item2]);

        foreach (var group in new[] { ("trusted", "Trusted release group"), ("scene", "Scene release group"), ("anime", "Anime release group") })
            Add($"release-group.{group.Item1}", "release-group", group.Item2, [group.Item2]);
        Add("release-group.unclassified", "release-group", "Unclassified release group", ["Unclassified"]);

        foreach (var revision in new[] { ("proper", "Proper"), ("repack1", "Repack"), ("repack2", "Repack 2"), ("repack3", "Repack 3") })
            Add($"release.revision.{revision.Item1}", "release.revision", revision.Item2, [revision.Item2]);

        foreach (var unwanted in new[] { ("cam", "CAM"), ("telesync", "Telesync"), ("screener", "Screener"), ("sample", "Sample"), ("hardcoded-subtitles", "Hardcoded subtitles"), ("upscaled", "Upscaled video") })
            Add($"unwanted.{unwanted.Item1}", "unwanted", unwanted.Item2);

        foreach (var transient in new[] { ("seeders", "Seeders"), ("release-age", "Release age"), ("indexer-priority", "Indexer priority"), ("ml-confidence", "ML confidence") })
            Add($"transient.{transient.Item1}", "acquisition-confidence", transient.Item2, [transient.Item2], transient: true, guideSource: null, guideVersion: null);

        // Seeder counts are transient acquisition evidence, not a public
        // numeric preference. These explicit buckets let a plan say that an
        // available release is preferable to one with no peers without
        // smuggling a hidden `OrderBy(seedCount)` into typed selection.
        Add("transient.seeders.available", "acquisition-confidence", "Seeders available", transient: true, guideSource: null, guideVersion: null);
        Add("transient.seeders.none", "acquisition-confidence", "No seeders reported", transient: true, guideSource: null, guideVersion: null);

        var relationships = new[]
        {
            new PreferenceRelationship("audio.format.truehd-atmos", "audio.format.truehd", PreferenceRelationshipKind.Implies),
            new PreferenceRelationship("audio.format.dtsx", "audio.format.dts-hd-ma", PreferenceRelationshipKind.CoreOf),
            new PreferenceRelationship("audio.format.eac3-atmos", "audio.format.eac3", PreferenceRelationshipKind.Implies),
            new PreferenceRelationship("video.dynamic-range.dolby-vision-fallback", "video.dynamic-range.dolby-vision", PreferenceRelationshipKind.Implies),
            new PreferenceRelationship("video.dynamic-range.dolby-vision-fallback", "video.dynamic-range.hdr10", PreferenceRelationshipKind.CarriedBy),
            new PreferenceRelationship("release.revision.repack3", "release.revision.repack2", PreferenceRelationshipKind.Subsumes),
            new PreferenceRelationship("release.revision.repack2", "release.revision.proper", PreferenceRelationshipKind.Subsumes)
        };

        return new PreferenceTraitRegistry(traits, relationships);
    }

    private static void RegisterName(
        IDictionary<string, string> names,
        string name,
        string owner,
        ICollection<string> errors)
    {
        if (names.TryGetValue(name, out var existing) && !string.Equals(existing, owner, StringComparison.Ordinal))
        {
            errors.Add($"Preference alias '{name}' belongs to both '{existing}' and '{owner}'.");
        }
        else
        {
            names[name] = owner;
        }
    }

    private static bool HasCycle(IReadOnlyDictionary<string, string[]> graph)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Keys)
        {
            if (Visit(node)) return true;
        }

        return false;

        bool Visit(string node)
        {
            if (visiting.Contains(node)) return true;
            if (!visited.Add(node)) return false;
            visiting.Add(node);
            if (graph.TryGetValue(node, out var children) && children.Any(Visit)) return true;
            visiting.Remove(node);
            return false;
        }
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var slug = string.Join(string.Empty, chars)
            .Replace("--", "-", StringComparison.Ordinal)
            .Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "unknown" : slug;
    }
}
