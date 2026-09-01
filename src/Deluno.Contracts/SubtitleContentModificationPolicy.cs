using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Contracts;

/// <summary>
/// Safe, deterministic cleanup that may be applied to a subtitle after a
/// provider returns it and before it is written beside the video.
///
/// <para>These are deliberately named transformations rather than a provider
/// score or an opaque command. A subtitle can be made easier to read without
/// changing its timing, and every enabled transformation is visible in the
/// library settings and in the fetch outcome.</para>
/// </summary>
public sealed record SubtitleContentModificationPolicy(
    bool StripHearingImpairedAnnotations = false,
    bool RemoveStyleTags = false,
    bool RemoveEmoji = false,
    bool NormalizeWhitespace = false,
    bool FixAllUppercase = false)
{
    [JsonIgnore]
    public bool IsEnabled =>
        StripHearingImpairedAnnotations
        || RemoveStyleTags
        || RemoveEmoji
        || NormalizeWhitespace
        || FixAllUppercase;
}

/// <summary>Canonical persistence for a subtitle content policy.</summary>
public static class SubtitleContentModificationPolicyCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SubtitleContentModificationPolicy? Normalize(SubtitleContentModificationPolicy? policy)
        => policy is { IsEnabled: true } ? policy : null;

    public static string? Serialize(SubtitleContentModificationPolicy? policy)
    {
        var normalized = Normalize(policy);
        return normalized is null ? null : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static SubtitleContentModificationPolicy? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return Normalize(JsonSerializer.Deserialize<SubtitleContentModificationPolicy>(json, JsonOptions));
    }
}

/// <summary>
/// Automatic subtitle timing-repair policy for one library.
///
/// <para>The threshold is expressed in Deluno's named match ladder rather than
/// Bazarr's arbitrary percentage. A repair can therefore be explained as
/// "below the same source" or "below made for this file" everywhere it is
/// shown.</para>
/// </summary>
public sealed record SubtitleTimingPolicy(
    bool Enabled = true,
    string SyncOnlyBelow = SubtitleSyncThreshold.MadeForThisFile,
    int MaxOffsetSeconds = 60,
    double RequiredPeakSigma = 3.0,
    IReadOnlyList<string>? ExcludedProviders = null)
{
    public bool ShouldSync(SubtitleMatch match)
        => Enabled && (int)match < SubtitleSyncThreshold.ExclusiveRung(SyncOnlyBelow);
}

public static class SubtitleSyncThreshold
{
    public const string SameSource = "same-source";
    public const string MadeForThisFile = "made-for-this-file";

    public static string Normalize(string? value)
        => string.Equals(value?.Trim(), SameSource, StringComparison.OrdinalIgnoreCase)
            ? SameSource
            : MadeForThisFile;

    /// <summary>
    /// The exclusive upper rung: a subtitle below this rung is eligible for
    /// automatic timing repair.
    /// </summary>
    public static int ExclusiveRung(string? value)
        => Normalize(value) == SameSource
            ? (int)SubtitleMatch.SameSource
            : (int)SubtitleMatch.MadeForThisFile;
}

public static class SubtitleTimingPolicyCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SubtitleTimingPolicy? Normalize(SubtitleTimingPolicy? policy)
    {
        if (policy is null)
        {
            return null;
        }

        var providers = (policy.ExcludedProviders ?? [])
            .Where(provider => !string.IsNullOrWhiteSpace(provider))
            .Select(provider => provider.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(provider => provider, StringComparer.Ordinal)
            .ToArray();

        return policy with
        {
            SyncOnlyBelow = SubtitleSyncThreshold.Normalize(policy.SyncOnlyBelow),
            MaxOffsetSeconds = Math.Clamp(policy.MaxOffsetSeconds, 1, 300),
            RequiredPeakSigma = Math.Clamp(policy.RequiredPeakSigma, 1.0, 10.0),
            ExcludedProviders = providers.Length == 0 ? null : providers
        };
    }

    public static string? Serialize(SubtitleTimingPolicy? policy)
    {
        var normalized = Normalize(policy);
        return normalized is null ? null : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static SubtitleTimingPolicy? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return Normalize(JsonSerializer.Deserialize<SubtitleTimingPolicy>(json, JsonOptions));
    }
}
