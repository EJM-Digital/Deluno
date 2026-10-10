using Deluno.Jobs.Contracts;

namespace Deluno.Jobs;

/// <summary>
/// Whether a send ended badly, the same reading Activity's Failed count takes (#1218): the send, its grab or its import
/// failed, and it has not been archived (dismissed or settled). A send still going, waiting or finished is not one, so there is nothing to dismiss.
/// </summary>
public static class DispatchFailure
{
    public static bool IsFailed(DownloadDispatchItem dispatch)
        => !Is(dispatch.Status, "archived")
            && (Is(dispatch.Status, "failed", "importfailed")
            || Is(dispatch.GrabStatus, "failed")
            || Is(dispatch.ImportStatus, "failed", "importfailed"));

    private static bool Is(string? value, params string[] any)
        => value is not null && any.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
}
