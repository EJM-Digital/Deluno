namespace Deluno.Platform.Contracts;

public static class SearchScoringModes
{
    public const string RulesOnly = "rules-only";
    public const string MlOnly = "ml-only";
    public const string Hybrid = "hybrid";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "rules" or "rules-only" or "traditional" => RulesOnly,
            "ml" or "ml-only" => MlOnly,
            _ => Hybrid
        };
    }

    public static bool IsSupported(string? value)
        => value is not null && Normalize(value) == value.Trim().ToLowerInvariant();
}
