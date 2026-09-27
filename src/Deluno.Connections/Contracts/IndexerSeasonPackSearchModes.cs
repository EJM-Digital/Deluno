namespace Deluno.Connections.Contracts;

/// <summary>The "both" (default) / "season-packs-only" / "episodes-only" convention for a per-indexer TV search restriction (#743).</summary>
public static class IndexerSeasonPackSearchModes
{
    public const string Both = "both";
    public const string SeasonPacksOnly = "season-packs-only";
    public const string EpisodesOnly = "episodes-only";

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            SeasonPacksOnly => SeasonPacksOnly,
            EpisodesOnly => EpisodesOnly,
            _ => Both
        };
}
