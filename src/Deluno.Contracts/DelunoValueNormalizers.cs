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
        => Math.Clamp(value <= 0 ? 24 : value, 1, 8760);

    public static string? NormalizePath(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    /// <summary>
    /// The layouts a shelf can be in.
    ///
    /// <para><b>Grid</b> is artwork and a mark — what you want when you know
    /// the film by its poster. <b>List</b> is a dense table, for file facts.
    /// <b>Overview</b> is the one in between and the one Radarr has that Deluno
    /// did not: a wide row per title, big enough to read the synopsis, which is
    /// how you browse a library you have not seen in a while. A poster grid
    /// cannot answer "what is this one about" and a table has no room to.</para>
    ///
    /// <para>Anything unrecognised falls back to the grid rather than throwing,
    /// because this reads a stored setting and a saved view from a database that
    /// may predate the vocabulary.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> UiViews = ["grid", "list", "overview"];

    public static string NormalizeUiView(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is not null && UiViews.Contains(normalized) ? normalized : "grid";
    }
}
