using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// Which import failure reasons ask a person to look at the file (#1329). The pipeline decides on its own where a recovery case
/// is raised; this table holds the policy's answer for every reason it names, so a reason added or moved without a decision fails here.
/// </summary>
public sealed class ImportFailurePolicyTableTests
{
    [Theory]
    [InlineData(ImportFailurePolicy.Unmatched, true)]
    [InlineData(ImportFailurePolicy.ImportFailed, true)]
    [InlineData(ImportFailurePolicy.Conflict, true)]
    [InlineData(ImportFailurePolicy.ReplacementOwnershipMismatch, true)]
    [InlineData(ImportFailurePolicy.NoVideoStream, false)]
    [InlineData(ImportFailurePolicy.LikelySample, false)]
    [InlineData(ImportFailurePolicy.UnsupportedFile, false)]
    [InlineData(ImportFailurePolicy.MediaProbeRejected, false)]
    [InlineData(ImportFailurePolicy.MediaProbeUnreadable, false)]
    [InlineData(ImportFailurePolicy.WrongRelease, false)]
    [InlineData(ImportFailurePolicy.ReplacementRejected, false)]
    [InlineData(ImportFailurePolicy.ProcessedFileIsLess, false)]
    [InlineData(ImportFailurePolicy.MissingLibraryRoot, false)]
    [InlineData(ImportFailurePolicy.InsufficientFreeSpace, false)]
    [InlineData(ImportFailurePolicy.IoError, false)]
    [InlineData(ImportFailurePolicy.MissingSource, false)]
    [InlineData(ImportFailurePolicy.Permission, false)]
    [InlineData(ImportFailurePolicy.HardlinkUnavailable, false)]
    [InlineData(ImportFailurePolicy.HardlinkFailed, false)]
    [InlineData(ImportFailurePolicy.SamePath, false)]
    [InlineData(ImportFailurePolicy.UnsafeSourcePath, false)]
    [InlineData(ImportFailurePolicy.ProcessorRejected, false)]
    [InlineData(ImportFailurePolicy.DownloadStalled, false)]
    [InlineData(ImportFailurePolicy.ClientReportedFailure, false)]
    [InlineData(ImportFailurePolicy.ManualFailure, false)]
    [InlineData(ImportFailurePolicy.DeadOrBrokenLink, false)]
    [InlineData(ImportFailurePolicy.DownloadIntegrityFailed, false)]
    [InlineData("a reason nobody has heard of", false)]
    public void Only_a_file_Deluno_has_and_cannot_decide_about_alone_asks_a_person_to_look(string reason, bool asksAPerson)
    {
        Assert.Equal(asksAPerson, ImportFailurePolicy.RaisesRecoveryCase(reason));
    }
}
