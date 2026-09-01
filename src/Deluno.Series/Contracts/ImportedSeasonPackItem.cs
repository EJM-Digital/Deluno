namespace Deluno.Series.Contracts;

/// <summary>
/// A season-pack manifest whose episode identities were resolved before the
/// catalogue write. A release label such as <c>S01</c> is not evidence that
/// every catalogued episode is present; callers must declare the files they
/// actually found, and the repository still verifies those identities against
/// the catalogue inside the import transaction.
/// </summary>
public sealed record ImportedSeasonPackItem(
    int SeasonNumber,
    string? FilePath = null,
    long? FileSizeBytes = null,
    IReadOnlyList<ImportedEpisodeItem>? Episodes = null);
