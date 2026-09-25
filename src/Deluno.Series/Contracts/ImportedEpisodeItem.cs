namespace Deluno.Series.Contracts;

public sealed record ImportedEpisodeItem(
    int SeasonNumber,
    int EpisodeNumber,
    bool HasFile,
    string? FilePath = null,
    long? FileSizeBytes = null,
    int? AbsoluteNumber = null,
    DateOnly? AirDate = null,
    int? SceneSeasonNumber = null,
    int? SceneEpisodeNumber = null,
    string? NumberingSource = null,
    /// <summary>
    /// The quality of this episode's own file, when the caller knows it.
    ///
    /// <para>An episode's cutoff is a fact about its file, and it used to be
    /// inherited from the series: one verdict decided for the whole show and
    /// written onto every episode. A show missing sixty-nine of seventy-one
    /// episodes has not met its cutoff — true of the show, false of the two
    /// episodes holding a file at the right quality, which were marked not-met
    /// and looked permanently upgradable.</para>
    ///
    /// <para>Null means the caller does not know, and the series-level answer
    /// is used as before.</para>
    /// </summary>
    string? CurrentQuality = null,

    /// <summary>
    /// Whether this episode's own file meets the cutoff, decided by the caller
    /// against <see cref="CurrentQuality"/>.
    ///
    /// <para>The verdict is made where the library's profile and the quality
    /// model are in scope, not in the repository: storing a row is not the
    /// place to decide policy. Null means the caller did not decide, and the
    /// series-level answer is used as before.</para>
    /// </summary>
    bool? QualityCutoffMet = null,

    /// <summary>
    /// Whether this episode's file came from a season pack rather than its
    /// own single-episode release or an existing-library scan.
    ///
    /// <para>Set by <c>ExpandSeasonPacksAsync</c> on the copies it produces
    /// from a declared season pack, so the wanted reason written for the
    /// episode can say what actually happened ("imported from a season
    /// pack") instead of the one generic sentence every import path used to
    /// share regardless of where the file came from (#654).</para>
    /// </summary>
    bool FromSeasonPack = false);
