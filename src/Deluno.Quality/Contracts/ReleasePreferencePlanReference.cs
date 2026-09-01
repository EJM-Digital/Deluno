using System.Text.Json;

namespace Deluno.Quality.Contracts;

/// <summary>
/// The immutable typed release-preference plan that a quality profile was
/// migrated or explicitly compiled from. Keeping the identity, version and
/// hash together prevents a profile from appearing to use a plan whose
/// definition has changed underneath it.
/// </summary>
public sealed record ReleasePreferencePlanReference(
    string PlanId,
    string Version,
    string PlanHash)
{
    public static ReleasePreferencePlanReference? Normalize(ReleasePreferencePlanReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        var planId = reference.PlanId?.Trim() ?? string.Empty;
        var version = reference.Version?.Trim() ?? string.Empty;
        var planHash = reference.PlanHash?.Trim().ToLowerInvariant() ?? string.Empty;
        if (planId.Length == 0 && version.Length == 0 && planHash.Length == 0)
        {
            return null;
        }

        if (planId.Length == 0 || version.Length == 0 || planHash.Length == 0)
        {
            throw new ArgumentException(
                "A release-preference plan reference must include a plan id, version and hash.",
                nameof(reference));
        }

        return new ReleasePreferencePlanReference(planId, version, planHash);
    }
}

public static class ReleasePreferencePlanReferenceCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string? Serialize(ReleasePreferencePlanReference? reference)
    {
        var normalized = ReleasePreferencePlanReference.Normalize(reference);
        return normalized is null ? null : JsonSerializer.Serialize(normalized, Options);
    }

    public static ReleasePreferencePlanReference? Deserialize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var reference = JsonSerializer.Deserialize<ReleasePreferencePlanReference>(value, Options)
            ?? throw new InvalidDataException("The stored release-preference plan reference is empty.");
        return ReleasePreferencePlanReference.Normalize(reference);
    }
}
