namespace Deluno.Contracts;

/// <summary>
/// Whether a film is obtainable yet, from its release dates and the rule the
/// user chose. Deluno searching before there is anything to find wastes every
/// cycle and fills activity with noise, so this is a real gate, not a label.
/// </summary>
public static class MovieAvailability
{
    public const string Announced = "announced";
    public const string InCinemas = "inCinemas";
    public const string Released = "released";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "announced" => Announced,
        "incinemas" => InCinemas,
        _ => Released
    };

    public static bool IsAvailable(
        string? minimumAvailability,
        DateOnly? inCinemas,
        DateOnly? digital,
        DateOnly? physical,
        DateOnly today)
    {
        switch (Normalize(minimumAvailability))
        {
            case Announced:
                return true;

            case InCinemas:
                // No cinema date on record is not evidence it never reached one,
                // so fall through to the obtainable dates rather than blocking.
                return Reached(inCinemas, today) || Reached(digital, today) || Reached(physical, today)
                       || (inCinemas is null && digital is null && physical is null);

            default:
                var obtainable = Earliest(digital, physical);
                // With no digital or physical date at all, refusing to search would
                // hide older films the provider has no release record for.
                return obtainable is null ? Reached(inCinemas, today) || inCinemas is null : Reached(obtainable, today);
        }
    }

    private static bool Reached(DateOnly? date, DateOnly today) => date is not null && date.Value <= today;

    private static DateOnly? Earliest(DateOnly? left, DateOnly? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left.Value <= right.Value ? left : right;
    }
}
