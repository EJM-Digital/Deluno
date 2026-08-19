namespace Deluno.Contracts;

/// <summary>
/// Domain vocabulary shared across bounded contexts: what counts as a media
/// type, an audience, a plausible year or rating, a sane sync interval.
///
/// These were private statics on <c>SqlitePlatformSettingsRepository</c>.
/// ADR-001 splits that class up, and Intake, Libraries and Quality all need
/// the same answers, so they live here rather than being copied per context.
/// Import with <c>using static</c> so call sites stay unqualified.
/// </summary>
public static class DelunoValueNormalizers
{
    public static string NormalizeMediaType(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "movies" => "movies",
            "tv" => "tv",
            "tv shows" => "tv",
            "tvshows" => "tv",
            _ => "movies"
        };
    }

    public static string NormalizeAudience(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "kids" => "kids",
            "adult" => "adult",
            _ => "any"
        };
    }

    public static double? NormalizeNullableRating(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return Math.Clamp(value.Value, 0, 10);
    }

    public static int? NormalizeNullableYear(int? value)
        => value is >= 1888 and <= 2100 ? value : null;

    public static int? NormalizeNullablePositiveValue(int? value)
    {
        return value is > 0 ? value.Value : null;
    }

    public static int NormalizePositiveValue(int? value, int fallback)
    {
        return value is > 0 ? value.Value : fallback;
    }

    public static int NormalizeSyncIntervalHours(int? value)
        => NormalizeSyncIntervalHours(value ?? 24);

    public static int NormalizeSyncIntervalHours(int value)
        => Math.Clamp(value <= 0 ? 24 : value, 1, 168);

    public static string? NormalizePath(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string NormalizeUiView(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "list" => "list",
            _ => "grid"
        };
    }
}
