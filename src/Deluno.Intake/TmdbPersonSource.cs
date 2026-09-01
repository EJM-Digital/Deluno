using System.Text.RegularExpressions;

namespace Deluno.Intake;

/// <summary>
/// The migration-free address format for a TMDb person import source.
///
/// <para>The existing intake schema stores one feed URL. A person source keeps
/// the person URL there and stores the selected Radarr-style credit filters in
/// its <c>credits</c> query parameter, for example
/// <c>https://www.themoviedb.org/person/12345?credits=cast,director</c>.</para>
/// </summary>
public static partial class TmdbPersonSource
{
    public const string Provider = "tmdb-person";
    public const string CreditsParameter = "credits";

    public static IReadOnlyList<string> CreditTypes { get; } =
        ["cast", "director", "producer", "sound", "writing"];

    public static IReadOnlyList<string> DefaultCreditTypes { get; } = ["cast"];

    public static bool TryParse(
        string? address,
        out string personId,
        out IReadOnlyList<string> creditTypes)
    {
        personId = string.Empty;
        creditTypes = DefaultCreditTypes;

        var value = address?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (PersonIdRegex().IsMatch(value))
        {
            personId = value;
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !IsTmdbHost(uri.Host))
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2 ||
            !string.Equals(segments[0], "person", StringComparison.OrdinalIgnoreCase) ||
            !PersonIdRegex().IsMatch(segments[1]))
        {
            return false;
        }

        personId = segments[1];
        creditTypes = ParseCreditTypes(uri.Query);
        return true;
    }

    public static IReadOnlyList<string> ParseCreditTypes(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return DefaultCreditTypes;
        }

        var value = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && string.Equals(parts[0], CreditsParameter, StringComparison.OrdinalIgnoreCase))
            .Select(parts => Uri.UnescapeDataString(parts[1]))
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => value.ToLowerInvariant())
            .Where(CreditTypes.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return value.Length == 0 ? DefaultCreditTypes : value;
    }

    public static string BuildAddress(string personId, IEnumerable<string>? creditTypes)
    {
        var value = personId?.Trim();
        if (!TryParse(value, out var parsedPersonId, out _) &&
            !PersonIdRegex().IsMatch(value ?? string.Empty))
        {
            throw new ArgumentException("A TMDb person ID or person URL is required.", nameof(personId));
        }

        var selected = NormalizeCreditTypes(creditTypes);
        return $"https://www.themoviedb.org/person/{parsedPersonId}?{CreditsParameter}={Uri.EscapeDataString(string.Join(',', selected))}";
    }

    public static IReadOnlyList<string> NormalizeCreditTypes(IEnumerable<string>? creditTypes)
    {
        var selected = (creditTypes ?? [])
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(CreditTypes.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return selected.Length == 0 ? DefaultCreditTypes : selected;
    }

    private static bool IsTmdbHost(string host)
        => string.Equals(host, "themoviedb.org", StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith(".themoviedb.org", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(host, "tmdb.org", StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith(".tmdb.org", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\d{1,12}$")]
    private static partial Regex PersonIdRegex();
}
