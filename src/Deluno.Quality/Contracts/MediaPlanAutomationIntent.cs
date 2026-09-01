using System.Text.Json;

namespace Deluno.Quality.Contracts;

/// <summary>
/// Typed, persisted intent for the parts of a Media Plan that are broader
/// than quality and search cadence. These values are recommendations until
/// their owning runtime is explicitly configured; keeping them typed prevents
/// a scenario from hiding executable policy in free-form notes.
/// </summary>
public sealed record MediaPlanAutomationIntent(
    string? ScenarioId = null,
    int? ScenarioVersion = null,
    string? SizeTierId = null,
    string? SizeTierName = null,
    string? SizeDescription = null,
    string? SubtitleIntent = null,
    string? RoutingIntent = null,
    string? SharingIntent = null,
    string? CleanupIntent = null,
    string? NotificationIntent = null,
    string? NamingIntent = null);

public static class MediaPlanAutomationIntentCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static MediaPlanAutomationIntent? Normalize(MediaPlanAutomationIntent? intent)
    {
        if (intent is null)
        {
            return null;
        }

        var normalized = intent with
        {
            ScenarioId = NormalizeSlug(intent.ScenarioId),
            ScenarioVersion = intent.ScenarioVersion is > 0 ? intent.ScenarioVersion : null,
            SizeTierId = NormalizeSlug(intent.SizeTierId),
            SizeTierName = NormalizeText(intent.SizeTierName),
            SizeDescription = NormalizeText(intent.SizeDescription),
            SubtitleIntent = NormalizeText(intent.SubtitleIntent),
            RoutingIntent = NormalizeText(intent.RoutingIntent),
            SharingIntent = NormalizeText(intent.SharingIntent),
            CleanupIntent = NormalizeText(intent.CleanupIntent),
            NotificationIntent = NormalizeText(intent.NotificationIntent),
            NamingIntent = NormalizeText(intent.NamingIntent)
        };

        // An empty object is equivalent to no captured plan intent.
        return IsEmpty(normalized) ? null : normalized;
    }

    public static string? Serialize(MediaPlanAutomationIntent? intent)
    {
        var normalized = Normalize(intent);
        return normalized is null ? null : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static MediaPlanAutomationIntent? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var intent = JsonSerializer.Deserialize<MediaPlanAutomationIntent>(json, JsonOptions)
            ?? throw new JsonException("The media-plan automation intent was empty.");
        return Normalize(intent);
    }

    private static bool IsEmpty(MediaPlanAutomationIntent intent)
        => intent.ScenarioId is null
            && intent.ScenarioVersion is null
            && intent.SizeTierId is null
            && intent.SizeTierName is null
            && intent.SizeDescription is null
            && intent.SubtitleIntent is null
            && intent.RoutingIntent is null
            && intent.SharingIntent is null
            && intent.CleanupIntent is null
            && intent.NotificationIntent is null
            && intent.NamingIntent is null;

    private static string? NormalizeSlug(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
