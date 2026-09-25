namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// Normalises the evidence Deluno can safely derive from a release/file name
/// into the shared preference vocabulary. Unknown open-world facts are omitted
/// deliberately; the evaluator then retains them as unknown rather than
/// inventing a negative match.
/// </summary>
public static class ReleasePreferenceFactFactory
{
    /// <summary>
    /// Adds transient acquisition facts to an already-normalized release
    /// evaluation. Signals are represented by explicit plan traits and
    /// closed-world buckets; the raw count never becomes a public score or a
    /// weighted decision value.
    /// </summary>
    /// <param name="isSeasonPack">
    /// Whether the release name is a whole-season pack, when the search is
    /// for TV (#646). Null when the signal was not computed, such as for a
    /// movie search or a plan with no <c>transient.season-pack</c> family.
    /// </param>
    public static IReadOnlyList<PreferenceFact> WithTransientSignals(
        ReleasePreferencePlan plan,
        IEnumerable<PreferenceFact>? facts,
        int? seeders = null,
        bool? isSeasonPack = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var result = (facts ?? []).ToList();
        ApplyClosedWorldBucket(
            plan,
            result,
            "transient.seeders",
            seeders is null ? null : seeders.Value > 0 ? "transient.seeders.available" : "transient.seeders.none",
            "transient-seeder-bucket",
            present => present
                ? "The indexer reported the release as having seeder availability."
                : "The closed-world seeder bucket did not match the reported value.");
        ApplyClosedWorldBucket(
            plan,
            result,
            "transient.season-pack",
            isSeasonPack is null ? null : isSeasonPack.Value ? "transient.season-pack.whole" : "transient.season-pack.episode",
            "transient-season-pack-bucket",
            present => present
                ? "The release name matches a whole-season pack."
                : "The release name does not match a whole-season pack.");
        return result;
    }

    /// <summary>
    /// One closed-world transient family: present when <paramref name="selectedTraitId"/>
    /// names one of the family's own trait ids, absent for the family's other
    /// levels. A no-op when the plan has no family with this id (a media type
    /// the signal does not apply to) or the signal was not computed.
    /// </summary>
    private static void ApplyClosedWorldBucket(
        ReleasePreferencePlan plan,
        List<PreferenceFact> facts,
        string familyId,
        string? selectedTraitId,
        string detectionRule,
        Func<bool, string> detail)
    {
        if (selectedTraitId is null)
        {
            return;
        }

        var family = plan.Families?.FirstOrDefault(item =>
            string.Equals(item.Id, familyId, StringComparison.OrdinalIgnoreCase));
        if (family is null)
        {
            return;
        }

        var familyTraits = family.Levels
            .SelectMany(level => level.NormalizedTraitIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (facts.Any(fact => familyTraits.Contains(fact.NormalizedTraitId)))
        {
            // An owner/probe assertion wins over a derived indexer signal;
            // never introduce a second state that would manufacture a
            // conflict merely because a caller supplied richer evidence.
            return;
        }

        foreach (var traitId in familyTraits.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            var present = string.Equals(traitId, selectedTraitId, StringComparison.OrdinalIgnoreCase);
            facts.Add(new PreferenceFact(
                traitId,
                present ? PreferenceFactState.Present : PreferenceFactState.Absent,
                new PreferenceEvidence(
                    Source: "indexer-metadata",
                    Confidence: 1,
                    Detail: detail(present),
                    DetectionRule: detectionRule,
                    DetectionVersion: "v1",
                    Model: PreferenceEvidenceModel.ClosedWorld)));
        }
    }

    public static IReadOnlyList<PreferenceFact> FromReleaseName(
        ReleasePreferencePlan plan,
        string? releaseName,
        string? quality = null,
        string evidenceSource = "release-title")
    {
        ArgumentNullException.ThrowIfNull(plan);
        var facts = new List<PreferenceFact>();
        var normalizedQuality = MediaPolicyCatalog.Current.NormalizeQuality(quality)
            ?? MediaPolicyCatalog.Current.DetectQuality(releaseName);
        if (!string.IsNullOrWhiteSpace(normalizedQuality))
        {
            var qualityTrait = InstalledPreferenceEvaluationFactory.QualityTraitId(normalizedQuality);
            Add(facts, qualityTrait, PreferenceFactState.Present, evidenceSource, "quality", PreferenceEvidenceModel.ClosedWorld);
            var qualityFamily = plan.Families.FirstOrDefault(family => family.Id.Equals("quality", StringComparison.OrdinalIgnoreCase));
            foreach (var other in qualityFamily?.Levels.SelectMany(level => level.NormalizedTraitIds) ?? [])
            {
                if (!other.Equals(qualityTrait, StringComparison.OrdinalIgnoreCase))
                {
                    Add(facts, other, PreferenceFactState.Absent, evidenceSource, "quality", PreferenceEvidenceModel.ClosedWorld);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(releaseName))
        {
            return facts;
        }

        var parsed = MediaFileNameFacts.Parse(releaseName);
        AddObserved(facts, "video.codec", parsed.VideoCodec, evidenceSource);
        AddObserved(facts, "audio.channels", parsed.AudioChannels, evidenceSource);
        AddObserved(facts, "source", parsed.Source, evidenceSource);

        var lower = releaseName.ToLowerInvariant();
        if (lower.Contains("truehd", StringComparison.Ordinal) && lower.Contains("atmos", StringComparison.Ordinal))
        {
            AddObserved(facts, "audio.format", "TrueHD Atmos", evidenceSource);
        }
        else
        {
            AddObserved(facts, "audio.format", parsed.AudioCodec, evidenceSource);
        }

        if (lower.Contains("atmos", StringComparison.Ordinal))
        {
            AddObserved(facts, "audio.object", "Atmos", evidenceSource);
        }

        AddObserved(facts, "video.dynamic-range", DetectDynamicRange(lower), evidenceSource);
        AddObserved(facts, "video.resolution", DetectResolution(lower), evidenceSource);
        AddObserved(facts, "video.bit-depth", DetectBitDepth(lower), evidenceSource);
        AddObserved(facts, "release.revision", DetectRevision(lower), evidenceSource);
        AddObserved(facts, "edition", DetectEdition(lower), evidenceSource);
        AddObserved(facts, "streaming.service", DetectService(lower), evidenceSource);
        AddObserved(facts, "unwanted", DetectUnwanted(lower), evidenceSource);
        AddPlanMatcherFacts(facts, plan, releaseName);

        return facts;
    }

    private static void AddPlanMatcherFacts(
        ICollection<PreferenceFact> facts,
        ReleasePreferencePlan plan,
        string releaseName)
    {
        foreach (var result in ReleasePreferenceFormatEvidenceFactory.Evaluate(plan, releaseName))
        {
            foreach (var (traitId, state) in result.TraitIds
                         .Select(traitId => (traitId, PreferenceFactState.Present))
                         .Concat((result.AbsentTraitIds ?? [])
                             .Select(traitId => (traitId, PreferenceFactState.Absent))))
            {
                var dynamicTrait = CustomFormatPreferenceTraits.IsDynamic(traitId);
                facts.Add(new PreferenceFact(
                    traitId,
                    state,
                    new PreferenceEvidence(
                        Source: dynamicTrait ? "custom-format-matcher" : "guide-custom-format",
                        Confidence: 1,
                        Detail: result.IsMatch
                            ? $"Matched rule '{result.RuleName ?? result.SourceId}'."
                            : $"Did not match rule '{result.RuleName ?? result.SourceId}'.",
                        DetectionRule: dynamicTrait
                            ? "immutable-custom-format-matcher"
                            : "guide-custom-format-pattern",
                        DetectionVersion: $"immutable-plan:{plan.Version}:{plan.PlanHash}",
                        Model: dynamicTrait
                            ? PreferenceEvidenceModel.ClosedWorld
                            : PreferenceEvidenceModel.OpenWorld)));
            }
        }
    }

    private static void AddObserved(
        ICollection<PreferenceFact> facts,
        string dimension,
        string? value,
        string source)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !PreferenceTraitRegistry.Current.TryResolveObserved(dimension, value, out var definition))
        {
            return;
        }

        Add(facts, definition.Id, PreferenceFactState.Present, source, dimension, PreferenceEvidenceModel.OpenWorld);
    }

    private static void Add(
        ICollection<PreferenceFact> facts,
        string traitId,
        PreferenceFactState state,
        string source,
        string detail,
        PreferenceEvidenceModel model)
    {
        facts.Add(new PreferenceFact(
            traitId,
            state,
            new PreferenceEvidence(
                Source: source,
                Confidence: model == PreferenceEvidenceModel.ClosedWorld ? 1 : 0.8,
                Detail: $"Observed {detail}.",
                DetectionRule: "release-preference-fact-factory",
                DetectionVersion: "v1",
                Model: model)));
    }

    private static string? DetectDynamicRange(string value)
    {
        var dolbyVision = value.Contains("dolby.vision", StringComparison.Ordinal)
            || value.Contains("dolby vision", StringComparison.Ordinal)
            || value.Contains("dovi", StringComparison.Ordinal)
            || value.Contains(".dv.", StringComparison.Ordinal)
            || value.Contains(" dv ", StringComparison.Ordinal);
        var hdr10 = value.Contains("hdr10", StringComparison.Ordinal);
        if (dolbyVision && hdr10) return "Dolby Vision with HDR10 fallback";
        if (dolbyVision) return "Dolby Vision";
        if (value.Contains("hdr10+", StringComparison.Ordinal) || value.Contains("hdr10plus", StringComparison.Ordinal)) return "HDR10+";
        if (value.Contains("hdr10", StringComparison.Ordinal)) return "HDR10";
        if (value.Contains("hlg", StringComparison.Ordinal)) return "HLG";
        if (value.Contains("sdr", StringComparison.Ordinal)) return "SDR";
        return null;
    }

    private static string? DetectResolution(string value)
        => value.Contains("2160p", StringComparison.Ordinal) || value.Contains("4k", StringComparison.Ordinal)
            ? "2160p"
            : value.Contains("1080p", StringComparison.Ordinal)
                ? "1080p"
                : value.Contains("720p", StringComparison.Ordinal)
                    ? "720p"
                    : value.Contains("576p", StringComparison.Ordinal)
                        ? "576p"
                        : value.Contains("480p", StringComparison.Ordinal) ? "480p" : null;

    private static string? DetectBitDepth(string value)
        => value.Contains("12bit", StringComparison.Ordinal) || value.Contains("12-bit", StringComparison.Ordinal)
            ? "12bit"
            : value.Contains("10bit", StringComparison.Ordinal) || value.Contains("10-bit", StringComparison.Ordinal)
                ? "10bit"
                : value.Contains("8bit", StringComparison.Ordinal) || value.Contains("8-bit", StringComparison.Ordinal)
                    ? "8bit"
                    : null;

    private static string? DetectRevision(string value)
        => value.Contains("repack3", StringComparison.Ordinal) || value.Contains("repack.3", StringComparison.Ordinal)
            ? "Repack 3"
            : value.Contains("repack2", StringComparison.Ordinal) || value.Contains("repack.2", StringComparison.Ordinal)
                ? "Repack 2"
                : value.Contains("repack", StringComparison.Ordinal) || value.Contains("proper", StringComparison.Ordinal)
                    ? "Proper"
                    : null;

    private static string? DetectEdition(string value)
        => value.Contains("director", StringComparison.Ordinal) && value.Contains("cut", StringComparison.Ordinal)
            ? "Director's cut"
            : value.Contains("extended", StringComparison.Ordinal)
                ? "Extended cut"
                : value.Contains("imax", StringComparison.Ordinal)
                    ? "IMAX"
                    : value.Contains("uncut", StringComparison.Ordinal) ? "Uncut" : null;

    private static string? DetectService(string value)
        => value.Contains("netflix", StringComparison.Ordinal) || HasReleaseToken(value, "nf")
            ? "Netflix"
            : value.Contains("amazon", StringComparison.Ordinal) || HasReleaseToken(value, "amzn")
                ? "Amazon Prime"
                : value.Contains("apple tv", StringComparison.Ordinal)
                  || value.Contains("apple.tv", StringComparison.Ordinal)
                  || HasReleaseToken(value, "atvp")
                    ? "Apple TV+"
                    : value.Contains("disney", StringComparison.Ordinal)
                      || HasReleaseToken(value, "dsnp", "dsny")
                        ? "Disney+"
                        : HasReleaseToken(value, "hmax", "max", "hbo")
                            ? "Max"
                            : value.Contains("hulu", StringComparison.Ordinal) ? "Hulu" : null;

    /// <summary>
    /// Matches the compact service tags used in real release names without
    /// treating the same letters inside a film title or release group as a
    /// service. The aliases mirror the reviewed guide package patterns.
    /// </summary>
    private static bool HasReleaseToken(string value, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            var start = 0;
            while (start < value.Length)
            {
                var index = value.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                var leftBoundary = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
                var end = index + token.Length;
                var rightBoundary = end == value.Length || !char.IsLetterOrDigit(value[end]);
                if (leftBoundary && rightBoundary)
                {
                    return true;
                }

                start = index + 1;
            }
        }

        return false;
    }

    private static string? DetectUnwanted(string value)
        => value.Contains("upscaled", StringComparison.Ordinal)
            ? "Upscaled"
            : value.Contains("camrip", StringComparison.Ordinal) || value.Contains("telesync", StringComparison.Ordinal)
                ? value.Contains("telesync", StringComparison.Ordinal) ? "Telesync" : "CAM"
                : value.Contains("screener", StringComparison.Ordinal) ? "Screener"
                : value.Contains("sample", StringComparison.Ordinal) ? "Sample"
                : null;
}
