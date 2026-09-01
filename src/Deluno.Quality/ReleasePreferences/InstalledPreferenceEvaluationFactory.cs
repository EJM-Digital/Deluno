using System.Security.Cryptography;
using System.Text;
using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// Builds the durable typed baseline for a file that has just entered a
/// library. The filename and the quality policy are the evidence available at
/// import time; facts that require a probe can be added by the later probe
/// pass without replacing this baseline.
/// </summary>
public static class InstalledPreferenceEvaluationFactory
{
    public static PreferenceEvaluationSnapshot? Create(
        QualityProfileItem profile,
        string mediaId,
        string libraryId,
        string filePath,
        long? fileSizeBytes,
        string? currentQuality,
        DateTimeOffset evaluatedUtc,
        string source,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        GuidePackage? guidePackage = null,
        ReleasePreferencePlan? preferencePlan = null,
        IReadOnlyList<PreferenceFact>? baselineFacts = null,
        string? probedVideoCodec = null,
        string? probedAudioCodec = null,
        string? probedAudioChannels = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var effectiveGuidePackage = guidePackage ?? GuidePackageCatalog.Current;
        var plan = preferencePlan ?? ReleasePreferencePlanFactory.CreateQualityPlan(profile, customFormats, effectiveGuidePackage);
        var hasBaseline = baselineFacts is { Count: > 0 };
        var facts = (baselineFacts ?? []).ToList();
        var normalizedQuality = MediaPolicyCatalog.Current.NormalizeQuality(currentQuality);
        if (!string.IsNullOrWhiteSpace(normalizedQuality))
        {
            // The wanted row can be repaired or re-imported without changing
            // the path. Replace only the quality family in that case; all
            // other durable evidence still describes the same installed file.
            RemoveDimensionFacts(facts, "quality");
            var selectedTraitId = QualityTraitId(normalizedQuality);
            facts.Add(new PreferenceFact(
                selectedTraitId,
                PreferenceFactState.Present,
                new PreferenceEvidence(
                    Source: "media-policy",
                    Confidence: 1,
                    Detail: $"Imported quality: {normalizedQuality}.",
                    DetectionRule: "media-policy",
                    DetectionVersion: MediaPolicyCatalog.Current.Version,
                    Model: PreferenceEvidenceModel.ClosedWorld)));

            // The policy classifier is closed-world for the quality family: a
            // normalized quality answer identifies the one selected tier and
            // rules out the other tiers. Recording those negatives prevents an
            // unrelated, better tier from making an otherwise known installed
            // file look "needs review" merely because it was not selected.
            foreach (var traitId in plan.Families
                         .Where(family => string.Equals(family.Id, "quality", StringComparison.OrdinalIgnoreCase))
                         .SelectMany(family => family.Levels)
                         .SelectMany(level => level.NormalizedTraitIds)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(traitId => !string.Equals(traitId, selectedTraitId, StringComparison.OrdinalIgnoreCase)))
            {
                facts.Add(new PreferenceFact(
                    traitId,
                    PreferenceFactState.Absent,
                    new PreferenceEvidence(
                        Source: "media-policy",
                        Confidence: 1,
                        Detail: "The closed-world quality classifier selected another tier.",
                        DetectionRule: "media-policy",
                        DetectionVersion: MediaPolicyCatalog.Current.Version,
                        Model: PreferenceEvidenceModel.ClosedWorld)));
            }
        }

        if (hasBaseline)
        {
            // Guide mappings belong to the plan, not to the physical file.
            // Recompute them against the active immutable plan so a plan
            // change cannot carry an old positive match into a new decision.
            facts.RemoveAll(fact => string.Equals(
                fact.Evidence?.Source,
                "guide-custom-format",
                StringComparison.OrdinalIgnoreCase));
            ApplyProbedFacts(
                facts,
                probedVideoCodec,
                probedAudioCodec,
                probedAudioChannels);
        }
        else
        {
            AddNameFacts(
                facts,
                filePath,
                probedVideoCodec,
                probedAudioCodec,
                probedAudioChannels);
        }

        var matchedFormats = ReleasePreferenceFormatEvidenceFactory.Match(
            plan,
            filePath,
            SelectProfileFormats(profile, customFormats),
            preferencePlan is null ? effectiveGuidePackage : null);
        foreach (var matchedFormat in matchedFormats)
        {
            foreach (var traitId in matchedFormat.TraitIds)
            {
                facts.Add(new PreferenceFact(
                    traitId,
                    PreferenceFactState.Present,
                    new PreferenceEvidence(
                        Source: "guide-custom-format",
                        Confidence: 1,
                        Detail: $"Matched guide rule '{matchedFormat.SourceId}'.",
                        DetectionRule: "guide-custom-format-pattern",
                        DetectionVersion: preferencePlan is null
                            ? $"{effectiveGuidePackage.Version}:{effectiveGuidePackage.Source.UpstreamRevision}"
                            : $"immutable-plan:{plan.Version}:{plan.PlanHash}",
                        Model: PreferenceEvidenceModel.OpenWorld)));
            }
        }
        var evaluation = ReleasePreferenceEvaluator.Evaluate(plan, facts);

        return new PreferenceEvaluationSnapshot(
            MediaId: mediaId?.Trim() ?? string.Empty,
            LibraryId: libraryId?.Trim(),
            FileIdentity: PreferenceFileIdentity.Compute(filePath, fileSizeBytes),
            FilePath: filePath.Trim(),
            FileSizeBytes: fileSizeBytes,
            PlanId: plan.Id,
            PlanVersion: plan.Version,
            PlanHash: plan.PlanHash,
            Facts: facts,
            Evaluation: evaluation,
            MatchedRuleIds: matchedFormats.Select(item => item.RuleId).ToArray(),
            EvaluatedUtc: evaluatedUtc,
            Source: source?.Trim());
    }

    private static IReadOnlyList<CustomFormatItem> SelectProfileFormats(
        QualityProfileItem profile,
        IReadOnlyList<CustomFormatItem>? customFormats)
    {
        if (customFormats is not { Count: > 0 })
        {
            return [];
        }

        var selectedIds = (profile.CustomFormatIds ?? string.Empty)
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return customFormats
            .Where(item => selectedIds.Contains(item.Id))
            .ToArray();
    }

    public static string QualityTraitId(string quality)
        => $"quality.{Slug(quality)}";

    private static void AddNameFacts(
        ICollection<PreferenceFact> facts,
        string filePath,
        string? probedVideoCodec,
        string? probedAudioCodec,
        string? probedAudioChannels)
    {
        var parsed = MediaFileNameFacts.Parse(filePath);
        var videoSource = string.IsNullOrWhiteSpace(probedVideoCodec) ? "file-name" : "media-probe";
        var audioSource = string.IsNullOrWhiteSpace(probedAudioCodec) ? "file-name" : "media-probe";
        var channelsSource = string.IsNullOrWhiteSpace(probedAudioChannels) ? "file-name" : "media-probe";
        Add(facts, "video.codec", probedVideoCodec ?? parsed.VideoCodec, videoSource);

        var audioCodec = probedAudioCodec ?? parsed.AudioCodec;
        if (string.Equals(audioCodec, "Atmos", StringComparison.OrdinalIgnoreCase))
        {
            Add(facts, "audio.object", audioCodec, audioSource);
        }
        else
        {
            Add(facts, "audio.format", audioCodec, audioSource);
        }
        Add(facts, "audio.channels", probedAudioChannels ?? parsed.AudioChannels, channelsSource);
        Add(facts, "source", parsed.Source, "file-name");
        Add(facts, "release-group", parsed.ReleaseGroup, "file-name");
    }

    private static void ApplyProbedFacts(
        ICollection<PreferenceFact> facts,
        string? probedVideoCodec,
        string? probedAudioCodec,
        string? probedAudioChannels)
    {
        ReplaceProbedDimension(facts, "video.codec", probedVideoCodec);
        ReplaceProbedDimension(facts, "audio.channels", probedAudioChannels);

        if (string.IsNullOrWhiteSpace(probedAudioCodec))
        {
            return;
        }

        if (string.Equals(probedAudioCodec, "Atmos", StringComparison.OrdinalIgnoreCase))
        {
            // The probe identifies the object layer but not necessarily the
            // base format. Keep a previously imported TrueHD/format match and
            // replace only the object evidence it can actually measure.
            RemoveDimensionFacts(facts, "audio.object");
            Add(facts, "audio.object", probedAudioCodec, "media-probe");
            return;
        }

        RemoveDimensionFacts(facts, "audio.format");
        RemoveDimensionFacts(facts, "audio.object");
        Add(facts, "audio.format", probedAudioCodec, "media-probe");
    }

    private static void ReplaceProbedDimension(
        ICollection<PreferenceFact> facts,
        string dimension,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        RemoveDimensionFacts(facts, dimension);
        Add(facts, dimension, value, "media-probe");
    }

    private static void RemoveDimensionFacts(
        ICollection<PreferenceFact> facts,
        string dimension)
    {
        if (facts is not List<PreferenceFact> list)
        {
            return;
        }

        list.RemoveAll(fact =>
        {
            if (PreferenceTraitRegistry.Current.TryResolve(fact.TraitId, out var definition))
            {
                return string.Equals(definition.Dimension, dimension, StringComparison.OrdinalIgnoreCase);
            }

            return fact.TraitId.StartsWith(dimension + ".", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static void Add(
        ICollection<PreferenceFact> facts,
        string family,
        string? value,
        string evidenceSource)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var definition = PreferenceTraitRegistry.Current.TryResolveObserved(family, value, out var resolved)
            ? resolved.Id
            : family == "release-group"
                ? "release-group.unclassified"
                : $"{family}.{Slug(value)}";

        facts.Add(new PreferenceFact(
            definition,
            PreferenceFactState.Present,
            new PreferenceEvidence(
                Source: evidenceSource,
                Confidence: string.Equals(evidenceSource, "media-probe", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0.8,
                Detail: evidenceSource == "media-probe"
                    ? $"Read from '{value}' in the media container."
                    : $"Read from '{value}' in the file name.",
                DetectionRule: evidenceSource == "media-probe"
                    ? "media-probe-facts"
                    : "media-file-name-facts",
                DetectionVersion: "v1",
                Model: PreferenceEvidenceModel.OpenWorld)));
    }

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

/// <summary>
/// A cheap identity for an installed file. It intentionally uses path,
/// length, and last-write time rather than hashing the file contents: imports
/// must not read a multi-gigabyte file a second time just to record evidence.
/// Any change in those observable facts causes a new baseline to be written.
/// </summary>
public static class PreferenceFileIdentity
{
    public static string Compute(string filePath, long? fileSizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var normalizedPath = NormalizePath(filePath);
        var lastWriteUtc = TryGetLastWriteUtc(filePath);
        var input = string.Join(
            "|",
            "preference-file/v1",
            normalizedPath,
            fileSizeBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
            lastWriteUtc?.ToString("O") ?? "unknown");
        return "preference-file/v1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static string NormalizePath(string value)
    {
        var path = value.Trim().Replace('\\', '/');
        try
        {
            path = Path.GetFullPath(path).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            // The path is still useful evidence even when a caller is holding
            // a not-yet-mounted or otherwise non-native path.
        }

        return OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path;
    }

    private static DateTimeOffset? TryGetLastWriteUtc(string path)
    {
        try
        {
            return File.Exists(path)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
